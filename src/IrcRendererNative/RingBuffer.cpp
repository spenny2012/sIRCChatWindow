#include "pch.h"
#include "RingBuffer.h"

#include <new>

namespace
{
    // Records start 4-aligned so the Segment array (alignof 4) inside each
    // record is always safely addressable.
    inline uint32_t AlignUp(uint32_t v) noexcept
    {
        return (v + 3u) & ~3u;
    }
}

RingBuffer::RingBuffer()
{
    // Reserve-only: commit charge (private bytes) accrues in 1 MiB steps as
    // scrollback actually grows, instead of 16 MiB up front. The meta ring is
    // likewise allocated lazily (EnsureMeta), sized to the cap in effect.
    m_arena = static_cast<char*>(::VirtualAlloc(nullptr, IrcArenaBytes,
        MEM_RESERVE, PAGE_READWRITE));
}

bool RingBuffer::EnsureMeta() noexcept
{
    if (m_meta)
        return true;
    m_meta.reset(new(std::nothrow) LineMeta[m_maxLines]);
    if (!m_meta)
        return false;
    m_metaCapacity = m_maxLines;
    return true;
}

RingBuffer::~RingBuffer()
{
    if (m_arena)
        ::VirtualFree(m_arena, 0, MEM_RELEASE);
}

bool RingBuffer::EnsureCommitted(uint32_t end) noexcept
{
    if (end <= m_committedBytes)
        return true;
    if (!m_arena || end > IrcArenaBytes)
        return false;

    constexpr uint32_t CommitChunk = 1024 * 1024;
    uint32_t target = ((end + CommitChunk - 1) / CommitChunk) * CommitChunk;
    if (target > IrcArenaBytes)
        target = IrcArenaBytes;

    if (!::VirtualAlloc(m_arena + m_committedBytes, target - m_committedBytes,
            MEM_COMMIT, PAGE_READWRITE))
        return false;

    m_committedBytes = target;
    return true;
}

uint32_t RingBuffer::EvictOldest() noexcept
{
    const uint32_t rows = m_meta[m_head].rowCount;
    m_head = (m_head + 1) % m_metaCapacity;
    --m_count;
    ++m_evictedTotal;
    if (m_count == 0)
        m_writeOffset = 0;
    return rows;
}

uint32_t RingBuffer::Append(const LineSlot& slot) noexcept
{
    // Allocation failure (OOM-level) drops the line, same as commit failure.
    if (!EnsureMeta())
        return slot.rowCount;

    const uint32_t segBytes = static_cast<uint32_t>(slot.segmentCount) * sizeof(Segment);
    const uint32_t recordBytes = AlignUp(segBytes + slot.length);

    uint32_t evictedRows = 0;
    uint32_t start = 0;
    for (;;)
    {
        if (m_count == 0)
        {
            m_writeOffset = 0;
            break;
        }
        if (m_count >= m_maxLines)
        {
            evictedRows += EvictOldest();
            continue;
        }

        const uint32_t oldest = m_meta[m_head].arenaOffset;
        if (oldest <= m_writeOffset)
        {
            // Live bytes are contiguous [oldest, writeOffset): free space is
            // the tail of the arena, then everything below the oldest record.
            if (m_writeOffset + recordBytes <= IrcArenaBytes)
            {
                start = m_writeOffset;
                break;
            }
            if (recordBytes < oldest)
            {
                // Wrap; the dead tail gap is reclaimed as those lines evict.
                start = 0;
                break;
            }
        }
        else
        {
            // Live bytes wrap around the end: only [writeOffset, oldest) is
            // free. Strict inequality keeps writeOffset from landing exactly
            // on the oldest record, which would be indistinguishable from the
            // contiguous layout above.
            if (m_writeOffset + recordBytes < oldest)
            {
                start = m_writeOffset;
                break;
            }
        }
        evictedRows += EvictOldest();
    }

    // Commit failure (OOM-level) drops the line; counting its rows as
    // "evicted" keeps the caller's m_totalRows bookkeeping consistent.
    if (!EnsureCommitted(start + recordBytes))
        return evictedRows + slot.rowCount;

    char* dst = m_arena + start;
    std::memcpy(dst, slot.segments, segBytes);
    std::memcpy(dst + segBytes, slot.text, slot.length);

    LineMeta& meta = m_meta[(m_head + m_count) % m_metaCapacity];
    meta.arenaOffset = start;
    meta.timestamp = static_cast<uint32_t>(GetTickCount64());
    meta.length = slot.length;
    meta.rowCount = slot.rowCount;
    meta.segmentCount = slot.segmentCount;
    meta.flags = slot.flags;
    ++m_count;

    m_writeOffset = start + recordBytes;
    return evictedRows;
}

uint32_t RingBuffer::SetMaxLines(uint32_t maxLines) noexcept
{
    if (maxLines < 1)
        maxLines = 1;
    if (maxLines > IrcLineCapacity)
        maxLines = IrcLineCapacity;
    m_maxLines = maxLines;

    uint32_t evictedRows = 0;
    while (m_count > m_maxLines)
        evictedRows += EvictOldest();

    // Right-size the meta ring so a small cap costs small memory (~16 B per
    // line). Unallocated meta stays lazy — EnsureMeta sizes it to the cap in
    // effect at first Append. On allocation failure, keep the old ring.
    if (m_meta && m_maxLines != m_metaCapacity)
    {
        LineMeta* next = new(std::nothrow) LineMeta[m_maxLines];
        if (next)
        {
            for (uint32_t i = 0; i < m_count; ++i)
                next[i] = m_meta[(m_head + i) % m_metaCapacity];
            m_meta.reset(next);
            m_metaCapacity = m_maxLines;
            m_head = 0;
        }
    }
    return evictedRows;
}

void RingBuffer::TrimStorage() noexcept
{
    if (!m_arena || m_committedBytes == 0)
        return;

    if (m_count == 0)
    {
        m_writeOffset = 0;
        ::VirtualFree(m_arena, m_committedBytes, MEM_DECOMMIT);
        m_committedBytes = 0;
        return;
    }

    const uint32_t oldest = m_meta[m_head].arenaOffset;
    if (oldest < m_writeOffset)
    {
        // Contiguous span [oldest, writeOffset): slide it to the arena start
        // in place. dest < src: memmove handles the overlap; then rebase
        // every offset.
        const uint32_t spanBytes = m_writeOffset - oldest;
        if (oldest > 0)
        {
            std::memmove(m_arena, m_arena + oldest, spanBytes);
            for (uint32_t i = 0; i < m_count; ++i)
                m_meta[(m_head + i) % m_metaCapacity].arenaOffset -= oldest;
            m_writeOffset = spanBytes;
        }
    }
    else
    {
        // Wrapped (or ambiguous equal-offset) span: repack every live record
        // in logical order through a temp buffer, since the destination
        // overlaps both fragments. Live bytes are bounded by the line cap, so
        // this is at most a few MB; on allocation failure keep the current
        // layout (same outcome as the old unconditional no-op).
        uint32_t packedBytes = 0;
        for (uint32_t i = 0; i < m_count; ++i)
        {
            const LineMeta& meta = m_meta[(m_head + i) % m_metaCapacity];
            packedBytes += AlignUp(
                static_cast<uint32_t>(meta.segmentCount) * sizeof(Segment) + meta.length);
        }

        std::unique_ptr<char[]> temp(new(std::nothrow) char[packedBytes]);
        if (!temp)
            return;

        uint32_t dst = 0;
        for (uint32_t i = 0; i < m_count; ++i)
        {
            LineMeta& meta = m_meta[(m_head + i) % m_metaCapacity];
            const uint32_t recordBytes = AlignUp(
                static_cast<uint32_t>(meta.segmentCount) * sizeof(Segment) + meta.length);
            std::memcpy(temp.get() + dst, m_arena + meta.arenaOffset, recordBytes);
            meta.arenaOffset = dst;
            dst += recordBytes;
        }

        std::memcpy(m_arena, temp.get(), packedBytes);
        m_writeOffset = packedBytes;
    }

    constexpr uint32_t Page = 4096;
    const uint32_t keep = ((m_writeOffset + Page - 1) / Page) * Page;
    if (keep < m_committedBytes)
    {
        ::VirtualFree(m_arena + keep, m_committedBytes - keep, MEM_DECOMMIT);
        m_committedBytes = keep;
    }
}

void RingBuffer::Clear() noexcept
{
    m_head = 0;
    m_count = 0;
    m_writeOffset = 0;
    m_evictedTotal = 0;

    // Return the high-water commit to the OS; EnsureCommitted re-grows on
    // demand and nothing relies on arena contents surviving a Clear.
    // (SetMaxLines shrink deliberately doesn't decommit: live records wrap
    // arbitrarily around the arena, so no contiguous range is reclaimable.)
    if (m_arena && m_committedBytes)
    {
        ::VirtualFree(m_arena, m_committedBytes, MEM_DECOMMIT);
        m_committedBytes = 0;
    }
}

LineView RingBuffer::Get(uint32_t logicalIndex) const noexcept
{
    LineView view = {};
    if (logicalIndex >= m_count)
        return view;

    const LineMeta& meta = m_meta[(m_head + logicalIndex) % m_metaCapacity];
    const char* base = m_arena + meta.arenaOffset;
    view.segments = reinterpret_cast<const Segment*>(base);
    view.text = base + static_cast<uint32_t>(meta.segmentCount) * sizeof(Segment);
    view.length = meta.length;
    view.rowCount = meta.rowCount;
    view.segmentCount = meta.segmentCount;
    view.flags = meta.flags;
    return view;
}

void RingBuffer::SetRowCount(uint32_t logicalIndex, uint16_t rows) noexcept
{
    if (logicalIndex < m_count)
        m_meta[(m_head + logicalIndex) % m_metaCapacity].rowCount = rows;
}

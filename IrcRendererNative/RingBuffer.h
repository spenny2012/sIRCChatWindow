#pragma once

#include "pch.h"

constexpr uint32_t IrcLineCapacity = 50000;
constexpr uint32_t IrcLineTextSize = 512;
constexpr uint32_t IrcMaxSegments = 16;

// Byte budget for packed line storage. Typical traffic (~200 B/line) hits the
// 50k line cap well under this; worst-case max-length lines retain ~24k lines
// before the byte budget evicts.
constexpr uint32_t IrcArenaBytes = 16 * 1024 * 1024;

struct Segment
{
    uint32_t fg;       // 0xAARRGGBB resolved color; 0 = default (renderer fg brush)
    uint32_t bg;       // 0xAARRGGBB resolved color; 0 = default (no bg fill)
    uint16_t offset;   // byte offset into the line text where this segment starts
    uint8_t  flags;    // bit 0 = bold, bit 1 = italic, bit 2 = underline
    uint8_t  reserved; // zero; keeps arena record bytes deterministic
};

static_assert(sizeof(Segment) == 12, "Segment layout is the arena record format");

// Per-line flags (LineSlot/LineMeta/LineView). NonAscii gates the UTF-8
// cluster-aware layout paths; pure-ASCII lines keep the byte==cell fast path.
constexpr uint8_t LineFlagNonAscii = 0x01;

// Ingest scratch: IrcParser::Parse fills one of these on the stack, then
// RingBuffer::Append packs the used portion into the arena. Never stored.
struct LineSlot
{
    char     text[IrcLineTextSize];
    Segment  segments[IrcMaxSegments];
    uint32_t timestamp;
    uint16_t length;
    uint16_t rowCount;      // wrapped visual rows at the current column width
    uint8_t  segmentCount;
    uint8_t  flags;         // LineFlag* bits
};

static_assert(sizeof(LineSlot) <= 720, "LineSlot should stay compact");

// Read-only view of a stored line. Pointers reference the arena and stay valid
// only until the next Append/Clear (single-threaded: render thread only).
struct LineView
{
    const char*    text;
    const Segment* segments;
    uint16_t       length;
    uint16_t       rowCount;
    uint8_t        segmentCount;
    uint8_t        flags;
};

// Scrollback storage: fixed per-line metadata ring plus a byte arena holding
// variable-size records ([segments][text], 4-aligned). Bounded by whichever
// runs out first — IrcLineCapacity lines or IrcArenaBytes of packed data.
// All access is single-threaded (the render thread ingests and draws).
class RingBuffer
{
public:
    RingBuffer();
    ~RingBuffer();

    RingBuffer(const RingBuffer&) = delete;
    RingBuffer& operator=(const RingBuffer&) = delete;

    // Pack the scratch line into the arena, evicting oldest lines when the
    // meta ring or the byte budget is exhausted. Returns the total wrapped
    // rows of the evicted lines so the caller can maintain its row total.
    uint32_t Append(const LineSlot& slot) noexcept;

    void Clear() noexcept;

    uint32_t Count() const noexcept { return m_count; }
    uint32_t Capacity() const noexcept { return IrcLineCapacity; }

    // Lines evicted since construction/Clear. Absolute line id =
    // EvictedTotal() + logicalIndex; stable across eviction, so selection
    // anchors survive scrollback churn.
    uint64_t EvictedTotal() const noexcept { return m_evictedTotal; }

    // logicalIndex 0 = oldest, Count()-1 = newest. Caller keeps index < Count().
    LineView Get(uint32_t logicalIndex) const noexcept;
    void SetRowCount(uint32_t logicalIndex, uint16_t rows) noexcept;

private:
    struct LineMeta
    {
        uint32_t arenaOffset;
        uint32_t timestamp;
        uint16_t length;
        uint16_t rowCount;
        uint8_t  segmentCount;
        uint8_t  flags;
    };

    // Drops the oldest line; returns its rowCount.
    uint32_t EvictOldest() noexcept;

    // Commits reserved arena pages up to `end` bytes. Returns false only if
    // the OS refuses the commit (the caller then drops the line).
    bool EnsureCommitted(uint32_t end) noexcept;

    std::unique_ptr<LineMeta[]> m_meta;
    char*                       m_arena = nullptr; // VirtualAlloc reserve; committed on demand
    uint32_t                    m_committedBytes = 0;
    uint32_t                    m_head = 0;        // meta index of oldest line
    uint32_t                    m_count = 0;
    uint32_t                    m_writeOffset = 0; // next arena byte to write
    uint64_t                    m_evictedTotal = 0;
};

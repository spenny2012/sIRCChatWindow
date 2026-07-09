#pragma once

#include "pch.h"

// Simple lock-free bounded MPSC queue using sequence counters.
// Capacity must be a power of two.
template <size_t Capacity, size_t SlotSize = IrcLineTextSize>
class MpscQueue
{
    static_assert((Capacity & (Capacity - 1)) == 0, "Capacity must be a power of two");

    struct Slot
    {
        std::atomic<size_t> sequence;
        char                data[SlotSize];
        uint16_t            length;
    };

public:
    MpscQueue()
        : m_buffer(new Slot[Capacity])
    {
        for (size_t i = 0; i < Capacity; ++i)
        {
            m_buffer[i].sequence.store(i, std::memory_order_relaxed);
            m_buffer[i].length = 0;
        }
        m_head.store(0, std::memory_order_relaxed);
        m_tail.store(0, std::memory_order_relaxed);
    }

    ~MpscQueue() = default;

    MpscQueue(const MpscQueue&) = delete;
    MpscQueue& operator=(const MpscQueue&) = delete;

    // Enqueue a line. Returns false if the queue is full.
    bool Enqueue(const char* data, uint16_t length) noexcept
    {
        if (length > SlotSize)
            length = static_cast<uint16_t>(SlotSize);

        Slot* slot = nullptr;
        size_t seq = 0;
        size_t tail = m_tail.load(std::memory_order_relaxed);
        for (;;)
        {
            slot = &m_buffer[tail & (Capacity - 1)];
            seq = slot->sequence.load(std::memory_order_acquire);
            intptr_t diff = static_cast<intptr_t>(seq) - static_cast<intptr_t>(tail);
            if (diff == 0)
            {
                if (m_tail.compare_exchange_weak(tail, tail + 1, std::memory_order_relaxed))
                    break;
            }
            else if (diff < 0)
            {
                return false; // full
            }
            else
            {
                tail = m_tail.load(std::memory_order_relaxed);
            }
        }

        std::memcpy(slot->data, data, length);
        slot->length = length;
        slot->sequence.store(tail + 1, std::memory_order_release);
        return true;
    }

    // Dequeue a line. Returns false if empty.
    bool Dequeue(char* outData, uint16_t& outLength) noexcept
    {
        Slot* slot = &m_buffer[m_head & (Capacity - 1)];
        size_t seq = slot->sequence.load(std::memory_order_acquire);
        size_t head = m_head.load(std::memory_order_relaxed);
        intptr_t diff = static_cast<intptr_t>(seq) - static_cast<intptr_t>(head + 1);
        if (diff == 0)
        {
            uint16_t len = slot->length;
            std::memcpy(outData, slot->data, len);
            outLength = len;
            slot->sequence.store(head + Capacity, std::memory_order_release);
            m_head.store(head + 1, std::memory_order_release);
            return true;
        }
        return false;
    }

private:
    std::unique_ptr<Slot[]> m_buffer;
    std::atomic<size_t>     m_head{ 0 };
    std::atomic<size_t>     m_tail{ 0 };
};

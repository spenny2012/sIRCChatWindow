#pragma once

#include "pch.h"
#include "RingBuffer.h"

namespace IrcParser
{
    constexpr uint8_t ColorDefault = 99;

    // Parse raw IRC text (with mIRC control codes) directly into a LineSlot.
    // No allocations, no exceptions.
    void Parse(LineSlot* slot, const char* text, uint16_t length) noexcept;
}

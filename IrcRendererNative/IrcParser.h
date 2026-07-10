#pragma once

#include "pch.h"
#include "RingBuffer.h"

namespace IrcParser
{
    // Parse raw IRC text (mIRC control codes and ANSI escape sequences)
    // directly into a LineSlot. Control bytes are stripped from the stored
    // text; formatting is resolved to packed colors in the segments.
    // No allocations, no exceptions.
    void Parse(LineSlot* slot, const char* text, uint16_t length) noexcept;
}

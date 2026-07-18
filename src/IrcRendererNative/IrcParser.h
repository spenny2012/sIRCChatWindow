#pragma once

#include "pch.h"
#include "RingBuffer.h"

namespace IrcParser
{
    // Parse raw IRC text (mIRC control codes and ANSI escape sequences)
    // directly into a LineSlot. Control bytes are stripped from the stored
    // text; formatting is resolved to packed colors in the segments.
    // defaultFg/defaultBg (0xAARRGGBB) substitute for "no explicit color" in
    // reverse-video swaps, so they bake the theme current at parse time.
    // No allocations, no exceptions.
    void Parse(LineSlot* slot, const char* text, uint16_t length,
        uint32_t defaultFg, uint32_t defaultBg) noexcept;
}

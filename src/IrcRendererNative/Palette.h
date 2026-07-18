#pragma once

#include "pch.h"

// Static color tables for resolving parsed color codes (mIRC indices, xterm
// 256-color, SGR truecolor) to packed 0xAARRGGBB at parse time. Alpha 0 is
// the "no explicit color" sentinel: the renderer substitutes its default fg
// brush and skips the bg fill.
namespace IrcPalette
{
    constexpr uint32_t Default   = 0u;
    constexpr uint32_t DefaultFg = 0xFFF2F2F2u; // keep in sync with the renderer's default fg brush (0.95)
    constexpr uint32_t DefaultBg = 0xFF141414u; // keep in sync with the renderer's clear color (0.08)
    constexpr uint32_t DefaultSelection = 0x59598CF2u; // translucent blue (0.35,0.55,0.95 @ alpha 0.35)

    constexpr uint32_t Pack(uint8_t r, uint8_t g, uint8_t b) noexcept
    {
        return 0xFF000000u | (static_cast<uint32_t>(r) << 16) |
               (static_cast<uint32_t>(g) << 8) | b;
    }

    // mIRC color index 0-98 (16-98 is the modern-IRC extended palette);
    // anything else returns Default.
    uint32_t Mirc(uint8_t index) noexcept;

    // xterm 256-color palette: 16 base + 6x6x6 cube + 24-step grayscale.
    uint32_t Xterm(uint8_t index) noexcept;
}

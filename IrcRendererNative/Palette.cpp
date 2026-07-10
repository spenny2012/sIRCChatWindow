#include "pch.h"
#include "Palette.h"

#include <array>

namespace
{
    // 0-15 match the classic mIRC colors the renderer's brush table used;
    // 16-98 is the standardized extended palette (modern.ircdocs.horse).
    constexpr uint32_t g_mirc[99] = {
        0xFFFFFFFFu, 0xFF000000u, 0xFF000080u, 0xFF008000u, // 0-3
        0xFFFF0000u, 0xFF800000u, 0xFF800080u, 0xFFFF8000u, // 4-7
        0xFFFFFF00u, 0xFF00FF00u, 0xFF008080u, 0xFF00FFFFu, // 8-11
        0xFF0000FFu, 0xFFFF00FFu, 0xFF808080u, 0xFFBFBFBFu, // 12-15
        0xFF470000u, 0xFF472100u, 0xFF474700u, 0xFF324700u, 0xFF004700u, 0xFF00472Cu,
        0xFF004747u, 0xFF002747u, 0xFF000047u, 0xFF2E0047u, 0xFF470047u, 0xFF47002Au, // 16-27
        0xFF740000u, 0xFF743A00u, 0xFF747400u, 0xFF517400u, 0xFF007400u, 0xFF007449u,
        0xFF007474u, 0xFF004074u, 0xFF000074u, 0xFF4B0074u, 0xFF740074u, 0xFF740045u, // 28-39
        0xFFB50000u, 0xFFB56300u, 0xFFB5B500u, 0xFF7DB500u, 0xFF00B500u, 0xFF00B571u,
        0xFF00B5B5u, 0xFF0063B5u, 0xFF0000B5u, 0xFF7500B5u, 0xFFB500B5u, 0xFFB5006Bu, // 40-51
        0xFFFF0000u, 0xFFFF8C00u, 0xFFFFFF00u, 0xFFB2FF00u, 0xFF00FF00u, 0xFF00FFA0u,
        0xFF00FFFFu, 0xFF008CFFu, 0xFF0000FFu, 0xFFA500FFu, 0xFFFF00FFu, 0xFFFF0098u, // 52-63
        0xFFFF5959u, 0xFFFFB459u, 0xFFFFFF71u, 0xFFCFFF60u, 0xFF6FFF6Fu, 0xFF65FFC9u,
        0xFF6DFFFFu, 0xFF59B4FFu, 0xFF5959FFu, 0xFFC459FFu, 0xFFFF66FFu, 0xFFFF59BCu, // 64-75
        0xFFFF9C9Cu, 0xFFFFD39Cu, 0xFFFFFF9Cu, 0xFFE2FF9Cu, 0xFF9CFF9Cu, 0xFF9CFFDBu,
        0xFF9CFFFFu, 0xFF9CD3FFu, 0xFF9C9CFFu, 0xFFDC9CFFu, 0xFFFF9CFFu, 0xFFFF94D3u, // 76-87
        0xFF000000u, 0xFF131313u, 0xFF282828u, 0xFF363636u, 0xFF4D4D4Du, 0xFF656565u,
        0xFF818181u, 0xFF9F9F9Fu, 0xFFBCBCBCu, 0xFFE2E2E2u, 0xFFFFFFFFu               // 88-98
    };

    constexpr std::array<uint32_t, 256> BuildXterm() noexcept
    {
        std::array<uint32_t, 256> t{};
        constexpr uint32_t base[16] = {
            0xFF000000u, 0xFF800000u, 0xFF008000u, 0xFF808000u,
            0xFF000080u, 0xFF800080u, 0xFF008080u, 0xFFC0C0C0u,
            0xFF808080u, 0xFFFF0000u, 0xFF00FF00u, 0xFFFFFF00u,
            0xFF0000FFu, 0xFFFF00FFu, 0xFF00FFFFu, 0xFFFFFFFFu
        };
        for (int i = 0; i < 16; ++i)
            t[i] = base[i];
        for (int i = 16; i < 232; ++i)
        {
            const int n = i - 16;
            const int levels[3] = { n / 36, (n / 6) % 6, n % 6 };
            uint8_t rgb[3] = {};
            for (int c = 0; c < 3; ++c)
                rgb[c] = static_cast<uint8_t>(levels[c] == 0 ? 0 : 55 + 40 * levels[c]);
            t[i] = IrcPalette::Pack(rgb[0], rgb[1], rgb[2]);
        }
        for (int i = 232; i < 256; ++i)
        {
            const uint8_t v = static_cast<uint8_t>(8 + 10 * (i - 232));
            t[i] = IrcPalette::Pack(v, v, v);
        }
        return t;
    }

    constexpr std::array<uint32_t, 256> g_xterm = BuildXterm();
}

uint32_t IrcPalette::Mirc(uint8_t index) noexcept
{
    return index < 99 ? g_mirc[index] : Default;
}

uint32_t IrcPalette::Xterm(uint8_t index) noexcept
{
    return g_xterm[index];
}

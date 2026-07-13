#include "pch.h"
#include "IrcParser.h"
#include "Palette.h"

namespace
{
    // ParseColor's "no color digits followed the code" result (mIRC index
    // space; 99 is also the spec's explicit "default" index).
    constexpr uint8_t MircIndexDefault = 99;

    // Current style while scanning a line. mIRC codes and ANSI SGR sequences
    // mutate the same state, so the two systems interleave freely.
    struct Style
    {
        uint32_t fg = IrcPalette::Default;
        uint32_t bg = IrcPalette::Default;
        uint8_t  flags = 0;       // bit 0 bold, bit 1 italic, bit 2 underline
        uint8_t  fgBase = 0xFF;   // 0-7 when fg came from SGR 30-37 (bold-brighten tracking)
        bool     reverse = false; // SGR 7/27
    };

    inline bool IsDigit(char c) noexcept
    {
        return c >= '0' && c <= '9';
    }

    inline uint8_t ParseColor(const char* text, uint16_t& pos, uint16_t length) noexcept
    {
        if (pos >= length || !IsDigit(text[pos]))
            return MircIndexDefault;

        uint8_t value = static_cast<uint8_t>(text[pos++] - '0');
        if (pos < length && IsDigit(text[pos]))
        {
            value = static_cast<uint8_t>(value * 10 + (text[pos++] - '0'));
        }
        return value;
    }

    inline void FlushSegment(LineSlot* slot, uint16_t start, uint16_t end,
        const Style& st, uint32_t defaultFg, uint32_t defaultBg) noexcept
    {
        if (end <= start || slot->segmentCount >= IrcMaxSegments)
            return;

        // Reverse video swaps effective colors at emission, substituting the
        // concrete default colors so "default reversed" still swaps visibly.
        // Base state stays untouched, so SGR 27 restores it exactly. The
        // defaults are the theme at parse time; a later theme change does not
        // rewrite already-parsed reversed spans.
        uint32_t fg = st.fg;
        uint32_t bg = st.bg;
        if (st.reverse)
        {
            fg = st.bg ? st.bg : defaultBg;
            bg = st.fg ? st.fg : defaultFg;
        }

        Segment& seg = slot->segments[slot->segmentCount++];
        seg.fg = fg;
        seg.bg = bg;
        seg.offset = start;
        seg.flags = st.flags;
        seg.reserved = 0;
    }

    // Applies one SGR parameter list (ESC[...m). Indexed loop so 38/48 can
    // consume their extended-color arguments. Unsupported codes (2, 5, 9,
    // 21, ...) are ignored by design.
    void ApplySgr(const uint16_t* params, uint8_t count, Style& st) noexcept
    {
        const auto clampByte = [](uint16_t v) noexcept {
            return static_cast<uint8_t>(v > 255 ? 255 : v);
        };

        for (uint8_t k = 0; k < count; ++k)
        {
            const uint16_t p = params[k];
            switch (p)
            {
            case 0:  st = Style{}; break;
            case 1:
                // Bold also brightens a basic 30-37 foreground, matching
                // terminal/irssi behavior in either code order.
                st.flags |= 0x01;
                if (st.fgBase < 8) st.fg = IrcPalette::Xterm(st.fgBase + 8);
                break;
            case 3:  st.flags |= 0x02; break;
            case 4:  st.flags |= 0x04; break;
            case 7:  st.reverse = true; break;
            case 22:
                st.flags &= static_cast<uint8_t>(~0x01);
                if (st.fgBase < 8) st.fg = IrcPalette::Xterm(st.fgBase);
                break;
            case 23: st.flags &= static_cast<uint8_t>(~0x02); break;
            case 24: st.flags &= static_cast<uint8_t>(~0x04); break;
            case 27: st.reverse = false; break;
            case 39: st.fg = IrcPalette::Default; st.fgBase = 0xFF; break;
            case 49: st.bg = IrcPalette::Default; break;
            case 38:
            case 48:
            {
                // Extended color: 38/48 ; 5 ; N or 38/48 ; 2 ; R ; G ; B.
                // Missing arguments or an unknown submode abandons the rest
                // of the list (the sequence text is already stripped).
                if (k + 1 >= count)
                    return;
                uint32_t color;
                const uint16_t mode = params[++k];
                if (mode == 5)
                {
                    if (k + 1 >= count)
                        return;
                    color = IrcPalette::Xterm(clampByte(params[++k]));
                }
                else if (mode == 2)
                {
                    if (k + 3 >= count)
                        return;
                    color = IrcPalette::Pack(clampByte(params[k + 1]),
                        clampByte(params[k + 2]), clampByte(params[k + 3]));
                    k += 3;
                }
                else
                {
                    return;
                }
                if (p == 38) { st.fg = color; st.fgBase = 0xFF; }
                else st.bg = color;
                break;
            }
            default:
                if (p >= 30 && p <= 37)
                {
                    st.fgBase = static_cast<uint8_t>(p - 30);
                    st.fg = IrcPalette::Xterm((st.flags & 0x01)
                        ? static_cast<uint8_t>(st.fgBase + 8) : st.fgBase);
                }
                else if (p >= 40 && p <= 47)
                {
                    st.bg = IrcPalette::Xterm(static_cast<uint8_t>(p - 40));
                }
                else if (p >= 90 && p <= 97)
                {
                    st.fg = IrcPalette::Xterm(static_cast<uint8_t>(p - 90 + 8));
                    st.fgBase = 0xFF;
                }
                else if (p >= 100 && p <= 107)
                {
                    st.bg = IrcPalette::Xterm(static_cast<uint8_t>(p - 100 + 8));
                }
                break;
            }
        }
    }

    // Consumes an escape sequence starting at text[i] == ESC, leaving i past
    // every byte consumed. Consumed bytes are never copied into the stored
    // text, so malformed or unsupported sequences are stripped, not rendered.
    void ConsumeEscape(const char* text, uint16_t& i, uint16_t length, Style& st) noexcept
    {
        ++i; // skip ESC
        if (i >= length)
            return; // bare ESC at end of line: stripped

        char c = text[i];
        if (c == ']')
        {
            // OSC: consume to BEL (or end of line).
            while (i < length && text[i] != '\x07')
                ++i;
            if (i < length)
                ++i;
            return;
        }
        if (c != '[')
        {
            // Two-byte escape; charset selects carry one extra byte.
            ++i;
            if ((c == '(' || c == ')' || c == '#') && i < length)
                ++i;
            return;
        }

        ++i; // skip '['
        uint16_t params[16];
        uint8_t count = 0;
        uint32_t cur = 0;
        while (i < length)
        {
            c = text[i];
            if (IsDigit(c))
            {
                cur = cur * 10 + static_cast<uint32_t>(c - '0');
                if (cur > 65535) cur = 65535;
                ++i;
            }
            else if (c == ';' || c == ':') // ':' = ITU sub-parameter separator
            {
                if (count < 16) params[count++] = static_cast<uint16_t>(cur);
                cur = 0;
                ++i;
            }
            else if (c >= 0x20 && c <= 0x3F)
            {
                ++i; // other parameter/intermediate bytes
            }
            else if (c >= 0x40 && c <= 0x7E)
            {
                ++i; // final byte: SGR applies, other CSI sequences are ignored
                if (c == 'm')
                {
                    // Trailing value (or the implicit 0 of a bare "ESC[m").
                    if (count < 16) params[count++] = static_cast<uint16_t>(cur);
                    ApplySgr(params, count, st);
                }
                return;
            }
            else
            {
                return; // control/high byte: abandon, main loop handles it
            }
        }
        // End of line before the final byte (including 512-byte truncation
        // mid-sequence): the partial sequence is stripped, no state change.
    }
}

void IrcParser::Parse(LineSlot* slot, const char* text, uint16_t length,
    uint32_t defaultFg, uint32_t defaultBg) noexcept
{
    Style st;
    uint16_t segmentStart = 0;
    uint16_t i = 0;
    unsigned char high = 0; // any stored byte >= 0x80 marks the line non-ASCII

    while (i < length)
    {
        const char c = text[i];
        if (c == '\x02')
        {
            FlushSegment(slot, segmentStart, slot->length, st, defaultFg, defaultBg);
            st.flags ^= 0x01; // toggle bold
            segmentStart = slot->length;
            ++i;
            continue;
        }
        if (c == '\x16')
        {
            FlushSegment(slot, segmentStart, slot->length, st, defaultFg, defaultBg);
            st.flags ^= 0x02; // toggle italic
            segmentStart = slot->length;
            ++i;
            continue;
        }
        if (c == '\x1F')
        {
            FlushSegment(slot, segmentStart, slot->length, st, defaultFg, defaultBg);
            st.flags ^= 0x04; // toggle underline
            segmentStart = slot->length;
            ++i;
            continue;
        }
        if (c == '\x0F')
        {
            FlushSegment(slot, segmentStart, slot->length, st, defaultFg, defaultBg);
            st = Style{};
            segmentStart = slot->length;
            ++i;
            continue;
        }
        if (c == '\x03')
        {
            FlushSegment(slot, segmentStart, slot->length, st, defaultFg, defaultBg);
            ++i;
            const uint8_t newFg = ParseColor(text, i, length);
            uint8_t newBg = MircIndexDefault;
            if (i < length && text[i] == ',')
            {
                ++i;
                newBg = ParseColor(text, i, length);
            }
            // Bare \x03 (or index 99) resets fg but leaves bg untouched.
            st.fg = (newFg != MircIndexDefault) ? IrcPalette::Mirc(newFg)
                                                : IrcPalette::Default;
            st.fgBase = 0xFF;
            if (newBg != MircIndexDefault)
                st.bg = IrcPalette::Mirc(newBg);
            segmentStart = slot->length;
            continue;
        }
        if (c == '\x1B')
        {
            FlushSegment(slot, segmentStart, slot->length, st, defaultFg, defaultBg);
            ConsumeEscape(text, i, length, st);
            segmentStart = slot->length;
            continue;
        }

        if (slot->length < IrcLineTextSize)
        {
            slot->text[slot->length++] = c;
            high |= static_cast<unsigned char>(c);
        }
        ++i;
    }

    FlushSegment(slot, segmentStart, slot->length, st, defaultFg, defaultBg);
    slot->flags = (high & 0x80) ? LineFlagNonAscii : 0; // assign: scratch slot is reused
}

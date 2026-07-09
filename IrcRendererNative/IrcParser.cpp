#include "pch.h"
#include "IrcParser.h"

namespace
{
    inline bool IsDigit(char c) noexcept
    {
        return c >= '0' && c <= '9';
    }

    inline uint8_t ParseColor(const char* text, uint16_t& pos, uint16_t length) noexcept
    {
        if (pos >= length || !IsDigit(text[pos]))
            return IrcParser::ColorDefault;

        uint8_t value = static_cast<uint8_t>(text[pos++] - '0');
        if (pos < length && IsDigit(text[pos]))
        {
            value = static_cast<uint8_t>(value * 10 + (text[pos++] - '0'));
        }
        return value;
    }

    inline void FlushSegment(LineSlot* slot, uint16_t start, uint16_t end,
        uint8_t fg, uint8_t bg, uint8_t flags) noexcept
    {
        if (end <= start || slot->segmentCount >= IrcMaxSegments)
            return;

        Segment& seg = slot->segments[slot->segmentCount++];
        seg.offset = start;
        seg.fg = fg;
        seg.bg = bg;
        seg.flags = flags;
    }
}

void IrcParser::Parse(LineSlot* slot, const char* text, uint16_t length) noexcept
{
    uint8_t fg = ColorDefault;
    uint8_t bg = ColorDefault;
    uint8_t flags = 0;
    uint16_t segmentStart = 0;
    uint16_t i = 0;

    while (i < length)
    {
        const char c = text[i];
        if (c == '\x02')
        {
            FlushSegment(slot, segmentStart, slot->length, fg, bg, flags);
            flags ^= 0x01; // toggle bold
            segmentStart = slot->length;
            ++i;
            continue;
        }
        if (c == '\x16')
        {
            FlushSegment(slot, segmentStart, slot->length, fg, bg, flags);
            flags ^= 0x02; // toggle italic
            segmentStart = slot->length;
            ++i;
            continue;
        }
        if (c == '\x1F')
        {
            FlushSegment(slot, segmentStart, slot->length, fg, bg, flags);
            flags ^= 0x04; // toggle underline
            segmentStart = slot->length;
            ++i;
            continue;
        }
        if (c == '\x0F')
        {
            FlushSegment(slot, segmentStart, slot->length, fg, bg, flags);
            fg = ColorDefault;
            bg = ColorDefault;
            flags = 0;
            segmentStart = slot->length;
            ++i;
            continue;
        }
        if (c == '\x03')
        {
            FlushSegment(slot, segmentStart, slot->length, fg, bg, flags);
            ++i;
            uint8_t newFg = ParseColor(text, i, length);
            uint8_t newBg = ColorDefault;
            if (i < length && text[i] == ',')
            {
                ++i;
                newBg = ParseColor(text, i, length);
            }
            if (newFg != ColorDefault) fg = newFg;
            else fg = ColorDefault;
            if (newBg != ColorDefault) bg = newBg;
            segmentStart = slot->length;
            continue;
        }

        if (slot->length < IrcLineTextSize)
        {
            slot->text[slot->length++] = c;
        }
        ++i;
    }

    FlushSegment(slot, segmentStart, slot->length, fg, bg, flags);
}

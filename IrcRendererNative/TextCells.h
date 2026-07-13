#pragma once

#include "pch.h"

// UTF-8 cluster walking and terminal-style cell widths for the fixed-column
// layout model. Each display cluster occupies 0, 1, or 2 monospace cells:
// emoji and CJK are 2, combining marks / ZWJ / variation selectors are 0,
// everything else is 1. This is wcwidth-lite, NOT UAX#11/UTS#51-complete:
//  - U+2600-27BF / U+2B00-2BFF count 1 cell unless VS16 (U+FE0F) follows;
//    their wide fallback glyphs may overhang the neighboring cell.
//  - ZWJ sequences occupy 2 cells regardless of the rendered glyph's width.
//  - No UAX#29 grapheme engine: bidi and Indic composition are out of scope;
//    zero-width folding covers the common combining blocks only.
// All functions are allocation-free, exception-free, and start with an ASCII
// short-circuit so pure-ASCII text never touches the range table.
namespace TextCells
{
    struct Utf8Cp
    {
        uint32_t cp;
        uint16_t bytes; // always >= 1 (progress guarantee)
    };

    struct Cluster
    {
        uint16_t bytes;
        uint8_t  cells;
    };

    struct CellRange
    {
        uint32_t first;
        uint32_t last;
        uint8_t  cells;
    };

    // Sorted by first; anything not listed is 1 cell. Code points below
    // 0x0300 never reach the table.
    inline constexpr CellRange kRanges[] = {
        { 0x0300,  0x036F,  0 }, // combining diacritical marks
        { 0x1100,  0x115F,  2 }, // Hangul jamo (leading)
        { 0x1AB0,  0x1AFF,  0 }, // combining extended
        { 0x1DC0,  0x1DFF,  0 }, // combining supplement
        { 0x200B,  0x200F,  0 }, // ZWSP, ZWNJ, ZWJ, LRM, RLM
        { 0x20D0,  0x20FF,  0 }, // combining marks for symbols (keycap U+20E3)
        { 0x2E80,  0x303E,  2 }, // CJK radicals, kana punctuation
        { 0x3041,  0x33FF,  2 }, // hiragana, katakana, CJK compat
        { 0x3400,  0x4DBF,  2 }, // CJK ext A
        { 0x4E00,  0x9FFF,  2 }, // CJK unified ideographs
        { 0xA000,  0xA4CF,  2 }, // Yi
        { 0xAC00,  0xD7A3,  2 }, // Hangul syllables
        { 0xF900,  0xFAFF,  2 }, // CJK compat ideographs
        { 0xFE00,  0xFE0F,  0 }, // variation selectors (FE0E=VS15, FE0F=VS16)
        { 0xFE20,  0xFE2E,  0 }, // combining half marks
        { 0xFE30,  0xFE4F,  2 }, // CJK compat forms
        { 0xFF00,  0xFF60,  2 }, // fullwidth forms
        { 0xFFE0,  0xFFE6,  2 }, // fullwidth signs
        { 0x1F300, 0x1F5FF, 2 }, // misc symbols & pictographs (incl. skin tones)
        { 0x1F600, 0x1F64F, 2 }, // emoticons
        { 0x1F680, 0x1F6FF, 2 }, // transport & map
        { 0x1F900, 0x1F9FF, 2 }, // supplemental symbols & pictographs
        { 0x1FA70, 0x1FAFF, 2 }, // symbols & pictographs extended-A
        { 0x20000, 0x2FFFD, 2 }, // CJK ext B-F
        { 0x30000, 0x3FFFD, 2 }, // CJK ext G
        { 0xE0100, 0xE01EF, 0 }, // variation selectors supplement
    };

    // Decodes the code point at text[i]. Invalid leads, stray continuation
    // bytes, and sequences truncated by the buffer or a formatting split all
    // decode as U+FFFD consuming exactly 1 byte. No overlong/surrogate
    // validation: input is display-only, not a security boundary.
    inline Utf8Cp DecodeUtf8(const char* text, uint16_t i, uint16_t length) noexcept
    {
        const unsigned char b0 = static_cast<unsigned char>(text[i]);
        if (b0 < 0x80)
            return { b0, 1 };

        uint32_t cp;
        uint16_t need;
        if ((b0 & 0xE0) == 0xC0)      { cp = b0 & 0x1Fu; need = 1; }
        else if ((b0 & 0xF0) == 0xE0) { cp = b0 & 0x0Fu; need = 2; }
        else if ((b0 & 0xF8) == 0xF0) { cp = b0 & 0x07u; need = 3; }
        else                          return { 0xFFFD, 1 };

        if (static_cast<uint16_t>(length - i) <= need)
            return { 0xFFFD, 1 };

        for (uint16_t k = 1; k <= need; ++k)
        {
            const unsigned char b = static_cast<unsigned char>(text[i + k]);
            if ((b & 0xC0) != 0x80)
                return { 0xFFFD, 1 };
            cp = (cp << 6) | (b & 0x3Fu);
        }
        return { cp, static_cast<uint16_t>(1 + need) };
    }

    inline int CellWidth(uint32_t cp) noexcept
    {
        if (cp < 0x0300)
            return 1;

        int lo = 0;
        int hi = static_cast<int>(sizeof(kRanges) / sizeof(kRanges[0])) - 1;
        while (lo <= hi)
        {
            const int mid = (lo + hi) / 2;
            if (cp < kRanges[mid].first)
                hi = mid - 1;
            else if (cp > kRanges[mid].last)
                lo = mid + 1;
            else
                return kRanges[mid].cells;
        }
        return 1;
    }

    // One display cluster starting at text[i]: base code point plus trailing
    // ZWJ-joined code points, VS15/16 (VS16 promotes to 2 cells), skin-tone
    // modifiers, combining marks; a regional-indicator pair folds into one
    // 2-cell flag cluster. An ASCII base folds followers too (keycap "1"+VS16
    // +U+20E3), but the common all-ASCII case exits on the first two checks.
    inline Cluster NextCluster(const char* text, uint16_t i, uint16_t length) noexcept
    {
        const unsigned char b0 = static_cast<unsigned char>(text[i]);
        uint32_t baseCp;
        int cells;
        uint16_t bytes;
        if (b0 < 0x80)
        {
            if (static_cast<uint16_t>(i + 1) >= length ||
                !(static_cast<unsigned char>(text[i + 1]) & 0x80))
                return { 1, 1 };
            baseCp = b0;
            cells = 1;
            bytes = 1;
        }
        else
        {
            const Utf8Cp base = DecodeUtf8(text, i, length);
            baseCp = base.cp;
            cells = CellWidth(base.cp);
            bytes = base.bytes;
        }

        const bool baseRI = baseCp >= 0x1F1E6 && baseCp <= 0x1F1FF;
        bool riFolded = false;
        while (static_cast<uint16_t>(i + bytes) < length)
        {
            const Utf8Cp next = DecodeUtf8(text, static_cast<uint16_t>(i + bytes), length);
            if (next.cp == 0x200D) // ZWJ: consume it and the joined code point
            {
                const uint16_t after = static_cast<uint16_t>(i + bytes + next.bytes);
                bytes = static_cast<uint16_t>(bytes + next.bytes);
                if (after >= length)
                    break;
                bytes = static_cast<uint16_t>(bytes + DecodeUtf8(text, after, length).bytes);
                continue;
            }
            if (next.cp == 0xFE0F) // VS16: emoji presentation
            {
                bytes = static_cast<uint16_t>(bytes + next.bytes);
                if (cells < 2) cells = 2;
                continue;
            }
            if (next.cp >= 0x1F3FB && next.cp <= 0x1F3FF) // skin tone
            {
                bytes = static_cast<uint16_t>(bytes + next.bytes);
                continue;
            }
            if (baseRI && !riFolded && next.cp >= 0x1F1E6 && next.cp <= 0x1F1FF)
            {
                bytes = static_cast<uint16_t>(bytes + next.bytes);
                cells = 2;
                riFolded = true;
                continue;
            }
            if (CellWidth(next.cp) == 0) // VS15, combining marks, ZW chars
            {
                bytes = static_cast<uint16_t>(bytes + next.bytes);
                continue;
            }
            break;
        }
        return { bytes, static_cast<uint8_t>(cells) };
    }

    // Sum of cluster cells over [start, end). Pure-ASCII ranges return
    // end - start without touching the cluster walk.
    inline int CountCells(const char* text, uint16_t start, uint16_t end) noexcept
    {
        uint16_t i = start;
        while (i < end && !(static_cast<unsigned char>(text[i]) & 0x80))
            ++i;
        if (i == end)
            return end - start;

        // Mixed content: cluster-walk from the start so ASCII-base folding
        // (e.g. keycap sequences) agrees with the wrap/draw walks.
        int cells = 0;
        i = start;
        while (i < end)
        {
            const Cluster cl = NextCluster(text, i, end);
            cells += cl.cells;
            i = static_cast<uint16_t>(i + cl.bytes);
        }
        return cells;
    }

    // Encodes one cluster's bytes to UTF-16. Caller guarantees room: n UTF-8
    // bytes always produce <= n UTF-16 units. Returns units written.
    inline uint16_t Utf8ToUtf16(const char* text, uint16_t bytes, wchar_t* out) noexcept
    {
        uint16_t n = 0;
        uint16_t i = 0;
        while (i < bytes)
        {
            const Utf8Cp u = DecodeUtf8(text, i, bytes);
            if (u.cp >= 0x10000)
            {
                const uint32_t v = u.cp - 0x10000;
                out[n++] = static_cast<wchar_t>(0xD800 + (v >> 10));
                out[n++] = static_cast<wchar_t>(0xDC00 + (v & 0x3FF));
            }
            else
            {
                out[n++] = static_cast<wchar_t>(u.cp);
            }
            i = static_cast<uint16_t>(i + u.bytes);
        }
        return n;
    }
}

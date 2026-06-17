// SPDX-License-Identifier: GPL-2.0-or-later
//
// Baudot / ITA2 (US-TTY variant) code tables. Five data bits select a glyph in
// one of two shift planes — LETTERS or FIGURES — toggled by the FIGS (0x1B) and
// LTRS (0x1F) shift codes. Used by the RTTY demodulator (decode) and, later,
// the transmitter (encode). Copyright (C) 2026 contributors.

namespace Openhpsdr.Zeus.Samples.Rtty;

internal static class Baudot
{
    public const int Figs = 0x1B; // shift to FIGURES plane
    public const int Ltrs = 0x1F; // shift to LETTERS plane
    public const int Space = 0x04;

    // Index = 5-bit code (0..31). '\0' marks a control/shift slot with no glyph
    // (callers handle CR/LF/space/shift explicitly). US-TTY figures layout;
    // slot 5 (FIGS) is BELL.
    private static readonly char[] LettersPlane =
    {
        '\0', 'E', '\n', 'A', ' ', 'S', 'I', 'U',
        '\r', 'D', 'R', 'J', 'N', 'F', 'C', 'K',
        'T', 'Z', 'L', 'W', 'H', 'Y', 'P', 'Q',
        'O', 'B', 'G', '\0', 'M', 'X', 'V', '\0',
    };

    private static readonly char[] FiguresPlane =
    {
        '\0', '3', '\n', '-', ' ', '\u0007', '8', '7',
        '\r', '$', '4', '\'', ',', '!', ':', '(',
        '5', '"', ')', '2', '#', '6', '0', '1',
        '9', '?', '&', '\0', '.', '/', ';', '\0',
    };

    /// <summary>Decode a 5-bit code in the given shift plane. Returns '\0' for
    /// shift codes and unassigned slots (the caller acts on those).</summary>
    public static char Decode(int code, bool figures)
        => (figures ? FiguresPlane : LettersPlane)[code & 0x1F];

    /// <summary>Encode a character to its 5-bit code and the plane it lives in.
    /// Letters are normalised to upper case (Baudot has no case). Returns false
    /// for characters with no Baudot representation.</summary>
    public static bool TryEncode(char c, out int code, out bool figures)
    {
        c = char.ToUpperInvariant(c);
        for (int i = 0; i < 32; i++)
        {
            if (i is Figs or Ltrs) continue;
            if (LettersPlane[i] == c) { code = i; figures = false; return true; }
            if (FiguresPlane[i] == c) { code = i; figures = true; return true; }
        }
        code = 0; figures = false; return false;
    }
}

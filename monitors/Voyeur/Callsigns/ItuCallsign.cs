// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see ATTRIBUTIONS.md for provenance.

using System.Text.RegularExpressions;

namespace Zeus.Server.Voyeur;

/// <summary>
/// Pure ITU amateur-callsign structure validation for Voyeur Mode callsign
/// accuracy (zeus-la5). This exists because the original
/// <see cref="CallsignExtractor"/> anchor required a LEADING [A-Z], so every
/// digit-first ITU prefix (2E0, 4X1, 9A1, 3D2, 7Q7, 1A0…) failed to validate
/// and could never be attributed. The structure model here accepts the three
/// real prefix shapes:
///
///   • 1–2 letters            — W, KB, VE, EI, OK   (→ W1AW, KB2UKA, VE3XYZ)
///   • letter + digit         — A4, E7, B7          (→ A45XYZ, E73AB)
///   • DIGIT + letter         — 2E, 4X, 9A, 3D, 7Q  (→ 2E0XYZ, 9A1A, 7Q7AB)
///
/// followed by the call's number digit and a 1–4 letter suffix. Portable
/// constructs (<c>CALL/P</c>, <c>CALL/MM</c>, <c>DL/CALL</c>, <c>9A/OK1XYZ</c>)
/// are handled by <see cref="IsValid"/>, which strips known modifiers / location
/// prefixes and validates the embedded base call. No DXCC database is consulted —
/// this is a SHAPE check (the matcher and QRZ do the rest), so it never rejects a
/// rare-but-well-formed call.
/// </summary>
public static partial class ItuCallsign
{
    // Core bare-call shape (no slashes). Prefix = 1–2 letters | letter+digit |
    // digit+letter; then the number digit; then a 1–4 letter suffix.
    [GeneratedRegex(@"^([A-Z]{1,2}|[A-Z][0-9]|[0-9][A-Z])[0-9][A-Z]{1,4}$")]
    private static partial Regex BaseRegex();

    // A location / reciprocal prefix segment in a portable call (the part before
    // the home call in DL/KB2UKA, or 9A/OK1XYZ, or VP2/CALL). Letters with an
    // optional trailing 1–2 digits, a digit+letter ITU prefix, or a bare
    // reciprocal digit.
    [GeneratedRegex(@"^([A-Z]{1,3}[0-9]{0,2}|[0-9][A-Z][0-9]?|[0-9]{1,2})$")]
    private static partial Regex PrefixRegex();

    // Operating modifiers that may follow a call after a slash. Case-folded to
    // upper before the test.
    private static readonly HashSet<string> Modifiers = new(StringComparer.Ordinal)
    {
        "P",    // portable
        "M",    // mobile
        "MM",   // maritime mobile
        "AM",   // aeronautical mobile
        "A",    // alternate / secondary location (some admins)
        "R",    // repeater / reciprocal marker
        "QRP",  // low power
        "QRPP", // very low power
        "LH",   // lighthouse activation
        "B",    // beacon
    };

    /// <summary>
    /// True when <paramref name="call"/> is a structurally valid amateur call,
    /// including portable forms (<c>CALL/P</c>, <c>DL/CALL</c>, <c>9A/CALL/P</c>).
    /// Pure; never throws; null/blank → false.
    /// </summary>
    public static bool IsValid(string? call)
    {
        if (string.IsNullOrWhiteSpace(call)) return false;
        var t = call.Trim().ToUpperInvariant();

        if (!t.Contains('/')) return IsValidBase(t);

        var segments = t.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return false;

        bool sawBase = false;
        foreach (var seg in segments)
        {
            if (IsValidBase(seg))
            {
                sawBase = true;
                continue;
            }
            // Non-base segments must be either a known modifier or a plausible
            // location/reciprocal prefix; anything else is garbage.
            if (Modifiers.Contains(seg)) continue;
            if (PrefixRegex().IsMatch(seg)) continue;
            return false;
        }
        return sawBase;
    }

    /// <summary>
    /// True when <paramref name="call"/> is a valid BARE call (no slashes, no
    /// modifiers). This is the digit-first-aware shape gate.
    /// </summary>
    public static bool IsValidBase(string? call)
    {
        if (string.IsNullOrWhiteSpace(call)) return false;
        return BaseRegex().IsMatch(call.Trim().ToUpperInvariant());
    }

    /// <summary>
    /// Resolve the "home" base call out of a (possibly portable) call — e.g.
    /// <c>DL/KB2UKA/P</c> → <c>KB2UKA</c>. When several segments are valid bases
    /// the LONGEST wins (the home call is longer than a location prefix). Returns
    /// the upper-cased base, or null when no segment is a valid base call.
    /// </summary>
    public static string? BaseCall(string? call)
    {
        if (string.IsNullOrWhiteSpace(call)) return null;
        var t = call.Trim().ToUpperInvariant();
        if (!t.Contains('/'))
            return IsValidBase(t) ? t : null;

        string? best = null;
        foreach (var seg in t.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!IsValidBase(seg)) continue;
            if (best is null || seg.Length > best.Length) best = seg;
        }
        return best;
    }

    /// <summary>True when the call's prefix begins with a digit (2E0, 4X1, 9A1,
    /// 3D2, 7Q7…) — the class the old anchor regex silently dropped.</summary>
    public static bool HasDigitFirstPrefix(string? call)
    {
        var b = BaseCall(call);
        return b is not null && b.Length > 0 && char.IsDigit(b[0]);
    }
}

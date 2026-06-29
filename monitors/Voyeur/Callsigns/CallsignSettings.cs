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

namespace Zeus.Server.Voyeur;

/// <summary>
/// Settings for the Voyeur Mode callsign-accuracy stage (zeus-la5). Kept in its
/// OWN record so it composes additively into the plugin's persisted settings
/// without colliding with other modules' fields. All values are conservative
/// defaults that keep the existing behaviour working with no download or setup.
/// </summary>
public sealed record CallsignSettings
{
    /// <summary>
    /// When true (default), a decoded callsign is snapped to the closest known
    /// call in the live "likely on frequency" set (session roster + QRZ-confirmed
    /// + provided spots) via <see cref="CallsignMatcher"/>. When false the decode
    /// is taken verbatim (only structural validation is applied).
    /// </summary>
    public bool FuzzyMatchToRoster { get; init; } = true;

    /// <summary>
    /// Beam width for both the whisper decode (passed to <c>--beam-size</c>) and
    /// the roster fuzzy-match (max top-ranked roster entries considered before
    /// gating). Larger = more accurate, slightly slower.
    ///
    /// DEFAULT 0 = greedy: when 0, the whisper-cli command is BYTE-IDENTICAL to
    /// today (no <c>--beam-size</c> is added), so the proven default decode path
    /// is unchanged. A positive value (e.g. 8) is OPT-IN. 0 or negative also
    /// means "no cap" on the roster fuzzy-match scan.
    /// </summary>
    public int BeamSize { get; init; } = 0;

    /// <summary>
    /// When true, the transcriber requests per-token probabilities (<c>-oj</c>)
    /// so a confidence can be attached to each attribution. Purely a data-capture
    /// toggle — it never changes which call is chosen. DEFAULT false: confidence
    /// capture is deferred/off so the default decode path is unchanged.
    /// </summary>
    public bool CaptureConfidence { get; init; } = false;
}

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
/// Tunables for Voyeur "over" segmentation. Persisted via the plugin settings
/// store (<c>IPluginSettings</c>) and passed to the drain loop on session start.
/// Owned by the segmentation module so its fields never collide with the STT /
/// summarizer settings records.
///
/// Defaults encode the fixes made to the original pure energy gate:
///  • <see cref="OpenMarginDb"/> lowered from 8 dB → 6 dB so weak DX overs that
///    sit only ~6 dB above the floor still open (paired with an N-block debounce
///    in the segmenter so the lower threshold doesn't chatter on noise).
///  • <see cref="HangSeconds"/> trimmed from 1.2 s → 1.0 s; the trailing hang is
///    also excluded from the SAVED audio span so whisper stops hallucinating on
///    the silent tail.
///  • <see cref="PreRollMs"/> = 500 ms of pre-speech audio is prepended on open
///    so the leading callsign is not clipped.
///  • <see cref="MaxOverSeconds"/> caps a single over so a stuck carrier / open
///    mic can't grow one unbounded WAV.
///
/// The energy gate is the always-on, no-download FLOOR. <see cref="UseVad"/> is
/// an opt-in enhancement (Silero VAD via the sherpa-onnx CLI) that refines the
/// per-over boundaries when the engine + model are installed, and degrades
/// silently back to the energy gate when they are absent.
/// </summary>
public sealed record SegSettings
{
    /// <summary>Opt in to Silero-VAD boundary refinement (default OFF — the
    /// energy gate is the floor and needs no download).</summary>
    public bool UseVad { get; init; } = false;

    /// <summary>RMS must rise this many dB above the tracked noise floor to open
    /// an over (default 6 dB; debounced over N blocks in the segmenter).</summary>
    public double OpenMarginDb { get; init; } = 6.0;

    /// <summary>Quiet duration that ends an over. Excluded from the saved audio
    /// span (default 1.0 s).</summary>
    public double HangSeconds { get; init; } = 1.0;

    /// <summary>Milliseconds of pre-speech audio prepended on open so the leading
    /// callsign / attack is preserved (default 500 ms).</summary>
    public int PreRollMs { get; init; } = 500;

    /// <summary>Hard cap on a single over's length — a stuck carrier can't grow
    /// one unbounded WAV (default 300 s).</summary>
    public double MaxOverSeconds { get; init; } = 300.0;
}

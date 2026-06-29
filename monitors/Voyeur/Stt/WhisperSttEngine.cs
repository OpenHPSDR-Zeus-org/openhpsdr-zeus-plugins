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

using Microsoft.Extensions.Logging;

namespace Zeus.Server.Voyeur;

/// <summary>
/// The LOCKED-DEFAULT STT engine, adapting the existing, battle-tested
/// <see cref="WhisperTranscriber"/> to the <see cref="ISttEngine"/> seam. This is
/// a THIN WRAPPER — it does NOT re-implement whisper; it delegates to the proven
/// <see cref="WhisperTranscriber.TranscribeAsync"/> (supervised whisper-cli child
/// process, 16 kHz resample, hard timeout) and maps its <c>string?</c> result
/// into an <see cref="SttResult"/>.
///
/// Hotwords: whisper.cpp has no <c>--hotwords-file</c>, but it accepts a decoding
/// <c>--prompt</c>, and <see cref="WhisperTranscriber"/> already passes a static
/// ham-radio prompt that biases toward phonetics / Q-codes / callsign shapes —
/// covering the same ground as Parakeet's (unavailable) hotwords. We additionally
/// COMPOSE a dynamic prompt from <see cref="SttOptions.Hotwords"/> (see
/// <see cref="ComposePrompt"/>, unit-tested). Feeding that dynamic prompt into
/// whisper requires a one-line optional prompt overload on
/// <see cref="WhisperTranscriber"/> (an integrator change — this wrapper must not
/// modify that shared file); until then the static ham prompt is used and the
/// composed prompt is surfaced for diagnostics. Callsign biasing is in any case
/// applied downstream in <see cref="CallsignExtractor"/>.
/// </summary>
public sealed class WhisperSttEngine : ISttEngine
{
    private readonly WhisperTranscriber _whisper;
    private readonly ILogger _log;

    public WhisperSttEngine(WhisperTranscriber whisper, ILogger log)
    {
        _whisper = whisper;
        _log = log;
    }

    public SttEngineKind Kind => SttEngineKind.Whisper;

    public bool Available => _whisper.Available;

    public async Task<SttResult> TranscribeAsync(string wavPath16k, SttOptions opt, CancellationToken ct)
    {
        if (!_whisper.Available) return SttResult.NotInstalled("Whisper");

        if (opt.Hotwords.Count > 0 && _log.IsEnabled(LogLevel.Debug))
            _log.LogDebug("voyeur.whisper hotword-bias prompt={Prompt}", ComposePrompt(opt.Hotwords));

        // Feed the contextual phrases (the confirmed session roster) through to
        // whisper as a dynamic prompt suffix. Empty ⇒ byte-identical default path.
        var text = await _whisper.TranscribeAsync(wavPath16k, opt.EffectiveTimeout, ct, opt.Hotwords);
        return string.IsNullOrWhiteSpace(text)
            ? SttResult.NoSpeech()
            : new SttResult(text, EngineAvailable: true);
    }

    /// <summary>
    /// Build a whisper decoding-prompt fragment biasing toward the given
    /// contextual phrases (callsigns / Q-codes / custom phonetics). Pure +
    /// unit-tested. De-duplicates (ordinal, case-insensitive), preserves first-
    /// seen order, drops blanks, and returns "" for an empty list.
    /// </summary>
    public static string ComposePrompt(IReadOnlyList<string> hotwords)
    {
        if (hotwords.Count == 0) return "";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<string>();
        foreach (var raw in hotwords)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var w = raw.Trim();
            if (seen.Add(w)) kept.Add(w);
        }
        if (kept.Count == 0) return "";
        return "Likely callsigns and terms: " + string.Join(", ", kept) + ".";
    }
}

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
/// Which speech-to-text backend transcribes a captured "over". Whisper is the
/// LOCKED DEFAULT (the existing, battle-tested <see cref="WhisperTranscriber"/>);
/// Parakeet is an opt-in alternative (NVIDIA Parakeet-TDT via the sherpa-onnx
/// CLI — see <see cref="SherpaParakeetTranscriber"/>). Both engines SHELL OUT to
/// a supervised native child process for crash isolation; neither ever
/// P/Invokes, so a segfault in native ASR code can only kill the child.
/// </summary>
public enum SttEngineKind
{
    Whisper = 0,
    Parakeet = 1,
}

/// <summary>
/// Per-transcription request knobs, shared across every <see cref="ISttEngine"/>.
/// Engine-agnostic by design: an engine ignores fields it can't honour (e.g.
/// Parakeet-TDT cannot use <see cref="Hotwords"/> — sherpa-onnx only accepts
/// hotwords with modified_beam_search, which the NeMo-transducer impl rejects —
/// so the Parakeet engine builds the file but does not pass it; Whisper folds
/// the hotwords into its decoding prompt instead).
/// </summary>
/// <param name="Hotwords">Contextual-bias phrases (callsigns / Q-codes / custom
/// phonetics). May be empty.</param>
/// <param name="Provider">onnxruntime execution provider for sherpa
/// (<c>cpu</c> | <c>cuda</c> | <c>coreml</c> | <c>directml</c>). The CPU floor
/// (<c>cpu</c>) MUST work everywhere, including Raspberry Pi. Ignored by
/// Whisper.</param>
/// <param name="Threads">Worker threads for the engine. Clamped to a sane floor.</param>
/// <param name="HotwordsScore">Global boost applied to hotword phrases when an
/// engine supports them.</param>
/// <param name="Timeout">Hard wall-clock cap; a child that runs past this is
/// killed and the over is dropped. (Carried here so the engine signature stays
/// <c>(wav, opt, ct)</c>.)</param>
public sealed record SttOptions(
    IReadOnlyList<string> Hotwords,
    string Provider = "cpu",
    int Threads = 4,
    float HotwordsScore = 2.0f,
    TimeSpan? Timeout = null)
{
    /// <summary>Defaults: no hotwords, cpu provider, 4 threads, 120 s cap.</summary>
    public static SttOptions Default { get; } = new(Array.Empty<string>());

    /// <summary>Effective timeout, defaulting to 120 s when unset.</summary>
    public TimeSpan EffectiveTimeout =>
        Timeout is { } t && t > TimeSpan.Zero ? t : TimeSpan.FromSeconds(120);

    /// <summary>Threads clamped to at least 1.</summary>
    public int EffectiveThreads => Threads < 1 ? 1 : Threads;
}

/// <summary>One decoded token with an OPTIONAL confidence. Confidence is
/// <see cref="float.NaN"/> when the engine does not report one (greedy NeMo
/// transducer decoding emits tokens without per-token probabilities).</summary>
public sealed record SttToken(string Text, float Confidence);

/// <summary>
/// Result of a transcription attempt. NEVER an exception — engines fail soft:
/// a missing binary/model yields <see cref="EngineAvailable"/> = false; a quiet
/// or garbled over yields a null/empty <see cref="Text"/> with
/// <see cref="EngineAvailable"/> = true.
/// </summary>
public sealed record SttResult(
    string? Text,
    bool EngineAvailable,
    IReadOnlyList<SttToken>? Tokens = null,
    string? Error = null)
{
    /// <summary>True when usable transcript text was produced.</summary>
    public bool HasText => !string.IsNullOrWhiteSpace(Text);

    /// <summary>Engine binary/model not installed — capture-only.</summary>
    public static SttResult NotInstalled(string engine) =>
        new(null, EngineAvailable: false, Error: $"{engine} engine not installed");

    /// <summary>Engine ran but produced no speech.</summary>
    public static SttResult NoSpeech() => new(null, EngineAvailable: true);
}

/// <summary>
/// A pluggable speech-to-text backend. The single seam Voyeur uses so the
/// transcription pipeline is agnostic to Whisper vs Parakeet. Implementations
/// MUST be safe to call from the single transcription worker, MUST NOT throw
/// into the caller, and MUST bound themselves by <see cref="SttOptions.Timeout"/>.
/// </summary>
public interface ISttEngine
{
    /// <summary>Which backend this is.</summary>
    SttEngineKind Kind { get; }

    /// <summary>True when both the engine binary and its model are present right
    /// now (re-resolved on each check, so a button-install is picked up without a
    /// restart — same discipline as <see cref="WhisperTranscriber"/>).</summary>
    bool Available { get; }

    /// <summary>
    /// Transcribe a captured over. <paramref name="wavPath16k"/> may be at any
    /// rate — engines down-convert to 16 kHz mono internally via
    /// <see cref="WhisperWav.Prepare"/> (idempotent when already 16 kHz). Returns
    /// a soft-failing <see cref="SttResult"/>; never throws, never blocks past
    /// <see cref="SttOptions.Timeout"/>.
    /// </summary>
    Task<SttResult> TranscribeAsync(string wavPath16k, SttOptions opt, CancellationToken ct);
}

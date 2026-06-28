// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus WAV Recorder plugin — extracted from Zeus core
// (Zeus.Server.Hosting/Wav/*). GPL-2.0-or-later, same as the rest of Zeus.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

namespace Zeus.Server.Wav;

/// <summary>
/// Tiny vendored copy of the core <c>DspPipelineService.SanitizeAudioSample</c>
/// clamp/zero-non-finite helper. The in-core <see cref="WavWriter"/> and
/// <see cref="WavFile"/> called into the host DSP type; an installable plugin
/// can't reach core internals, so — exactly like the Voyeur plugin vendors its
/// own <c>WavWriter</c> — the recorder carries its own copy. Keep this byte-for-
/// byte equivalent to core so written/read WAVs are identical to the in-core
/// feature (NaN/Inf → 0, hard-clamp to ±1).
/// </summary>
internal static class AudioSanitize
{
    /// <summary>Zero a non-finite sample and hard-clamp to ±1.0.</summary>
    public static float SanitizeAudioSample(float sample)
    {
        if (!float.IsFinite(sample)) return 0f;
        return Math.Clamp(sample, -1f, 1f);
    }
}

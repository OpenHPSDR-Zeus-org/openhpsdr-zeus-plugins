// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus voice audio chain v1 — Aural Exciter.
// Copyright (C) 2025-2026 KB2UKA and contributors.
//
// Aural-Exciter-style harmonic enhancement:
//
//   1. Input → biquad high-pass filter (RBJ, Butterworth Q=0.7071) at a
//      user-tunable cutoff (typ 2-8 kHz). Below the cutoff the signal is
//      essentially silenced; above it the signal passes through.
//   2. HP-output → tanh waveshaper with a pre-saturation drive boost. The
//      tanh nonlinearity generates odd harmonics of the high-frequency
//      content — that's where the "air" and "presence" comes from.
//   3. Mix the harmonics back over the original (untouched) input. Operator
//      sets the dry/wet blend with the Mix knob (0-100%, typ 10-30%).
//
// The result: the original signal is preserved untouched (so bass and
// low-mids are unaffected); a small dose of synthesised upper harmonics
// rides on top to add sparkle and definition. Classic Aphex Aural Exciter
// pattern, all-managed C# implementation against WDSP-native sample
// buffers.

namespace Openhpsdr.Zeus.Samples.Exciter;

/// <summary>
/// Aural Exciter — single-band high-pass + waveshaper + mix-back.
/// Realtime contract identical to <c>CompressorDsp</c> / <c>EqDsp</c>:
/// no allocate, no lock, no IO in <see cref="Process"/>.
/// </summary>
public sealed class ExciterDsp
{
    // -----------------------------------------------------------------------
    // Operator-facing parameters.
    // -----------------------------------------------------------------------
    public float FrequencyHz { get; set; } = 5000f;   // HP cutoff, typ 2000-8000
    public float DriveDb     { get; set; } = 6f;      // Pre-saturation gain, typ 0-24
    public float MixPercent  { get; set; } = 20f;     // Wet/dry blend, 0-100
    public float InputDb     { get; set; } = 0f;      // Input gain trim, -24..+12 dB (v0.2.0)
    public float OutputDb    { get; set; } = 0f;      // Output gain trim, -24..+12 dB (v0.2.0)
    public bool  Bypass      { get; set; } = false;

    // -----------------------------------------------------------------------
    // Metering — last-block readbacks.
    // -----------------------------------------------------------------------
    public float LastInputPeakDb  { get; private set; } = MinDb;
    public float LastOutputPeakDb { get; private set; } = MinDb;
    public float LastHarmonicLevelDb { get; private set; } = MinDb; // peak of the wet signal pre-mix

    // -----------------------------------------------------------------------
    // Biquad HP coefficients + state (Direct-Form-II Transposed).
    // -----------------------------------------------------------------------
    private float _b0, _b1, _b2, _a1, _a2;
    private float _z1, _z2;
    private float _sampleRate = 48000f;
    private bool _coefDirty = true;

    public void Initialize(int sampleRateHz)
    {
        if (sampleRateHz <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz), "sample rate must be positive");
        _sampleRate = sampleRateHz;
        _coefDirty = true;
        Reset();
    }

    public void Reset()
    {
        _z1 = 0f;
        _z2 = 0f;
        LastInputPeakDb = MinDb;
        LastOutputPeakDb = MinDb;
        LastHarmonicLevelDb = MinDb;
    }

    public void MarkParamsDirty() => _coefDirty = true;

    /// <summary>
    /// Recompute biquad HP coefficients per the RBJ Audio EQ Cookbook
    /// (Bristow-Johnson). Butterworth Q = 1/√2 ≈ 0.7071 for a flat
    /// passband with maximum slope into the stop band.
    /// </summary>
    private void RecomputeCoefficients()
    {
        const float Q = 0.7071f;
        float f0 = MathF.Max(20f, MathF.Min(_sampleRate * 0.45f, FrequencyHz));
        float w0 = 2f * MathF.PI * f0 / _sampleRate;
        float cosW0 = MathF.Cos(w0);
        float alpha = MathF.Sin(w0) / (2f * Q);

        // High-pass biquad — RBJ cookbook formulas.
        float b0 = (1f + cosW0) * 0.5f;
        float b1 = -(1f + cosW0);
        float b2 = (1f + cosW0) * 0.5f;
        float a0 = 1f + alpha;
        float a1 = -2f * cosW0;
        float a2 = 1f - alpha;

        float inv = 1f / a0;
        _b0 = b0 * inv;
        _b1 = b1 * inv;
        _b2 = b2 * inv;
        _a1 = a1 * inv;
        _a2 = a2 * inv;

        _coefDirty = false;
    }

    /// <summary>
    /// Process one block. In-place processing acceptable (same backing buffer
    /// for both spans).
    /// </summary>
    public void Process(ReadOnlySpan<float> input, Span<float> output)
    {
        if (input.Length != output.Length)
            throw new ArgumentException("input and output spans must be the same length");
        if (input.Length == 0) return;

        // Bypass fast-path per the audio-chain convention — copy through,
        // identity meters, zero harmonic-level metric. Preserve filter state
        // (Z1/Z2) so re-engaging doesn't pop.
        if (Bypass)
        {
            input.CopyTo(output);
            float peak = MinDb;
            for (int i = 0; i < input.Length; i++)
            {
                float dbA = LinearToDb(MathF.Abs(input[i]));
                if (dbA > peak) peak = dbA;
            }
            LastInputPeakDb = peak;
            LastOutputPeakDb = peak;
            LastHarmonicLevelDb = MinDb;
            return;
        }

        if (_coefDirty) RecomputeCoefficients();

        // Snapshot once per block.
        float driveLin = DbToLinear(DriveDb);
        float mix = MathF.Max(0f, MathF.Min(1f, MixPercent * 0.01f));
        float inLin  = DbToLinear(InputDb);   // v0.2.0 — input trim
        float outLin = DbToLinear(OutputDb);  // v0.2.0 — output trim

        float z1 = _z1;
        float z2 = _z2;

        float inputPeakDb = MinDb;
        float outputPeakDb = MinDb;
        float harmonicPeakDb = MinDb;

        for (int n = 0; n < input.Length; n++)
        {
            // IN meter — raw input level BEFORE input trim.
            float raw = input[n];
            float rawAbsDb = LinearToDb(MathF.Abs(raw));
            if (rawAbsDb > inputPeakDb) inputPeakDb = rawAbsDb;

            // Apply input trim (v0.2.0) before the effect.
            float x = raw * inLin;

            // Biquad HP — Direct-Form-II Transposed.
            float hp = _b0 * x + z1;
            z1 = _b1 * x - _a1 * hp + z2;
            z2 = _b2 * x - _a2 * hp;

            // Saturate the HP'd signal — tanh produces (mostly) odd harmonics,
            // which is the classic "Aphex" character: shimmering and bright
            // rather than gritty.
            float wet = MathF.Tanh(driveLin * hp);

            float wetAbsDb = LinearToDb(MathF.Abs(wet));
            if (wetAbsDb > harmonicPeakDb) harmonicPeakDb = wetAbsDb;

            // Mix back over the DRY signal. The dry signal is untouched, so
            // bass / low-mids / mid-range stay pristine. Only the harmonics
            // synthesised from the HP-filtered band get added on top.
            float mixed = x + wet * mix;

            // Apply output trim (v0.2.0) — OUT meter reflects post-trim.
            float y = mixed * outLin;
            output[n] = y;

            float yAbsDb = LinearToDb(MathF.Abs(y));
            if (yAbsDb > outputPeakDb) outputPeakDb = yAbsDb;
        }

        _z1 = z1;
        _z2 = z2;

        LastInputPeakDb = inputPeakDb;
        LastOutputPeakDb = outputPeakDb;
        LastHarmonicLevelDb = harmonicPeakDb;
    }

    // -----------------------------------------------------------------------
    // dB helpers — same shape as Compressor / EQ.
    // -----------------------------------------------------------------------
    internal const float MinDb = -200f;

    internal static float LinearToDb(float linear)
    {
        if (linear <= 1e-10f) return MinDb;
        return MathF.Log(linear) * 8.685889638065035f; // 20 / ln(10)
    }

    internal static float DbToLinear(float db)
    {
        return MathF.Exp(db * 0.11512925464970228f); // ln(10) / 20
    }
}

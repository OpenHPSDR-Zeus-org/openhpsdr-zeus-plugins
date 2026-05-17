// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus voice audio chain v1 — Psychoacoustic Bass Enhancer (Family B).
// Copyright (C) 2025-2026 KB2UKA and contributors.
//
// Implements the missing-fundamental approach to bass enhancement
// (Aphex 204 "Big Bottom" / Waves MaxxBass family). The radio antenna
// can't efficiently radiate sub-100 Hz, so adding actual bass to the
// TX audio is wasted energy. Instead this block synthesises HARMONICS
// of the bass content in the radiable mid range — the ear hears the
// harmonic series and reconstructs the perceived fundamental.
//
// Reference: Bankstown (MIT, Rust LV2) — github.com/chadmed/bankstown
// for the canonical Family-B algorithm. Algorithm derived from the
// published MaxxBass paper (Ben-Tzur, AES) and the Aphex 204 owner's
// manual. No code copied from Bankstown.
//
// Per-sample algorithm (~25 mul/adds + 2 abs):
//
//   1. lp = low_pass(input, cutoff)           ← isolate bass band
//   2. rect = |lp|                            ← full-wave rectify
//   3. dc = dc_block(rect)                    ← remove DC offset
//   4. hp = high_pass(dc, cutoff)             ← push the harmonics up
//   5. shaped = tanh(amount * hp)             ← shape & limit the wet
//   6. output = input + shaped * mix          ← mix over dry
//
// All filters are biquads (RBJ Audio EQ Cookbook, Butterworth Q).

namespace Openhpsdr.Zeus.Samples.Bass;

public sealed class BassDsp
{
    // -----------------------------------------------------------------------
    // Operator-facing parameters.
    // -----------------------------------------------------------------------
    public float FrequencyHz { get; set; } = 150f;    // LP/HP split point, typ 80-300
    public float AmountPct   { get; set; } = 35f;     // Harmonic-shaper drive, 0-100
    public float MixPct      { get; set; } = 25f;     // Wet/dry blend, 0-100
    public bool  Bypass      { get; set; } = false;

    // -----------------------------------------------------------------------
    // Metering — last-block readbacks.
    // -----------------------------------------------------------------------
    public float LastInputPeakDb  { get; private set; } = MinDb;
    public float LastOutputPeakDb { get; private set; } = MinDb;
    public float LastHarmonicLevelDb { get; private set; } = MinDb; // peak of the wet pre-mix

    // -----------------------------------------------------------------------
    // Biquad LP + HP coefficients + state.
    // -----------------------------------------------------------------------
    private float _lpB0, _lpB1, _lpB2, _lpA1, _lpA2;
    private float _lpZ1, _lpZ2;
    private float _hpB0, _hpB1, _hpB2, _hpA1, _hpA2;
    private float _hpZ1, _hpZ2;

    // DC blocker — simple one-pole HP at very low cutoff (~10 Hz), state
    // preserved across blocks.
    private float _dcBlockX1, _dcBlockY1;

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
        _lpZ1 = _lpZ2 = 0f;
        _hpZ1 = _hpZ2 = 0f;
        _dcBlockX1 = _dcBlockY1 = 0f;
        LastInputPeakDb = MinDb;
        LastOutputPeakDb = MinDb;
        LastHarmonicLevelDb = MinDb;
    }

    public void MarkParamsDirty() => _coefDirty = true;

    /// <summary>
    /// Recompute biquad LP + HP coefficients at the operator-tunable cutoff.
    /// Butterworth Q = 1/√2 for both — flat passband, gentle roll-off into
    /// stop band. RBJ Audio EQ Cookbook.
    /// </summary>
    private void RecomputeCoefficients()
    {
        const float Q = 0.7071f;
        float f0 = MathF.Max(20f, MathF.Min(_sampleRate * 0.45f, FrequencyHz));
        float w0 = 2f * MathF.PI * f0 / _sampleRate;
        float cosW0 = MathF.Cos(w0);
        float alpha = MathF.Sin(w0) / (2f * Q);

        // Low-pass biquad — RBJ.
        {
            float b0 = (1f - cosW0) * 0.5f;
            float b1 = 1f - cosW0;
            float b2 = (1f - cosW0) * 0.5f;
            float a0 = 1f + alpha;
            float a1 = -2f * cosW0;
            float a2 = 1f - alpha;
            float inv = 1f / a0;
            _lpB0 = b0 * inv; _lpB1 = b1 * inv; _lpB2 = b2 * inv;
            _lpA1 = a1 * inv; _lpA2 = a2 * inv;
        }

        // High-pass biquad — RBJ.
        {
            float b0 = (1f + cosW0) * 0.5f;
            float b1 = -(1f + cosW0);
            float b2 = (1f + cosW0) * 0.5f;
            float a0 = 1f + alpha;
            float a1 = -2f * cosW0;
            float a2 = 1f - alpha;
            float inv = 1f / a0;
            _hpB0 = b0 * inv; _hpB1 = b1 * inv; _hpB2 = b2 * inv;
            _hpA1 = a1 * inv; _hpA2 = a2 * inv;
        }

        _coefDirty = false;
    }

    public void Process(ReadOnlySpan<float> input, Span<float> output)
    {
        if (input.Length != output.Length)
            throw new ArgumentException("input and output spans must be the same length");
        if (input.Length == 0) return;

        // Bypass fast-path per the audio-chain convention.
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
        float amountLin = DbToLinear(AmountPct * 0.36f); // 0..36 dB shaper drive
        float mix = MathF.Max(0f, MathF.Min(1f, MixPct * 0.01f));

        // Pull state into locals for the inner loop.
        float lpZ1 = _lpZ1, lpZ2 = _lpZ2;
        float hpZ1 = _hpZ1, hpZ2 = _hpZ2;
        float dcX1 = _dcBlockX1, dcY1 = _dcBlockY1;

        // DC-blocker pole — 10 Hz at SR (~0.9987 at 48 k).
        float dcR = 1f - (2f * MathF.PI * 10f / _sampleRate);

        float inputPeakDb = MinDb;
        float outputPeakDb = MinDb;
        float harmonicPeakDb = MinDb;

        for (int n = 0; n < input.Length; n++)
        {
            float x = input[n];
            float xAbsDb = LinearToDb(MathF.Abs(x));
            if (xAbsDb > inputPeakDb) inputPeakDb = xAbsDb;

            // 1. Low-pass — isolate the bass band.
            float lp = _lpB0 * x + lpZ1;
            lpZ1 = _lpB1 * x - _lpA1 * lp + lpZ2;
            lpZ2 = _lpB2 * x - _lpA2 * lp;

            // 2. Full-wave rectify → creates 2nd, 4th, 6th harmonics. Classic
            // missing-fundamental technique: rectifying a sine at f produces
            // a signal with strong 2f / 4f / 6f content and a DC offset.
            float rect = MathF.Abs(lp);

            // 3. DC-block — remove the offset so we don't slam the LP filter
            // downstream with bias. y[n] = x[n] - x[n-1] + R · y[n-1].
            float dc = rect - dcX1 + dcR * dcY1;
            dcX1 = rect;
            dcY1 = dc;

            // 4. High-pass at the same cutoff — push the synth output up into
            // the radiable band. The bass-band fundamentals are removed; only
            // the 2H/4H/6H harmonics survive, plus a softened residual.
            float hp = _hpB0 * dc + hpZ1;
            hpZ1 = _hpB1 * dc - _hpA1 * hp + hpZ2;
            hpZ2 = _hpB2 * dc - _hpA2 * hp;

            // 5. tanh shaper — limits peaks, slightly enriches the harmonic
            // series, controlled by Amount.
            float shaped = MathF.Tanh(amountLin * hp);

            float wetAbsDb = LinearToDb(MathF.Abs(shaped));
            if (wetAbsDb > harmonicPeakDb) harmonicPeakDb = wetAbsDb;

            // 6. Mix over dry. Dry signal is untouched — the operator's
            // existing mid-range and air stays exactly as it was; we just add
            // the synthesised octave-up content over the top.
            float y = x + shaped * mix;
            output[n] = y;

            float yAbsDb = LinearToDb(MathF.Abs(y));
            if (yAbsDb > outputPeakDb) outputPeakDb = yAbsDb;
        }

        _lpZ1 = lpZ1; _lpZ2 = lpZ2;
        _hpZ1 = hpZ1; _hpZ2 = hpZ2;
        _dcBlockX1 = dcX1;
        _dcBlockY1 = dcY1;

        LastInputPeakDb = inputPeakDb;
        LastOutputPeakDb = outputPeakDb;
        LastHarmonicLevelDb = harmonicPeakDb;
    }

    // -----------------------------------------------------------------------
    internal const float MinDb = -200f;

    internal static float LinearToDb(float linear)
    {
        if (linear <= 1e-10f) return MinDb;
        return MathF.Log(linear) * 8.685889638065035f;
    }

    internal static float DbToLinear(float db) => MathF.Exp(db * 0.11512925464970228f);
}

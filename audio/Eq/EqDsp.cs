// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus voice audio chain v1 — 10-Band Parametric EQ.
// Copyright (C) 2025-2026 KB2UKA and contributors.
//
// 10 cascaded biquad peaking filters per the Audio EQ Cookbook
// (Bristow-Johnson, https://webaudio.github.io/Audio-EQ-Cookbook/audio-eq-cookbook.html).
// Each band: frequency (Hz), gain (dB), Q. Coefficients re-derived
// only when the operator touches a parameter; per-sample inner loop
// is just 5 multiplies + 4 adds per band (50 mul-adds per sample for
// all 10 bands cascaded at 48 kHz = trivial CPU).

namespace Openhpsdr.Zeus.Samples.Eq;

/// <summary>
/// 10-band cascaded parametric EQ. All 10 bands are peaking filters;
/// operators get the most flexible shape. HP/LP shelves can ride at
/// bands 1 and 10 by dialing Q low and pulling gain up/down, or by
/// extending this DSP to support filter-type-per-band in a later
/// release.
///
/// Realtime contract identical to <c>CompressorDsp</c>: no allocate,
/// no lock, no IO inside <see cref="Process"/>.
/// </summary>
public sealed class EqDsp
{
    public const int BandCount = 10;

    // Default band frequencies — voice-focused logarithmic spread from
    // 80 Hz (low-mids / chest) to 6 kHz (presence / "air"). Operator can
    // move each band freely; these are just initial positions.
    public static readonly float[] DefaultFrequencies =
    {
        80f, 150f, 300f, 500f, 800f, 1200f, 1800f, 2700f, 4000f, 6000f,
    };

    // Per-band state.
    public sealed class Band
    {
        public float FrequencyHz = 1000f;
        public float GainDb      = 0f;
        public float Q           = 1.0f;

        // Biquad coefficients (recomputed when params change).
        public float B0, B1, B2, A1, A2;
        // Direct-form II transposed state (one delay per band — minimal
        // numerical denormal risk vs Direct-Form-I in float32).
        public float Z1, Z2;
    }

    public readonly Band[] Bands = new Band[BandCount];

    public bool Bypass { get; set; } = false;

    // Last-block readbacks for metering (input peak, output peak).
    public float LastInputPeakDb  { get; private set; } = MinDb;
    public float LastOutputPeakDb { get; private set; } = MinDb;

    private float _sampleRate = 48000f;
    private bool _coefDirty = true;

    public EqDsp()
    {
        for (int i = 0; i < BandCount; i++)
            Bands[i] = new Band { FrequencyHz = DefaultFrequencies[i], GainDb = 0f, Q = 1.0f };
    }

    public void Initialize(int sampleRateHz)
    {
        if (sampleRateHz <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz), "sample rate must be positive");
        _sampleRate = sampleRateHz;
        _coefDirty = true;
        Reset();
    }

    /// <summary>Zero the per-band delay state. Safe to call from the audio thread.</summary>
    public void Reset()
    {
        for (int i = 0; i < BandCount; i++)
        {
            Bands[i].Z1 = 0f;
            Bands[i].Z2 = 0f;
        }
        LastInputPeakDb = MinDb;
        LastOutputPeakDb = MinDb;
    }

    /// <summary>Mark coefficients dirty — recomputed at the start of the next Process block.</summary>
    public void MarkParamsDirty() => _coefDirty = true;

    /// <summary>
    /// Recompute peaking-EQ biquad coefficients for all 10 bands using
    /// the standard Audio EQ Cookbook formulas:
    ///   A   = √(10^(gainDb/20))
    ///   ω0  = 2π · f0 / fs
    ///   α   = sin(ω0) / (2Q)
    ///   b0  = 1 + α·A     b1 = -2·cos(ω0)     b2 = 1 - α·A
    ///   a0  = 1 + α/A     a1 = -2·cos(ω0)     a2 = 1 - α/A
    ///   (then normalise everything by a0)
    /// </summary>
    private void RecomputeCoefficients()
    {
        for (int i = 0; i < BandCount; i++)
        {
            var b = Bands[i];
            float f0 = MathF.Max(10f, MathF.Min(_sampleRate * 0.45f, b.FrequencyHz));
            float gainDb = b.GainDb;
            float q = MathF.Max(0.1f, b.Q);

            float A = MathF.Pow(10f, gainDb / 40f);  // sqrt(10^(gainDb/20))
            float w0 = 2f * MathF.PI * f0 / _sampleRate;
            float cosW0 = MathF.Cos(w0);
            float alpha = MathF.Sin(w0) / (2f * q);

            float b0 = 1f + alpha * A;
            float b1 = -2f * cosW0;
            float b2 = 1f - alpha * A;
            float a0 = 1f + alpha / A;
            float a1 = -2f * cosW0;
            float a2 = 1f - alpha / A;

            // Normalise by a0.
            float inv = 1f / a0;
            b.B0 = b0 * inv;
            b.B1 = b1 * inv;
            b.B2 = b2 * inv;
            b.A1 = a1 * inv;
            b.A2 = a2 * inv;
        }
        _coefDirty = false;
    }

    /// <summary>
    /// Process one block. <paramref name="input"/> and <paramref name="output"/>
    /// MUST be the same length. In-place processing acceptable.
    /// </summary>
    public void Process(ReadOnlySpan<float> input, Span<float> output)
    {
        if (input.Length != output.Length)
            throw new ArgumentException("input and output spans must be the same length");
        if (input.Length == 0) return;

        // Bypass fast-path — copy + identity meters, skip filters.
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
            return;
        }

        if (_coefDirty) RecomputeCoefficients();

        float inputPeakDb = MinDb;
        float outputPeakDb = MinDb;

        // Process sample-by-sample, cascading all 10 bands. Direct-Form-II
        // Transposed structure per band — two state vars (Z1, Z2), four
        // multiplies + two adds for the input path, two multiplies + two
        // adds for the feedback path = ~6 mul-adds per band per sample.
        // 10 bands × ~6 ops × 1024 samples = ~60k ops per P1 TX block,
        // well under the per-tick CPU budget.
        for (int n = 0; n < input.Length; n++)
        {
            float x = input[n];
            float xAbsDb = LinearToDb(MathF.Abs(x));
            if (xAbsDb > inputPeakDb) inputPeakDb = xAbsDb;

            float y = x;
            for (int i = 0; i < BandCount; i++)
            {
                var b = Bands[i];
                // Transposed Direct Form II:
                //   y[n] = b0 · x[n] + Z1
                //   Z1   = b1 · x[n] - a1 · y[n] + Z2
                //   Z2   = b2 · x[n] - a2 · y[n]
                float yi = b.B0 * y + b.Z1;
                b.Z1 = b.B1 * y - b.A1 * yi + b.Z2;
                b.Z2 = b.B2 * y - b.A2 * yi;
                y = yi;
            }
            output[n] = y;

            float yAbsDb = LinearToDb(MathF.Abs(y));
            if (yAbsDb > outputPeakDb) outputPeakDb = yAbsDb;
        }

        LastInputPeakDb = inputPeakDb;
        LastOutputPeakDb = outputPeakDb;
    }

    // -----------------------------------------------------------------------
    // dB helpers — same shape as CompressorDsp.
    // -----------------------------------------------------------------------
    internal const float MinDb = -200f;

    internal static float LinearToDb(float linear)
    {
        if (linear <= 1e-10f) return MinDb;
        return MathF.Log(linear) * 8.685889638065035f; // 20 / ln(10)
    }
}

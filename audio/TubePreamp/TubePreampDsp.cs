// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus voice audio chain — Tube Preamp DSP.
// Copyright (C) 2025-2026 KB2UKA and contributors.
//
// Tube-preamp / soft-saturator. The signal path:
//
//   1. Input gain (Drive, in dB) scales the sample into the saturator.
//
//   2. Asymmetric tanh saturation. The classic vacuum-triode transfer
//      curve is asymmetric — the plate-current response above the
//      operating point differs from below. A clean, well-known way to
//      synthesise that asymmetry with a single hyperbolic-tangent call
//      per sample is the operating-point shift:
//
//          y = tanh(driveLin * x + biasOffset) - tanh(biasOffset)
//
//      The biasOffset literally moves the operating point along the
//      tanh's S-curve. Positive bias clips the negative half-cycle more
//      aggressively than the positive (more "fatness" — strong 2nd
//      harmonic), negative bias does the inverse. Subtracting
//      tanh(biasOffset) removes the static DC pedestal so we don't
//      leak DC into the rest of the chain.
//
//      With biasOffset = 0 the curve collapses to symmetric tanh —
//      mostly odd harmonics, "JFET" character. Sweep the Bias knob
//      between vintage warmth (>0) and clean (≈0).
//
//   3. 1-pole DC blocker at ~5 Hz scrubs any residual DC the asymmetric
//      clipping might leak when bias changes. Differential form (cheap,
//      stable, transparent above ~20 Hz).
//
//   4. 1-pole low-pass "Warmth" filter (operator-tunable 4-20 kHz)
//      rolls off the brightest tube harmonics. Lower setting = more
//      "vintage" / less brittle; higher setting = more present / open.
//
//   5. Output trim (Output, in dB) compensates for the saturation
//      changing perceived level. -24..+12 dB, default -6 dB.
//
// Realtime contract identical to ExciterDsp / CompressorDsp / EqDsp:
// no allocate, no lock, no IO in Process(). Param mutations on a control
// thread are picked up at the next block via MarkParamsDirty().

namespace Openhpsdr.Zeus.Samples.TubePreamp;

/// <summary>
/// Realtime soft-saturating tube preamp. Single-channel (mono) for the
/// Zeus TX voice chain. All state is per-instance; multiple instances
/// in the same chain don't share state.
/// </summary>
public sealed class TubePreampDsp
{
    // -----------------------------------------------------------------------
    // Operator-facing parameters (set by control thread; snapshot per block).
    // -----------------------------------------------------------------------

    /// <summary>Input gain into the saturator, in dB. Typical 0..36.</summary>
    public float DriveDb { get; set; } = 12f;

    /// <summary>Asymmetry / operating-point shift, in tanh-domain units. -1..+1.
    /// 0 = symmetric soft-clip (mostly odd harmonics). >0 emphasises 2nd
    /// harmonic ("warmth"). &lt;0 inverts the asymmetry.</summary>
    public float Bias { get; set; } = 0.3f;

    /// <summary>Post-saturation low-pass corner in Hz. 4000..20000.</summary>
    public float WarmthHz { get; set; } = 12000f;

    /// <summary>Output trim in dB. -24..+12.</summary>
    public float OutputDb { get; set; } = -6f;

    /// <summary>Bypass the saturator + post-processing. Filter state is
    /// retained so re-engaging doesn't pop.</summary>
    public bool Bypass { get; set; } = false;

    // -----------------------------------------------------------------------
    // Metering — last-block readbacks (read by control thread; cheap to read
    // a float on a separate thread for a UI meter).
    // -----------------------------------------------------------------------
    public float LastInputPeakDb  { get; private set; } = MinDb;
    public float LastOutputPeakDb { get; private set; } = MinDb;

    /// <summary>Peak of the wet (post-saturator, pre-output-trim) signal —
    /// useful for visualising "how hard is the tube being pushed".</summary>
    public float LastDrivePeakDb  { get; private set; } = MinDb;

    // -----------------------------------------------------------------------
    // Filter state.
    // -----------------------------------------------------------------------

    // 1-pole LP (Warmth): y[n] = (1-a) * y[n-1] + a * x[n]
    private float _lpA = 0f;          // coefficient
    private float _lpZ = 0f;          // last output

    // 1-pole HP DC-blocker: y[n] = x[n] - x[n-1] + r * y[n-1]
    // r ≈ 1 - 2π·fc/SR for a ~5 Hz corner at 48 kHz; r ≈ 0.99935.
    private float _dcR = 0.99935f;
    private float _dcXPrev = 0f;
    private float _dcYPrev = 0f;

    private float _sampleRate = 48000f;
    private bool _coefDirty = true;

    // -----------------------------------------------------------------------
    // Lifecycle.
    // -----------------------------------------------------------------------

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
        _lpZ = 0f;
        _dcXPrev = 0f;
        _dcYPrev = 0f;
        LastInputPeakDb = MinDb;
        LastOutputPeakDb = MinDb;
        LastDrivePeakDb = MinDb;
    }

    public void MarkParamsDirty() => _coefDirty = true;

    private void RecomputeCoefficients()
    {
        // 1-pole LP coefficient for Warmth.
        float fc = MathF.Max(20f, MathF.Min(_sampleRate * 0.45f, WarmthHz));
        float x = MathF.Exp(-2f * MathF.PI * fc / _sampleRate);
        _lpA = 1f - x;

        // DC-blocker pole. Higher SR → r closer to 1.
        float fcHp = 5f; // Hz
        _dcR = 1f - (2f * MathF.PI * fcHp / _sampleRate);
        if (_dcR < 0.9f) _dcR = 0.9f;

        _coefDirty = false;
    }

    // -----------------------------------------------------------------------
    // Process.
    // -----------------------------------------------------------------------

    public void Process(ReadOnlySpan<float> input, Span<float> output)
    {
        if (input.Length != output.Length)
            throw new ArgumentException("input and output spans must be the same length");
        if (input.Length == 0) return;

        // Bypass fast-path per the audio-chain convention. Pass-through with
        // identity meters; preserve filter state so re-engage doesn't pop.
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
            LastDrivePeakDb = MinDb;
            return;
        }

        if (_coefDirty) RecomputeCoefficients();

        // Snapshot parameters at block boundary so the curve doesn't shimmer
        // when knobs are moving fast.
        float driveLin = DbToLinear(DriveDb);
        float bias     = MathF.Max(-1f, MathF.Min(1f, Bias));
        float dcOffset = MathF.Tanh(bias);
        float outLin   = DbToLinear(OutputDb);
        float lpA      = _lpA;
        float dcR      = _dcR;

        // Promote filter state into locals to keep them in CPU registers.
        float lpZ      = _lpZ;
        float dcXPrev  = _dcXPrev;
        float dcYPrev  = _dcYPrev;

        float inputPeakDb  = MinDb;
        float outputPeakDb = MinDb;
        float drivePeakDb  = MinDb;

        for (int n = 0; n < input.Length; n++)
        {
            float x = input[n];
            float xAbsDb = LinearToDb(MathF.Abs(x));
            if (xAbsDb > inputPeakDb) inputPeakDb = xAbsDb;

            // Asymmetric tube saturator with operating-point shift.
            float w = MathF.Tanh(driveLin * x + bias) - dcOffset;

            float wAbsDb = LinearToDb(MathF.Abs(w));
            if (wAbsDb > drivePeakDb) drivePeakDb = wAbsDb;

            // 1-pole DC blocker — scrubs anything the saturator leaks below
            // ~5 Hz so this block remains chain-friendly.
            float dcY = w - dcXPrev + dcR * dcYPrev;
            dcXPrev = w;
            dcYPrev = dcY;

            // 1-pole LP "Warmth". Rolls off the steepest harmonics — at
            // 20 kHz the filter is essentially open; at 4 kHz it's the
            // dominant tonal shaper.
            lpZ = lpZ + lpA * (dcY - lpZ);

            // Output trim. The saturation curve is bounded to ±1, so the
            // signal is clamped *before* this multiply — the trim restores
            // perceived level without re-clipping.
            float y = lpZ * outLin;
            output[n] = y;

            float yAbsDb = LinearToDb(MathF.Abs(y));
            if (yAbsDb > outputPeakDb) outputPeakDb = yAbsDb;
        }

        _lpZ = lpZ;
        _dcXPrev = dcXPrev;
        _dcYPrev = dcYPrev;

        LastInputPeakDb  = inputPeakDb;
        LastOutputPeakDb = outputPeakDb;
        LastDrivePeakDb  = drivePeakDb;
    }

    // -----------------------------------------------------------------------
    // dB helpers — same constants as EQ / Compressor / Exciter.
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

// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus voice audio chain v1 — Compressor (issue #332 Phase 1).
// Copyright (C) 2025-2026 KB2UKA and contributors.

namespace Openhpsdr.Zeus.Samples.Compressor;

/// <summary>
/// Single-stage VCA-style compressor with separate attack/release envelope
/// follower, threshold, soft knee, ratio, and makeup gain. Realtime-safe —
/// no allocation, no locking, no IO in <see cref="Process"/>.
///
/// Algorithm (per-sample):
///   1. <c>envLin = envelope follower over |input|, attack/release smoothed</c>
///   2. <c>envDb  = 20·log10(envLin)</c>
///   3. <c>over   = envDb − thresholdDb</c>
///   4. Soft-knee gain reduction:
///        over ≤ −kneeDb/2  ⇒ grDb = 0
///        over ≥ +kneeDb/2  ⇒ grDb = over · (1 − 1/ratio)
///        in the knee       ⇒ quadratic blend between the two
///   5. <c>output = input · 10^((makeupDb − grDb) / 20)</c>
///
/// All state (envelope) is owned by this class and is preserved across
/// <see cref="Process"/> calls. <see cref="Reset"/> zeroes the envelope (call
/// on MOX edge if you want a clean restart, otherwise just let it decay).
/// </summary>
public sealed class CompressorDsp
{
    // -----------------------------------------------------------------------
    // Operator-facing parameters. Setters are control-thread; the audio thread
    // snapshots them once per Process() block to avoid torn reads mid-block.
    // -----------------------------------------------------------------------
    public float ThresholdDb { get; set; } = -18f;   // dBFS, typ -60..0
    public float Ratio       { get; set; } = 3f;     // >=1, typ 1..20
    public float AttackMs    { get; set; } = 5f;     // typ 0.1..100
    public float ReleaseMs   { get; set; } = 100f;   // typ 10..2000
    public float KneeDb      { get; set; } = 6f;     // typ 0..24 (soft knee width)
    public float MakeupDb    { get; set; } = 0f;     // typ 0..24
    // Bypass — when true Process() copies input → output and zeroes GR.
    // The audio still flows through the chain slot; this is the standard
    // per-block bypass convention all Zeus audio-chain plugins implement.
    public bool  Bypass      { get; set; } = false;

    // -----------------------------------------------------------------------
    // Metering — last-block readbacks. Updated at the end of every Process()
    // call. Operator UI samples these at ~30 Hz from a non-RT thread; tearing
    // on a multi-byte read is acceptable for display values.
    // -----------------------------------------------------------------------
    public float LastInputPeakDb     { get; private set; } = MinDb;
    public float LastOutputPeakDb    { get; private set; } = MinDb;
    public float LastGainReductionDb { get; private set; } = 0f;

    private float _envLin;       // envelope follower state in linear amplitude
    private float _sampleRate;   // captured at Initialize, used by Process

    /// <summary>Initialise (or re-initialise) DSP state for a given sample rate.</summary>
    public void Initialize(int sampleRateHz)
    {
        if (sampleRateHz <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz), "sample rate must be positive");
        _sampleRate = sampleRateHz;
        _envLin = 0f;
        LastInputPeakDb = MinDb;
        LastOutputPeakDb = MinDb;
        LastGainReductionDb = 0f;
    }

    /// <summary>Zero the envelope follower state. Safe to call from the audio thread.</summary>
    public void Reset() => _envLin = 0f;

    /// <summary>
    /// Process one block. <paramref name="input"/> and <paramref name="output"/>
    /// MUST be the same length. In-place processing is acceptable
    /// (caller may pass the same backing buffer for both spans).
    /// </summary>
    public void Process(ReadOnlySpan<float> input, Span<float> output)
    {
        if (input.Length != output.Length)
            throw new ArgumentException("input and output spans must be the same length");
        if (input.Length == 0) return;

        // Bypass fast-path — copy input to output, update meters as identity,
        // skip all DSP work. Envelope state is preserved so re-engaging the
        // compressor doesn't pop. Operator-facing: A/B audition by clicking
        // the BYPASS toggle without leaving TX.
        if (Bypass)
        {
            input.CopyTo(output);
            float peakDb = MinDb;
            for (int i = 0; i < input.Length; i++)
            {
                float dbA = LinearToDb(MathF.Abs(input[i]));
                if (dbA > peakDb) peakDb = dbA;
            }
            LastInputPeakDb     = peakDb;
            LastOutputPeakDb    = peakDb;
            LastGainReductionDb = 0f;
            return;
        }

        // Snapshot params once per block — avoids a torn read if a control-
        // thread Set lands mid-block. Cheap.
        float thresholdDb = ThresholdDb;
        float ratio       = MathF.Max(1.001f, Ratio);       // 1:1 is no-op, never below
        float kneeDb      = MathF.Max(0f, KneeDb);
        float makeupDb    = MakeupDb;
        float attackMs    = MathF.Max(0.01f, AttackMs);     // clamp to avoid div-by-zero
        float releaseMs   = MathF.Max(1f, ReleaseMs);

        // Time-constant coefficients — α = 1 − exp(−1/(SR · τ_seconds))
        // for an exponential one-pole follower. Faster than a true RMS detector
        // and well-matched to perceived voice loudness when attack < 20 ms.
        float attackCoef  = 1f - MathF.Exp(-1f / (_sampleRate * attackMs  * 0.001f));
        float releaseCoef = 1f - MathF.Exp(-1f / (_sampleRate * releaseMs * 0.001f));

        // Knee shape — pre-compute the slope-1 factor.
        // Above the upper knee edge:        gr = over · (1 − 1/ratio)
        // Below the lower knee edge:        gr = 0
        // In the knee:                      quadratic blend
        float slopeFactor = 1f - 1f / ratio;
        float halfKnee    = kneeDb * 0.5f;
        float makeupLin   = DbToLinear(makeupDb);

        float env = _envLin;
        float inputPeakDb  = MinDb;
        float outputPeakDb = MinDb;
        float maxGrDb      = 0f;

        for (int i = 0; i < input.Length; i++)
        {
            float x  = input[i];
            float ax = MathF.Abs(x);

            // Envelope follower — peak-style, asymmetric attack/release.
            // Use attack when input exceeds envelope, release otherwise.
            float coef = ax > env ? attackCoef : releaseCoef;
            env += coef * (ax - env);

            // Convert envelope to dBFS (clamped to MinDb to avoid log(0)).
            float envDb = LinearToDb(env);

            // Gain reduction in dB.
            float over = envDb - thresholdDb;
            float grDb;
            if (over <= -halfKnee)
            {
                grDb = 0f;
            }
            else if (over >= halfKnee || kneeDb <= 0f)
            {
                grDb = over * slopeFactor;
            }
            else
            {
                // Quadratic knee — at over=−halfKnee, gr=0; at over=halfKnee,
                // gr=over·slopeFactor; smooth quadratic in between.
                float t = (over + halfKnee) / kneeDb;
                grDb = slopeFactor * (over + halfKnee) * t * 0.5f;
            }

            // Apply: output = input · 10^((makeup − gr) / 20)
            float gainLin = DbToLinear(makeupDb - grDb);
            float y = x * gainLin;
            output[i] = y;

            // Track block peaks for metering.
            float xAbsDb = LinearToDb(ax);
            float yAbsDb = LinearToDb(MathF.Abs(y));
            if (xAbsDb > inputPeakDb)  inputPeakDb  = xAbsDb;
            if (yAbsDb > outputPeakDb) outputPeakDb = yAbsDb;
            if (grDb   > maxGrDb)      maxGrDb      = grDb;
        }

        _envLin = env;
        LastInputPeakDb     = inputPeakDb;
        LastOutputPeakDb    = outputPeakDb;
        LastGainReductionDb = maxGrDb;
    }

    // -----------------------------------------------------------------------
    // dB / linear helpers — exposed internal for unit tests.
    // -----------------------------------------------------------------------
    internal const float MinDb = -200f;

    internal static float LinearToDb(float linear)
    {
        if (linear <= 1e-10f) return MinDb;
        return MathF.Log(linear) * DbConversionScale; // 20 / ln(10)
    }

    internal static float DbToLinear(float db)
    {
        // 10^(db/20) — implemented as exp(db · ln(10)/20) to stay in the
        // RT-safe MathF.Exp path.
        return MathF.Exp(db * Ln10Over20);
    }

    private const float DbConversionScale = 8.685889638065035f;   // 20 / ln(10)
    private const float Ln10Over20         = 0.11512925464970228f; // ln(10) / 20
}

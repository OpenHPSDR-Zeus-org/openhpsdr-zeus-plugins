// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus voice audio chain — Noise Gate DSP.
// Copyright (C) 2025-2026 KB2UKA and contributors.
//
// Voice noise gate with hysteresis, hold timer, separate attack/release
// time constants, configurable attenuation depth, and a final output
// gain trim. The signal path:
//
//   1. Peak-envelope detector on the input. Instantaneous attack — when
//      a sample's absolute value exceeds the envelope, the envelope
//      snaps up to that value. Exponential decay with a short fixed
//      time constant (~10 ms) so the envelope tracks voice transients
//      cleanly without chattering on individual cycles.
//
//   2. State machine with hysteresis. The gate opens when the envelope
//      crosses the user-set Threshold and closes when it drops to
//      Threshold - 3 dB (the hysteresis gap). The 3 dB gap is the
//      single most important detail in a usable voice gate — without
//      it the gate chatters when speech hovers right at the threshold.
//
//   3. Hold timer. Once open, the gate stays open for Hold milliseconds
//      after the envelope drops below the close threshold. Prevents the
//      gate from clamping shut on natural silences inside a word.
//
//   4. Gain slew. Separate attack/release one-pole filters drive the
//      applied gain toward its target (1.0 when open, rangeLin when
//      closed). Attack typ. 1-10 ms (must be fast enough not to chop
//      consonants), Release typ. 50-500 ms (slow release sounds
//      natural).
//
//   5. Output trim. -24..+12 dB applied AFTER the gate so the operator
//      can level-match the post-gate signal into the next chain block.
//
// Realtime contract identical to ExciterDsp / CompressorDsp / EqDsp:
// no allocate, no lock, no IO in Process(). Param mutations on a control
// thread are picked up at the next block via MarkParamsDirty().

namespace Openhpsdr.Zeus.Samples.NoiseGate;

/// <summary>
/// Realtime voice noise gate. Single-channel (mono) for the Zeus TX
/// voice chain. All state is per-instance; multiple instances in the
/// same chain don't share state.
/// </summary>
public sealed class NoiseGateDsp
{
    // -----------------------------------------------------------------------
    // Operator-facing parameters (set by control thread; snapshot per block).
    // -----------------------------------------------------------------------

    /// <summary>Open threshold in dBFS. Voice typ. -45..-25.</summary>
    public float ThresholdDb { get; set; } = -35f;

    /// <summary>Gain-attack time constant in ms. Typ. 1-10. Too slow
    /// chops consonants; too fast is no longer audibly transient-safe.</summary>
    public float AttackMs { get; set; } = 2f;

    /// <summary>Hold time in ms. Once the envelope drops below the close
    /// threshold the gate stays open this long before starting release.
    /// Typ. 50-200 for voice — keeps the gate from clamping between
    /// syllables.</summary>
    public float HoldMs { get; set; } = 80f;

    /// <summary>Gain-release time constant in ms. Typ. 80-300.</summary>
    public float ReleaseMs { get; set; } = 150f;

    /// <summary>Range / depth, in dB. The applied gain when the gate is
    /// closed. -inf would be a hard mute; -60 leaves a barely-audible
    /// residual; -20 is a "duck" rather than a "gate". -80..0.</summary>
    public float RangeDb { get; set; } = -60f;

    /// <summary>Output trim applied after the gate, in dB. -24..+12.</summary>
    public float OutputDb { get; set; } = 0f;

    /// <summary>Bypass — clean pass-through; state preserved across
    /// re-engage so it doesn't pop.</summary>
    public bool Bypass { get; set; } = false;

    // -----------------------------------------------------------------------
    // Metering — last-block readbacks.
    // -----------------------------------------------------------------------
    public float LastInputPeakDb  { get; private set; } = MinDb;
    public float LastOutputPeakDb { get; private set; } = MinDb;

    /// <summary>Current gate gain in dB, end-of-block. 0 = wide open;
    /// approaches <c>RangeDb</c> as the gate closes. Useful as a
    /// "gain reduction" meter — a downward-pointing column.</summary>
    public float LastGainDb { get; private set; } = 0f;

    /// <summary>Coarse end-of-block state: 0 = closed, 1 = hold, 2 = open.
    /// Drives the OPEN / HOLD / CLOSED pill in the UI.</summary>
    public int LastState { get; private set; } = 0;

    // -----------------------------------------------------------------------
    // Internal state.
    // -----------------------------------------------------------------------

    // Envelope detector — instantaneous attack, exponential decay.
    private float _envLin = 0f;
    private const float EnvDecayMs = 10f; // fixed; gates feel "tight" with ~10 ms
    private float _envDecayCoef = 0f;

    // Gain slew state — what's currently being applied.
    private float _currentGainLin = 1f;

    // Hold timer — countdown in samples. >0 means we're holding.
    private int _holdCountdown = 0;

    // Coefficients computed per block (or when params change).
    private float _attackCoef = 0f;
    private float _releaseCoef = 0f;

    // State machine — true means the gate is currently open / holding.
    // The end-of-block snapshot is mirrored to LastState.
    private bool _isOpen = false;

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
        _envLin = 0f;
        _currentGainLin = 1f;
        _holdCountdown = 0;
        _isOpen = false;
        LastInputPeakDb = MinDb;
        LastOutputPeakDb = MinDb;
        LastGainDb = 0f;
        LastState = 0;
    }

    public void MarkParamsDirty() => _coefDirty = true;

    private void RecomputeCoefficients()
    {
        // Envelope follower decay coefficient. exp(-1 / (τ * SR)).
        _envDecayCoef = MathF.Exp(-1f / (EnvDecayMs * 0.001f * _sampleRate));

        // Gain attack / release slew.
        float attackSec  = MathF.Max(0.0001f, AttackMs  * 0.001f);
        float releaseSec = MathF.Max(0.001f,  ReleaseMs * 0.001f);
        _attackCoef  = MathF.Exp(-1f / (attackSec  * _sampleRate));
        _releaseCoef = MathF.Exp(-1f / (releaseSec * _sampleRate));

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

        // Bypass fast-path. State retained so re-engage doesn't pop.
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
            LastGainDb = 0f;
            LastState = 2; // surface as "open" so the operator sees the
                           // pass-through visually mirrored
            return;
        }

        if (_coefDirty) RecomputeCoefficients();

        // Snapshot parameters once per block — knob moves don't shimmer
        // the threshold or the meters.
        float thrOpenLin  = DbToLinear(ThresholdDb);
        float thrCloseLin = DbToLinear(ThresholdDb - 3f); // 3 dB hysteresis
        float rangeLin    = DbToLinear(RangeDb);
        float outLin      = DbToLinear(OutputDb);
        float envDecay    = _envDecayCoef;
        float attack      = _attackCoef;
        float release     = _releaseCoef;
        int   holdMax     = (int)(MathF.Max(0f, HoldMs) * 0.001f * _sampleRate);

        // Promote state into locals so it stays in CPU registers.
        float env  = _envLin;
        float gain = _currentGainLin;
        int   hold = _holdCountdown;
        bool  open = _isOpen;

        float inputPeakDb  = MinDb;
        float outputPeakDb = MinDb;

        for (int n = 0; n < input.Length; n++)
        {
            float x = input[n];
            float absX = MathF.Abs(x);

            float xAbsDb = LinearToDb(absX);
            if (xAbsDb > inputPeakDb) inputPeakDb = xAbsDb;

            // Envelope: instantaneous attack, exponential release.
            env = env * envDecay;
            if (absX > env) env = absX;

            // State machine + hysteresis.
            float targetGain;
            if (open)
            {
                // Currently open. Drop out if envelope falls below the
                // CLOSE threshold AND the hold timer has elapsed.
                if (env >= thrCloseLin)
                {
                    targetGain = 1f;
                    hold = holdMax; // re-arm hold any time signal is back above close
                }
                else if (hold > 0)
                {
                    targetGain = 1f;
                    hold--;
                }
                else
                {
                    targetGain = rangeLin;
                    open = false;
                }
            }
            else
            {
                // Currently closed. Open only if envelope crosses the
                // OPEN threshold (the hysteresis gap above the close
                // threshold).
                if (env > thrOpenLin)
                {
                    targetGain = 1f;
                    open = true;
                    hold = holdMax;
                }
                else
                {
                    targetGain = rangeLin;
                }
            }

            // Asymmetric one-pole slew toward the target. Attack
            // when rising (gate opening), release when falling.
            float coef = (targetGain > gain) ? attack : release;
            gain = targetGain + (gain - targetGain) * coef;

            // Apply gate gain, then the output trim.
            float y = x * gain * outLin;
            output[n] = y;

            float yAbsDb = LinearToDb(MathF.Abs(y));
            if (yAbsDb > outputPeakDb) outputPeakDb = yAbsDb;
        }

        _envLin = env;
        _currentGainLin = gain;
        _holdCountdown = hold;
        _isOpen = open;

        LastInputPeakDb  = inputPeakDb;
        LastOutputPeakDb = outputPeakDb;
        LastGainDb       = LinearToDb(gain); // 0 dB when wide open
        // Encode end-of-block state for the UI pill. Hold = open with
        // a non-zero hold countdown (signal is currently below close
        // threshold but we're keeping the gate up).
        LastState = !open ? 0 : (hold > 0 ? 1 : 2);
    }

    // -----------------------------------------------------------------------
    // dB helpers — same constants as the other audio-chain plugins.
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

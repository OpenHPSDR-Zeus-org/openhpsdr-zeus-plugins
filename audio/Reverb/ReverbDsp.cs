// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus voice audio chain v1 — Schroeder reverb.
// Copyright (C) 2025-2026 KB2UKA and contributors.
//
// Classic Schroeder reverb topology:
//
//                  +-> Comb #1 (delay 1116 / damp) -+
//                  +-> Comb #2 (delay 1188 / damp) -+
//   input  ----+-> +-> Comb #3 (delay 1277 / damp) -+--> sum --> Allpass #1 --> Allpass #2 --> wet
//              |   +-> Comb #4 (delay 1356 / damp) -+
//              |
//              +---------------- PreDelay tap ------+
//
//   output = (1 - mix) · input + mix · wet
//
// Comb-filter delays are the four shortest comb lengths from Freeverb (Schroeder /
// Stautner & Puckette tradition — primes/near-primes chosen so the early modal
// echoes don't line up and clang). Sample-rate scaled at Initialize. Feedback
// gain derived from Size + Decay. Each comb has a one-pole LP in its feedback
// path controlled by Damp (Schroeder's "absorbent reverb" extension).
//
// Two series allpass filters at 556 / 441 samples (Freeverb's first two) with
// fixed feedback 0.5 diffuse the comb output into a dense, ringless tail.
//
// References (clean-room reading, no code copied):
//   - Schroeder, "Natural Sounding Artificial Reverberation", JAES 1962
//   - Stautner & Puckette, "Designing Multi-Channel Reverberators", CMJ 1982
//   - Freeverb / Public domain (Jezar at Dreampoint, 2000) — delay constants
//   - Smith, "Physical Audio Signal Processing", CCRMA online text

namespace Openhpsdr.Zeus.Samples.Reverb;

public sealed class ReverbDsp
{
    // -----------------------------------------------------------------------
    // Operator-facing parameters.
    // -----------------------------------------------------------------------
    /// <summary>Apparent room size, 0-100. Scales the comb delay lengths.</summary>
    public float SizePct { get; set; } = 50f;

    /// <summary>Decay time, 0-100. Maps to comb feedback gain (RT60).</summary>
    public float DecayPct { get; set; } = 40f;

    /// <summary>HF damping, 0-100. One-pole LP coefficient inside each comb's
    /// feedback path — higher = more high-frequency absorption.</summary>
    public float DampPct { get; set; } = 50f;

    /// <summary>Pre-delay in ms, 0-100. Time gap between dry transient and the
    /// first reflected energy.</summary>
    public float PreDelayMs { get; set; } = 15f;

    /// <summary>Wet/dry blend, 0-100. Conservative default — ham-radio TX
    /// sweetening, not music production.</summary>
    public float MixPct { get; set; } = 12f;

    /// <summary>Input gain trim, -24..+12 dB (v0.2.0).</summary>
    public float InputDb { get; set; } = 0f;

    /// <summary>Output gain trim, -24..+12 dB (v0.2.0).</summary>
    public float OutputDb { get; set; } = 0f;

    public bool Bypass { get; set; } = false;

    // -----------------------------------------------------------------------
    // Metering — last-block readbacks.
    // -----------------------------------------------------------------------
    public float LastInputPeakDb  { get; private set; } = MinDb;
    public float LastOutputPeakDb { get; private set; } = MinDb;
    public float LastWetLevelDb   { get; private set; } = MinDb;

    // -----------------------------------------------------------------------
    // Schroeder topology.
    // -----------------------------------------------------------------------
    private const int CombCount    = 4;
    private const int AllpassCount = 2;

    // Freeverb's four shortest comb lengths at 44.1 kHz.
    private static readonly int[] CombLensRef    = { 1116, 1188, 1277, 1356 };
    // Freeverb's first two allpass lengths at 44.1 kHz.
    private static readonly int[] AllpassLensRef = {  556,  441 };

    // Comb filter state.
    private float[][] _combBuf = new float[CombCount][];
    private int[]     _combIdx = new int[CombCount];
    private int[]     _combLen = new int[CombCount];
    private float[]   _combLp  = new float[CombCount]; // one-pole LP state per comb
    private float     _combFeedback;
    private float     _combDamp;

    // Allpass state.
    private float[][] _apBuf = new float[AllpassCount][];
    private int[]     _apIdx = new int[AllpassCount];
    private int[]     _apLen = new int[AllpassCount];
    private const float ApFeedback = 0.5f; // classic Schroeder allpass coefficient

    // Pre-delay ring.
    private float[] _preBuf = Array.Empty<float>();
    private int     _preIdx;
    private int     _preLen;

    private float _sampleRate = 48000f;
    private bool  _dirty = true;

    public void Initialize(int sampleRateHz)
    {
        if (sampleRateHz <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz), "sample rate must be positive");
        _sampleRate = sampleRateHz;

        // Pre-allocate worst-case buffers so Process() can never allocate.
        // Comb buffers must be sized to the largest comb delay we could ever
        // configure (100 % Size at 48 kHz with the longest Freeverb constant).
        float maxScale = sampleRateHz / 44100f;
        for (int i = 0; i < CombCount; i++)
        {
            int maxLen = (int)MathF.Ceiling(CombLensRef[i] * maxScale * 1.5f);
            _combBuf[i] = new float[maxLen];
        }
        for (int i = 0; i < AllpassCount; i++)
        {
            int maxLen = (int)MathF.Ceiling(AllpassLensRef[i] * maxScale * 1.5f);
            _apBuf[i] = new float[maxLen];
        }
        // Pre-delay ring sized for 100 ms (we expose 0-100 ms).
        _preBuf = new float[(int)MathF.Ceiling(_sampleRate * 0.1f) + 8];

        _dirty = true;
        Reset();
    }

    public void Reset()
    {
        for (int i = 0; i < CombCount; i++)
        {
            Array.Clear(_combBuf[i]);
            _combIdx[i] = 0;
            _combLp[i]  = 0f;
        }
        for (int i = 0; i < AllpassCount; i++)
        {
            Array.Clear(_apBuf[i]);
            _apIdx[i] = 0;
        }
        Array.Clear(_preBuf);
        _preIdx = 0;

        LastInputPeakDb  = MinDb;
        LastOutputPeakDb = MinDb;
        LastWetLevelDb   = MinDb;
    }

    public void MarkParamsDirty() => _dirty = true;

    private void RecomputeRuntime()
    {
        // Size 0-100 → 0.7-1.1 delay-length scale. 50 % puts the network at the
        // Freeverb reference geometry; below that the room shrinks (tighter,
        // brighter early reflections), above it stretches (longer, lazier
        // decay).
        float size    = MathF.Max(0f, MathF.Min(100f, SizePct))    * 0.01f;
        float sizeMul = 0.7f + size * 0.4f;
        float srScale = _sampleRate / 44100f;

        for (int i = 0; i < CombCount; i++)
        {
            int len = (int)MathF.Round(CombLensRef[i] * srScale * sizeMul);
            if (len < 1) len = 1;
            if (len > _combBuf[i].Length) len = _combBuf[i].Length;
            _combLen[i] = len;
            if (_combIdx[i] >= len) _combIdx[i] = 0;
        }
        for (int i = 0; i < AllpassCount; i++)
        {
            int len = (int)MathF.Round(AllpassLensRef[i] * srScale * sizeMul);
            if (len < 1) len = 1;
            if (len > _apBuf[i].Length) len = _apBuf[i].Length;
            _apLen[i] = len;
            if (_apIdx[i] >= len) _apIdx[i] = 0;
        }

        // Decay 0-100 → comb feedback 0.70-0.97. Higher feedback = longer
        // RT60. Capped well below 1.0 so the network can't self-oscillate.
        float decay = MathF.Max(0f, MathF.Min(100f, DecayPct)) * 0.01f;
        _combFeedback = 0.70f + decay * 0.27f;

        // Damp 0-100 → one-pole LP coefficient 0.0-0.4. Each sample of the
        // feedback path is LP-filtered as: lp = damp·lp_prev + (1 - damp)·y.
        float damp = MathF.Max(0f, MathF.Min(100f, DampPct)) * 0.01f;
        _combDamp = damp * 0.4f;

        // Pre-delay 0-100 ms → sample count.
        float preMs = MathF.Max(0f, MathF.Min(100f, PreDelayMs));
        int preSamples = (int)MathF.Round(preMs * _sampleRate * 0.001f);
        if (preSamples < 0) preSamples = 0;
        if (preSamples >= _preBuf.Length) preSamples = _preBuf.Length - 1;
        _preLen = preSamples;
        if (_preLen > 0 && _preIdx >= _preLen) _preIdx = 0;

        _dirty = false;
    }

    public void Process(ReadOnlySpan<float> input, Span<float> output)
    {
        if (input.Length != output.Length)
            throw new ArgumentException("input and output spans must be the same length");
        if (input.Length == 0) return;

        if (Bypass)
        {
            input.CopyTo(output);
            float peak = MinDb;
            for (int i = 0; i < input.Length; i++)
            {
                float dbA = LinearToDb(MathF.Abs(input[i]));
                if (dbA > peak) peak = dbA;
            }
            LastInputPeakDb  = peak;
            LastOutputPeakDb = peak;
            LastWetLevelDb   = MinDb;
            return;
        }

        if (_dirty) RecomputeRuntime();

        float mix     = MathF.Max(0f, MathF.Min(1f, MixPct * 0.01f));
        float dryGain = 1f - mix;
        float inLin   = DbToLinear(InputDb);  // v0.2.0 — input trim
        float outLin  = DbToLinear(OutputDb); // v0.2.0 — output trim
        float fb      = _combFeedback;
        float damp    = _combDamp;
        float oneMinusDamp = 1f - damp;

        // Hoist into locals so the JIT keeps them in registers.
        var c0Buf = _combBuf[0]; var c1Buf = _combBuf[1]; var c2Buf = _combBuf[2]; var c3Buf = _combBuf[3];
        int c0Len = _combLen[0]; int c1Len = _combLen[1]; int c2Len = _combLen[2]; int c3Len = _combLen[3];
        int c0Idx = _combIdx[0]; int c1Idx = _combIdx[1]; int c2Idx = _combIdx[2]; int c3Idx = _combIdx[3];
        float c0Lp = _combLp[0]; float c1Lp = _combLp[1]; float c2Lp = _combLp[2]; float c3Lp = _combLp[3];

        var a0Buf = _apBuf[0]; var a1Buf = _apBuf[1];
        int a0Len = _apLen[0]; int a1Len = _apLen[1];
        int a0Idx = _apIdx[0]; int a1Idx = _apIdx[1];

        var preBuf = _preBuf;
        int preLen = _preLen;
        int preIdx = _preIdx;
        int preCap = preBuf.Length;

        float inputPeakDb  = MinDb;
        float outputPeakDb = MinDb;
        float wetPeakDb    = MinDb;

        // Per-Schroeder convention, combs are summed and then attenuated so the
        // raw wet amplitude is comparable to dry before Mix. Network gain is
        // dominated by 1 / (1 - fb) per comb; with 4 combs in parallel a small
        // scaling factor keeps headroom sensible.
        const float WetScale = 0.18f;

        for (int n = 0; n < input.Length; n++)
        {
            // IN meter — raw input level BEFORE input trim.
            float raw = input[n];
            float rawAbsDb = LinearToDb(MathF.Abs(raw));
            if (rawAbsDb > inputPeakDb) inputPeakDb = rawAbsDb;

            // Apply input trim (v0.2.0) before the effect.
            float x = raw * inLin;

            // Pre-delay tap. When preLen == 0 the ring degenerates to a direct
            // pass through (we just feed x straight into the combs).
            float preTap;
            if (preLen > 0)
            {
                preTap = preBuf[preIdx];
                preBuf[preIdx] = x;
                preIdx++;
                if (preIdx >= preLen) preIdx = 0;
            }
            else
            {
                preTap = x;
            }

            // Comb #0
            float c0Out = c0Buf[c0Idx];
            c0Lp = c0Out * oneMinusDamp + c0Lp * damp;
            c0Buf[c0Idx] = preTap + c0Lp * fb;
            c0Idx++; if (c0Idx >= c0Len) c0Idx = 0;

            // Comb #1
            float c1Out = c1Buf[c1Idx];
            c1Lp = c1Out * oneMinusDamp + c1Lp * damp;
            c1Buf[c1Idx] = preTap + c1Lp * fb;
            c1Idx++; if (c1Idx >= c1Len) c1Idx = 0;

            // Comb #2
            float c2Out = c2Buf[c2Idx];
            c2Lp = c2Out * oneMinusDamp + c2Lp * damp;
            c2Buf[c2Idx] = preTap + c2Lp * fb;
            c2Idx++; if (c2Idx >= c2Len) c2Idx = 0;

            // Comb #3
            float c3Out = c3Buf[c3Idx];
            c3Lp = c3Out * oneMinusDamp + c3Lp * damp;
            c3Buf[c3Idx] = preTap + c3Lp * fb;
            c3Idx++; if (c3Idx >= c3Len) c3Idx = 0;

            float wet = (c0Out + c1Out + c2Out + c3Out) * WetScale;

            // Allpass #0 — Schroeder's transposed-form allpass:
            //   y[n]   = -gain·x[n] + buf[n - M] + gain·buf_out
            //   buf[n] = x[n] + gain·(buf[n - M])
            // Implementation form:
            //   bufOut = buf[idx]; y = -gain·in + bufOut; buf[idx] = in + gain·bufOut
            {
                float ap0Read = a0Buf[a0Idx];
                float y0 = -ApFeedback * wet + ap0Read;
                a0Buf[a0Idx] = wet + ApFeedback * ap0Read;
                a0Idx++; if (a0Idx >= a0Len) a0Idx = 0;
                wet = y0;
            }
            // Allpass #1
            {
                float ap1Read = a1Buf[a1Idx];
                float y1 = -ApFeedback * wet + ap1Read;
                a1Buf[a1Idx] = wet + ApFeedback * ap1Read;
                a1Idx++; if (a1Idx >= a1Len) a1Idx = 0;
                wet = y1;
            }

            float wetAbsDb = LinearToDb(MathF.Abs(wet));
            if (wetAbsDb > wetPeakDb) wetPeakDb = wetAbsDb;

            // Wet/dry blend, then output trim (v0.2.0).
            float mixed = dryGain * x + mix * wet;
            float y = mixed * outLin;
            output[n] = y;

            float yAbsDb = LinearToDb(MathF.Abs(y));
            if (yAbsDb > outputPeakDb) outputPeakDb = yAbsDb;
        }

        // Flush state back.
        _combIdx[0] = c0Idx; _combIdx[1] = c1Idx; _combIdx[2] = c2Idx; _combIdx[3] = c3Idx;
        _combLp[0]  = c0Lp;  _combLp[1]  = c1Lp;  _combLp[2]  = c2Lp;  _combLp[3]  = c3Lp;
        _apIdx[0]   = a0Idx; _apIdx[1]   = a1Idx;
        _preIdx     = preIdx;

        // Avoid unused-buffer-cap warning on the static analyser. (Defensive
        // bound, never trips because Initialize sized the ring correctly.)
        if (preIdx >= preCap) _preIdx = 0;

        LastInputPeakDb  = inputPeakDb;
        LastOutputPeakDb = outputPeakDb;
        LastWetLevelDb   = wetPeakDb;
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

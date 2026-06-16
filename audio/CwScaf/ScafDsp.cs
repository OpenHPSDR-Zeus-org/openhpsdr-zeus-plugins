// SPDX-License-Identifier: GPL-2.0-or-later
//
// CW SCAF (Switched-Capacitor Audio Filter emulation) — realtime DSP core.
// Copyright (C) 2026 contributors.

namespace Openhpsdr.Zeus.Samples.CwScaf;

/// <summary>
/// Variable-bandwidth audio bandpass tuned for CW copy, modelled on the
/// classic switched-capacitor audio filters (Datong FL-series / MFJ / Timewave)
/// hams bolt onto a receiver's audio output. Implemented as a cascade of
/// Robert Bristow-Johnson (RBJ) "BPF, constant 0 dB peak gain" biquads — the
/// cascade sharpens the skirts while keeping unity gain at the centre pitch.
///
/// Realtime-safe — no allocation, no locking, no IO in <see cref="Process"/>.
/// Coefficients are recomputed only when a parameter changes (dirty flag,
/// snapshotted at the top of <see cref="Process"/>), never per sample.
///
/// Parameters (control-thread setters; the audio thread snapshots them once
/// per block):
///   • <see cref="CenterHz"/>    — passband centre, set to the operator's CW pitch.
///   • <see cref="BandwidthHz"/> — −3 dB width of a single section (Q = centre / width).
///   • <see cref="Sections"/>    — number of cascaded biquads (1..4); more = steeper.
///   • <see cref="Bypass"/>      — per-block pass-through (audio still flows).
/// </summary>
public sealed class ScafDsp
{
    public const int MaxSections = 4;

    // -----------------------------------------------------------------------
    // Operator-facing parameters.
    // -----------------------------------------------------------------------
    private float _centerHz = 600f;
    private float _bandwidthHz = 200f;
    private int _sections = 2;

    public float CenterHz
    {
        get => _centerHz;
        set { _centerHz = value; _dirty = true; }
    }

    public float BandwidthHz
    {
        get => _bandwidthHz;
        set { _bandwidthHz = value; _dirty = true; }
    }

    public int Sections
    {
        get => _sections;
        set { _sections = value; _dirty = true; }
    }

    /// <summary>When true, <see cref="Process"/> copies input → output and skips
    /// filtering. Filter state is preserved so re-engaging doesn't click.</summary>
    public bool Bypass { get; set; } = false;

    // -----------------------------------------------------------------------
    // Metering — last-block peak readbacks (display only; non-RT reader).
    // -----------------------------------------------------------------------
    public float LastInputPeak  { get; private set; }
    public float LastOutputPeak { get; private set; }

    // Per-section transposed-direct-form-II state.
    private readonly float[] _z1 = new float[MaxSections];
    private readonly float[] _z2 = new float[MaxSections];

    // Shared biquad coefficients (every section is identical — same centre/Q).
    private float _b0, _b1, _b2, _a1, _a2;
    private float _sampleRate;
    private bool _dirty = true;

    public void Initialize(int sampleRateHz)
    {
        if (sampleRateHz <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz), "sample rate must be positive");
        _sampleRate = sampleRateHz;
        Reset();
        _dirty = true;
    }

    /// <summary>Zero the filter delay lines. Safe to call from the audio thread.</summary>
    public void Reset()
    {
        Array.Clear(_z1, 0, _z1.Length);
        Array.Clear(_z2, 0, _z2.Length);
    }

    public void Process(ReadOnlySpan<float> input, Span<float> output)
    {
        if (input.Length != output.Length)
            throw new ArgumentException("input and output spans must be the same length");
        if (input.Length == 0) return;

        if (Bypass)
        {
            input.CopyTo(output);
            float pk = 0f;
            for (int i = 0; i < input.Length; i++) { float a = MathF.Abs(input[i]); if (a > pk) pk = a; }
            LastInputPeak = pk;
            LastOutputPeak = pk;
            return;
        }

        if (_dirty) RecomputeCoefficients();

        int sections = Math.Clamp(_sections, 1, MaxSections);
        float b0 = _b0, b1 = _b1, b2 = _b2, a1 = _a1, a2 = _a2;

        float inPeak = 0f, outPeak = 0f;

        for (int i = 0; i < input.Length; i++)
        {
            float x = input[i];
            float ax = MathF.Abs(x);
            if (ax > inPeak) inPeak = ax;

            // Cascade: each section is the same TDF-II biquad.
            float s = x;
            for (int k = 0; k < sections; k++)
            {
                float z1 = _z1[k];
                float z2 = _z2[k];
                float y = b0 * s + z1;
                _z1[k] = b1 * s - a1 * y + z2;
                _z2[k] = b2 * s - a2 * y;
                s = y;
            }

            output[i] = s;
            float aOut = MathF.Abs(s);
            if (aOut > outPeak) outPeak = aOut;
        }

        LastInputPeak = inPeak;
        LastOutputPeak = outPeak;
    }

    /// <summary>
    /// RBJ "BPF, constant 0 dB peak gain" cookbook coefficients, normalised by
    /// a0. Centre and Q are clamped to keep the biquad stable across the audio
    /// band at the current sample rate.
    /// </summary>
    private void RecomputeCoefficients()
    {
        float fs = _sampleRate > 0 ? _sampleRate : 48000f;

        // Keep the centre safely inside (0, Nyquist) and the width positive.
        float nyq = fs * 0.5f;
        float f0 = Math.Clamp(_centerHz, 50f, nyq - 50f);
        float bw = MathF.Max(10f, _bandwidthHz);
        float q = MathF.Max(0.3f, f0 / bw);

        float w0 = 2f * MathF.PI * f0 / fs;
        float cosw0 = MathF.Cos(w0);
        float alpha = MathF.Sin(w0) / (2f * q);

        float a0 = 1f + alpha;
        float inv = 1f / a0;

        _b0 = alpha * inv;
        _b1 = 0f;
        _b2 = -alpha * inv;
        _a1 = (-2f * cosw0) * inv;
        _a2 = (1f - alpha) * inv;

        _dirty = false;
    }
}

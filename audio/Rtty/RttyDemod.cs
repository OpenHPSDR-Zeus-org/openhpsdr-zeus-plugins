// SPDX-License-Identifier: GPL-2.0-or-later
//
// RttyDemod — AFSK RTTY receiver. Two quadrature tone detectors (mark/space)
// drive a software UART that deframes ITA2 (Baudot) characters. Runs on the
// host audio thread one block at a time; the per-sample path allocates nothing
// and takes no lock. Completed characters (≈6/s at 45 baud) are pushed to a
// lock-free queue the REST status endpoint drains. Mark/space envelopes are
// published as plain fields for the panel's tuning indicator.
// Copyright (C) 2026 contributors.

using System.Collections.Concurrent;

namespace Openhpsdr.Zeus.Samples.Rtty;

public sealed class RttyDemod
{
    private readonly int _fs;

    // Tuning parameters (set under SetParams; read on the audio thread — only
    // the control thread writes, and a torn read just costs one stale sample).
    private double _baud;
    private bool _reverse;
    private bool _usos;

    // Quadrature local oscillators (unit-modulus complex rotators).
    private double _mRe = 1, _mIm, _sRe = 1, _sIm;
    private double _mStepRe = 1, _mStepIm, _sStepRe = 1, _sStepIm;
    private int _renorm;

    // One-pole post-detection integrators (≈1-bit time constant) for the
    // in-phase / quadrature products of each tone.
    private double _mII, _mIQ, _sII, _sIQ;
    private double _lpA;

    // Software UART.
    private enum U { Idle, Start, Data, Stop }
    private U _state = U.Idle;
    private double _samplesPerBit;
    private double _bitTimer;
    private int _bitIndex;
    private int _code;
    private bool _figures;
    private bool _lastMark = true;

    // Output + telemetry.
    private readonly ConcurrentQueue<char> _out = new();
    public double MarkLevel { get; private set; }
    public double SpaceLevel { get; private set; }
    public bool MarkHigh { get; private set; } = true;
    public long CharCount { get; private set; }

    public double MarkHz { get; private set; }
    public double ShiftHz { get; private set; }
    public double Baud => _baud;
    public bool Reverse => _reverse;
    public bool Usos => _usos;

    public RttyDemod(int sampleRate, double markHz = 2125, double shiftHz = 170, double baud = 45.45)
    {
        _fs = sampleRate;
        SetParams(markHz, shiftHz, baud, reverse: false, usos: true);
    }

    public void SetParams(double markHz, double shiftHz, double baud, bool reverse, bool usos)
    {
        MarkHz = markHz;
        ShiftHz = shiftHz;
        _baud = baud <= 0 ? 45.45 : baud;
        _reverse = reverse;
        _usos = usos;
        _samplesPerBit = _fs / _baud;

        double spaceHz = markHz + shiftHz;
        double wm = 2 * Math.PI * markHz / _fs;
        double ws = 2 * Math.PI * spaceHz / _fs;
        _mStepRe = Math.Cos(wm); _mStepIm = Math.Sin(wm);
        _sStepRe = Math.Cos(ws); _sStepIm = Math.Sin(ws);

        // Matched-filter integration over roughly one bit (BW ≈ baud).
        _lpA = 1.0 - Math.Exp(-2 * Math.PI * _baud / _fs);
    }

    public void Reset()
    {
        _state = U.Idle;
        _bitTimer = 0; _bitIndex = 0; _code = 0; _figures = false; _lastMark = true;
        while (_out.TryDequeue(out _)) { }
    }

    public bool TryDequeue(out char c) => _out.TryDequeue(out c);

    /// <summary>Approximate queued (undrained) character count.</summary>
    public int Pending => _out.Count;

    public void Process(ReadOnlySpan<float> x)
    {
        double a = _lpA;
        for (int n = 0; n < x.Length; n++)
        {
            double s = x[n];

            // Advance the two complex rotators by one sample.
            double mr = _mRe * _mStepRe - _mIm * _mStepIm;
            double mi = _mRe * _mStepIm + _mIm * _mStepRe;
            _mRe = mr; _mIm = mi;
            double sr = _sRe * _sStepRe - _sIm * _sStepIm;
            double si = _sRe * _sStepIm + _sIm * _sStepRe;
            _sRe = sr; _sIm = si;

            if (++_renorm >= 1024) { _renorm = 0; Renormalize(); }

            // Quadrature correlate each tone, then integrate (matched filter).
            _mII += a * (s * _mRe - _mII);
            _mIQ += a * (s * _mIm - _mIQ);
            _sII += a * (s * _sRe - _sII);
            _sIQ += a * (s * _sIm - _sIQ);

            double markPow = _mII * _mII + _mIQ * _mIQ;
            double spacePow = _sII * _sII + _sIQ * _sIQ;

            // Bit decision: mark tone stronger ⇒ logic 1, unless the operator
            // has the pair reversed (USB vs LSB / swapped tones).
            bool mark = _reverse ? spacePow > markPow : markPow > spacePow;
            MarkHigh = mark;

            SampleUart(mark);
            _lastMark = mark;

            MarkLevel = markPow;
            SpaceLevel = spacePow;
        }
    }

    // Mid-bit-sampling software UART. A start bit is a mark→space transition;
    // we wait half a bit to the centre, confirm space, then sample five data
    // bits at one-bit intervals, then a stop bit, and decode.
    private void SampleUart(bool mark)
    {
        switch (_state)
        {
            case U.Idle:
                // Falling edge (mark idle → space) opens a candidate start bit.
                if (_lastMark && !mark)
                {
                    _state = U.Start;
                    _bitTimer = _samplesPerBit * 0.5;
                }
                break;

            case U.Start:
                if (--_bitTimer <= 0)
                {
                    if (!mark)
                    {
                        _state = U.Data;
                        _bitIndex = 0;
                        _code = 0;
                        _bitTimer = _samplesPerBit;
                    }
                    else
                    {
                        _state = U.Idle; // false start (noise spike)
                    }
                }
                break;

            case U.Data:
                if (--_bitTimer <= 0)
                {
                    if (mark) _code |= 1 << _bitIndex; // LSB-first
                    _bitIndex++;
                    _bitTimer = _samplesPerBit;
                    if (_bitIndex >= 5) _state = U.Stop;
                }
                break;

            case U.Stop:
                if (--_bitTimer <= 0)
                {
                    Emit(_code);
                    _state = U.Idle;
                }
                break;
        }
    }

    private void Emit(int code)
    {
        if (code == Baudot.Ltrs) { _figures = false; return; }
        if (code == Baudot.Figs) { _figures = true; return; }

        char c = Baudot.Decode(code, _figures);
        if (_usos && code == Baudot.Space) _figures = false; // unshift on space

        if (c != '\0' && c != '\r')
        {
            _out.Enqueue(c);
            CharCount++;
        }
    }

    private void Renormalize()
    {
        double gm = 1.0 / Math.Sqrt(_mRe * _mRe + _mIm * _mIm + 1e-18);
        _mRe *= gm; _mIm *= gm;
        double gs = 1.0 / Math.Sqrt(_sRe * _sRe + _sIm * _sIm + 1e-18);
        _sRe *= gs; _sIm *= gs;
    }
}

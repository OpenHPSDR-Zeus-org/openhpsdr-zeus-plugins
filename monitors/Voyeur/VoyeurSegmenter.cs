// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see ATTRIBUTIONS.md for provenance.

namespace Zeus.Server.Voyeur;

/// <summary>
/// Energy-based transmission ("over") segmenter for Voyeur Mode (zeus-la5
/// Phase 1). Runs on the Voyeur drain thread — NEVER the DSP/RX thread — over
/// the demodulated mono RX audio. Tracks a slow noise-floor follower and gates
/// a segment open when short-window RMS rises a margin above it, closing the
/// segment after a hang period of quiet so speech pauses don't chop one over
/// into many.
///
/// Phase 2 replaces this with Silero VAD (the Phase-0 spike showed even
/// un-gated whisper barely hallucinates on this audio, but VAD still cuts work
/// and sharpens the per-over boundaries). The interface here — feed blocks,
/// get start/continue/end transitions — is exactly what the ASR runner will
/// consume, so Phase 2 is a swap of the detector, not a rewrite of the caller.
/// </summary>
public sealed class VoyeurSegmenter
{
    public enum Transition { Idle, Started, Continuing, Ended }

    public readonly record struct Result(Transition Transition, int DurationMs, float PeakDbfs);

    private readonly int _sampleRate;
    private readonly double _openMarginDb;   // RMS must exceed floor by this to open
    private readonly double _closeMarginDb;  // ...and fall below this to start the hang
    private readonly int _hangSamples;       // quiet duration that ends an over
    private readonly int _minSegmentSamples; // ignore blips shorter than this
    private readonly int _openDebounceSamples; // above-margin run required before opening
    private readonly long _maxOverSamples;   // hard cap on a single over (stuck carrier)

    // Floor-follower time constants (SECONDS). Converted to a per-block
    // coefficient via 1-exp(-blockSeconds/tau), so behaviour is INDEPENDENT of
    // the drain block size (the old fixed 0.20/0.0005 per-block coeffs drifted
    // with block length).
    private const double FloorDownTau = 0.10; // fast: find the quiet floor (~100 ms)
    private const double FloorUpTau   = 5.0;  // slow: let the floor rise (~5 s)

    private bool _active;
    private bool _floorSeeded;
    private double _noiseFloor = 1e-4; // linear RMS estimate; seeded to ambient on first block
    private long _activeSamples;
    private long _quietRun;
    private long _openRun;             // consecutive above-margin samples while idle
    private float _peak;

    public VoyeurSegmenter(int sampleRate) : this(sampleRate, new SegSettings()) { }

    public VoyeurSegmenter(int sampleRate, SegSettings settings)
    {
        _sampleRate = sampleRate;
        _openMarginDb = settings.OpenMarginDb;                         // default 6 dB (was 8)
        _closeMarginDb = Math.Max(0.0, settings.OpenMarginDb - 2.0);   // hysteresis below open
        _hangSamples = (int)(settings.HangSeconds * sampleRate);       // default 1.0 s (was 1.2)
        _minSegmentSamples = (int)(0.4 * sampleRate);
        _openDebounceSamples = (int)(0.06 * sampleRate);               // 60 ms debounce on the lower threshold
        _maxOverSamples = settings.MaxOverSeconds > 0
            ? (long)(settings.MaxOverSeconds * sampleRate)
            : long.MaxValue;
    }

    /// <summary>True while inside an over (caller should be capturing audio).</summary>
    public bool IsActive => _active;

    /// <summary>True while inside an over but in the trailing-quiet (hang) window.
    /// The drain loop buffers these blocks and DROPS them on close so the saved
    /// WAV (and whisper) never see the silent tail.</summary>
    public bool InHang => _active && _quietRun > 0;

    /// <summary>
    /// Process one block of mono samples. Returns the transition for this block.
    /// On <see cref="Transition.Ended"/>, DurationMs/PeakDbfs describe the over
    /// that just closed. The caller appends the block to the segment's audio
    /// whenever the result is Started or Continuing.
    /// </summary>
    public Result Process(ReadOnlySpan<float> block)
    {
        if (block.IsEmpty) return new Result(_active ? Transition.Continuing : Transition.Idle, 0, 0);

        // Block RMS + peak.
        double sumsq = 0;
        float peak = 0;
        foreach (var s in block)
        {
            sumsq += (double)s * s;
            float a = s < 0 ? -s : s;
            if (a > peak) peak = a;
        }
        double rms = Math.Sqrt(sumsq / block.Length);

        // Seed the floor to the first block's ambient level so the open gate is
        // relative to THIS receiver's noise, not an arbitrary constant. Without
        // this, a quiet-but-above-1e-4 band floor would false-trigger an over on
        // the very first block. The seed block itself never opens.
        if (!_floorSeeded)
        {
            _noiseFloor = Math.Max(rms, 1e-6);
            _floorSeeded = true;
            return new Result(Transition.Idle, 0, 0);
        }

        // Noise-floor follower with per-SECOND time constants, so the tracking
        // speed does not change with the drain block size.
        double blockSeconds = (double)block.Length / _sampleRate;
        double aDown = 1.0 - Math.Exp(-blockSeconds / FloorDownTau);
        double aUp   = 1.0 - Math.Exp(-blockSeconds / FloorUpTau);
        _noiseFloor += (rms - _noiseFloor) * (rms < _noiseFloor ? aDown : aUp);
        _noiseFloor = Math.Max(_noiseFloor, 1e-6);

        double overDb = 20.0 * Math.Log10(rms / _noiseFloor);

        if (!_active)
        {
            if (overDb >= _openMarginDb)
            {
                // Debounce the lowered (6 dB) threshold: require a short run of
                // above-margin audio before declaring an over, so weak-DX
                // sensitivity doesn't turn into noise chatter. The pre-roll buffer
                // (drain side) preserves these debounce blocks as the leading
                // attack / callsign.
                _openRun += block.Length;
                if (_openRun >= _openDebounceSamples)
                {
                    _active = true;
                    _activeSamples = block.Length;
                    _quietRun = 0;
                    _openRun = 0;
                    _peak = peak;
                    return new Result(Transition.Started, 0, 0);
                }
            }
            else
            {
                _openRun = 0;
            }
            return new Result(Transition.Idle, 0, 0);
        }

        // Active.
        _activeSamples += block.Length;
        if (peak > _peak) _peak = peak;

        // Hard cap: a stuck carrier / open mic can never grow one unbounded over.
        if (_activeSamples >= _maxOverSamples)
        {
            int durMsCap = (int)(_activeSamples * 1000L / _sampleRate);
            float peakDbCap = _peak > 0 ? 20f * MathF.Log10(_peak) : -120f;
            Reset();
            return new Result(Transition.Ended, durMsCap, peakDbCap);
        }

        if (overDb < _closeMarginDb)
        {
            _quietRun += block.Length;
            if (_quietRun >= _hangSamples)
            {
                // Close the over. Subtract the trailing hang so the reported
                // duration is the speech, not the hang tail.
                long speechSamples = Math.Max(0, _activeSamples - _quietRun);
                bool tooShort = speechSamples < _minSegmentSamples;
                int durMs = (int)(speechSamples * 1000L / _sampleRate);
                float peakDb = _peak > 0 ? 20f * MathF.Log10(_peak) : -120f;
                Reset();
                // A sub-minimum blip (a click, a fragment) is dropped: report
                // Ended only for real overs so the caller doesn't store noise.
                return tooShort
                    ? new Result(Transition.Idle, 0, 0)
                    : new Result(Transition.Ended, durMs, peakDb);
            }
        }
        else
        {
            _quietRun = 0; // speech resumed within the hang window
        }
        return new Result(Transition.Continuing, 0, 0);
    }

    /// <summary>Force-close any in-flight over (session stop). Returns the
    /// Ended result if a real over was open, else Idle.</summary>
    public Result Flush()
    {
        if (!_active) return new Result(Transition.Idle, 0, 0);
        long speechSamples = Math.Max(0, _activeSamples - _quietRun);
        bool tooShort = speechSamples < _minSegmentSamples;
        int durMs = (int)(speechSamples * 1000L / _sampleRate);
        float peakDb = _peak > 0 ? 20f * MathF.Log10(_peak) : -120f;
        Reset();
        return tooShort ? new Result(Transition.Idle, 0, 0) : new Result(Transition.Ended, durMs, peakDb);
    }

    private void Reset()
    {
        _active = false;
        _activeSamples = 0;
        _quietRun = 0;
        _openRun = 0;
        _peak = 0;
    }
}

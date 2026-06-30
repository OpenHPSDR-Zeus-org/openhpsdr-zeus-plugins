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
/// A tiny fixed-capacity, overwrite-oldest mono ring buffer (~500 ms by default)
/// that the Voyeur drain thread feeds continuously WHILE IDLE, so that when the
/// segmenter finally declares an over "Started" the audio captured just BEFORE
/// the gate opened — the speech attack and, critically, the leading callsign —
/// can be prepended to the saved WAV.
///
/// Why this exists: the energy gate (and any debounce on top of it) inherently
/// opens a few blocks AFTER speech actually began, which clips the first
/// syllables. ASR then loses the most valuable token in a net log — the
/// callsign. Keeping a rolling tail of pre-speech audio and prepending its
/// <see cref="Snapshot()"/> on open recovers that attack with zero cost to the
/// RX path (this runs only on the below-normal drain thread).
///
/// THREADING: single-threaded by construction. It is fed and snapshotted from
/// the ONE Voyeur drain thread (never the DSP/RX producer thread), so it carries
/// no locks or volatile state. Do not share an instance across threads.
///
/// Unlike <see cref="VoyeurAudioRing"/> (a back-pressure-free SPSC ring that
/// DROPS on overflow to protect RX), this buffer intentionally OVERWRITES the
/// oldest samples on overflow: we always want the most-recent ~500 ms, never a
/// stale snapshot.
/// </summary>
public sealed class PreRollBuffer
{
    private readonly float[] _buf;
    private readonly int _capacity;
    private int _head;   // index of the next write slot
    private int _count;  // valid samples currently retained (0.._capacity)

    /// <summary>Create a buffer that retains the most-recent
    /// <paramref name="capacitySamples"/> mono samples.</summary>
    public PreRollBuffer(int capacitySamples)
    {
        _capacity = Math.Max(1, capacitySamples);
        _buf = new float[_capacity];
    }

    /// <summary>Convenience factory: size the buffer to hold
    /// <paramref name="milliseconds"/> of mono audio at
    /// <paramref name="sampleRate"/> Hz.</summary>
    public static PreRollBuffer ForMs(int milliseconds, int sampleRate)
    {
        long n = (long)Math.Max(0, milliseconds) * Math.Max(1, sampleRate) / 1000L;
        return new PreRollBuffer((int)Math.Max(1, n));
    }

    /// <summary>Buffer capacity in samples.</summary>
    public int Capacity => _capacity;

    /// <summary>Samples currently retained (never exceeds <see cref="Capacity"/>).</summary>
    public int Count => _count;

    /// <summary>
    /// Append a block of mono samples, overwriting the oldest retained samples
    /// once the buffer is full. O(n) memcpy, no allocation. If the incoming block
    /// is larger than the whole buffer, only its trailing <see cref="Capacity"/>
    /// samples are kept (the rest could never survive anyway).
    /// </summary>
    public void Write(ReadOnlySpan<float> src)
    {
        if (src.IsEmpty) return;

        // A single block bigger than the buffer: keep only its most-recent tail
        // and reset to a clean, full, unwrapped state.
        if (src.Length >= _capacity)
        {
            src.Slice(src.Length - _capacity).CopyTo(_buf);
            _head = 0;
            _count = _capacity;
            return;
        }

        int first = Math.Min(src.Length, _capacity - _head);
        src.Slice(0, first).CopyTo(_buf.AsSpan(_head));
        if (first < src.Length)
            src.Slice(first).CopyTo(_buf.AsSpan(0)); // wrap to the front

        _head = (_head + src.Length) % _capacity;
        _count = Math.Min(_capacity, _count + src.Length);
    }

    /// <summary>
    /// Copy the retained samples, oldest-first (chronological order), into
    /// <paramref name="dst"/>. Returns the number of samples written
    /// (min of <see cref="Count"/> and the destination length). Does not mutate
    /// the buffer — call <see cref="Clear"/> after consuming on segment-open so
    /// the same pre-roll is not prepended twice.
    /// </summary>
    public int CopyTo(Span<float> dst)
    {
        int n = Math.Min(_count, dst.Length);
        if (n == 0) return 0;

        // Oldest sample index. When not full, samples were written sequentially
        // from index 0, so start = 0. When full, _head points at the oldest.
        int start = ((_head - _count) % _capacity + _capacity) % _capacity;
        int first = Math.Min(n, _capacity - start);
        _buf.AsSpan(start, first).CopyTo(dst);
        if (first < n)
            _buf.AsSpan(0, n - first).CopyTo(dst.Slice(first));
        return n;
    }

    /// <summary>
    /// Allocate and return the retained samples in chronological (oldest-first)
    /// order. Returns an empty array when nothing is buffered.
    /// </summary>
    public float[] Snapshot()
    {
        if (_count == 0) return Array.Empty<float>();
        var outBuf = new float[_count];
        CopyTo(outBuf);
        return outBuf;
    }

    /// <summary>Drop all retained samples. Call after prepending on open.</summary>
    public void Clear()
    {
        _head = 0;
        _count = 0;
    }
}

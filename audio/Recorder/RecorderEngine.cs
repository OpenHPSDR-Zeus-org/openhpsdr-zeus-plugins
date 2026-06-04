// SPDX-License-Identifier: GPL-2.0-or-later
//
// RecorderEngine — decouples the realtime RX audio tap from disk IO. OnRxAudio
// (audio thread) copies samples into a pre-allocated lock-free SPSC ring; a
// dedicated background thread drains the ring to a WAV file. This is how a
// recorder honours the IRxAudioTapPlugin contract (no IO/alloc/lock in the
// tap callback) — and is cleaner than blocking the audio thread on disk IO.
// Copyright (C) 2026 contributors.

namespace Openhpsdr.Zeus.Samples.Recorder;

public sealed class RecorderEngine : IDisposable
{
    // Ring sized to ~11 s at 48 kHz — far more than the gap between the ~30 Hz
    // tap callback and the flush thread; protects against a transient disk
    // stall without unbounded memory. Power of two for cheap masking.
    private const int RingCapacity = 1 << 19; // 524288 floats
    private const int RingMask = RingCapacity - 1;

    private readonly float[] _ring = new float[RingCapacity];
    private readonly int _sampleRate;
    private readonly string _recordingsDir;

    // SPSC cursors — monotonic. _w written by the audio thread, _r by the flush
    // thread; each reads the other via Volatile for visibility/ordering.
    private long _w;
    private long _r;

    private volatile bool _recording;
    private volatile float _lastPeak;
    private long _droppedSamples;

    private Thread? _flushThread;
    private WavSink? _sink;
    private string? _currentFile;

    public RecorderEngine(int sampleRate, string recordingsDir)
    {
        _sampleRate = sampleRate;
        _recordingsDir = recordingsDir;
        Directory.CreateDirectory(_recordingsDir);
    }

    public bool IsRecording => _recording;
    public string? CurrentFileName => _currentFile is null ? null : Path.GetFileName(_currentFile);
    public float LastPeak => _lastPeak;
    public long DroppedSamples => Interlocked.Read(ref _droppedSamples);

    public double ElapsedSeconds
    {
        get
        {
            var sink = _sink;
            return sink is null ? 0 : (double)sink.SampleCount / _sampleRate;
        }
    }

    /// <summary>Audio-thread entry for the currently-selected source. No alloc,
    /// no lock, no IO.</summary>
    public void Feed(ReadOnlySpan<float> samples)
    {
        // Cheap peak for the level meter (always, even when idle).
        float peak = 0f;
        for (int i = 0; i < samples.Length; i++)
        {
            float a = samples[i] < 0 ? -samples[i] : samples[i];
            if (a > peak) peak = a;
        }
        _lastPeak = peak;

        if (!_recording) return;

        long w = _w;
        long r = Volatile.Read(ref _r);
        long free = RingCapacity - (w - r);
        if (free < samples.Length)
        {
            // Overrun (flush thread fell behind). Drop this block to keep the
            // file intact rather than overwrite unread samples.
            Interlocked.Add(ref _droppedSamples, samples.Length);
            return;
        }

        int start = (int)(w & RingMask);
        int first = Math.Min(samples.Length, RingCapacity - start);
        samples[..first].CopyTo(_ring.AsSpan(start, first));
        if (first < samples.Length)
            samples[first..].CopyTo(_ring.AsSpan(0, samples.Length - first));

        Volatile.Write(ref _w, w + samples.Length);
    }

    /// <summary>Start a new recording. Returns the file name. Control thread.</summary>
    public string Start()
    {
        if (_recording) throw new InvalidOperationException("already recording");

        var name = $"rx-{DateTime.Now:yyyyMMdd-HHmmss}.wav";
        var path = Path.Combine(_recordingsDir, name);
        _sink = new WavSink(path, _sampleRate);
        _currentFile = path;

        Volatile.Write(ref _r, 0);
        Volatile.Write(ref _w, 0);
        Interlocked.Exchange(ref _droppedSamples, 0);

        _recording = true;
        _flushThread = new Thread(FlushLoop) { IsBackground = true, Name = "rx-recorder-flush" };
        _flushThread.Start();
        return name;
    }

    /// <summary>Stop the current recording. Returns (file, samples) or null. Control thread.</summary>
    public (string File, long Samples)? Stop()
    {
        if (!_recording) return null;
        _recording = false;
        _flushThread?.Join(2000);
        _flushThread = null;

        var sink = _sink;
        _sink = null;
        long samples = sink?.SampleCount ?? 0;
        sink?.Dispose();

        var path = _currentFile;
        var file = path is null ? null : Path.GetFileName(path);
        _currentFile = null;

        // The selected source produced nothing (e.g. TX MIC with no live mic,
        // or TX AIR with the TX monitor off) — discard the empty 44-byte WAV
        // rather than litter the folder with useless headers.
        if (samples == 0 && path is not null)
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }

        return file is null ? null : (file, samples);
    }

    private void FlushLoop()
    {
        // Thread-owned scratch — copy out of the ring then write, so the audio
        // thread never waits on us.
        var scratch = new float[8192];
        while (_recording)
        {
            if (!DrainOnce(scratch)) Thread.Sleep(8);
        }
        // Final drain after stop signalled.
        while (DrainOnce(scratch)) { }
    }

    private bool DrainOnce(float[] scratch)
    {
        long w = Volatile.Read(ref _w);
        long r = _r;
        long avail = w - r;
        if (avail <= 0) return false;

        int n = (int)Math.Min(avail, scratch.Length);
        int start = (int)(r & RingMask);
        int first = Math.Min(n, RingCapacity - start);
        _ring.AsSpan(start, first).CopyTo(scratch.AsSpan(0, first));
        if (first < n)
            _ring.AsSpan(0, n - first).CopyTo(scratch.AsSpan(first, n - first));

        _sink?.Append(scratch.AsSpan(0, n));
        Volatile.Write(ref _r, r + n);
        return true;
    }

    public void Dispose()
    {
        try { Stop(); } catch { /* best effort */ }
    }
}

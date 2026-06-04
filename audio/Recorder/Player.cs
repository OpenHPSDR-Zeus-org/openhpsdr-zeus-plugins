// SPDX-License-Identifier: GPL-2.0-or-later
//
// Player — plays a recorded clip back through the host playback sink. Reads the
// WAV off the control thread, then a background thread paces 960-sample blocks
// at real time. Each block goes on the air when the operator holds MOX,
// otherwise to the local monitor — mirroring the built-in recorder. The host
// sink never keys the radio; the operator does.
// Copyright (C) 2026 contributors.

using Zeus.Plugins.Contracts.Audio;

namespace Openhpsdr.Zeus.Samples.Recorder;

public sealed class Player : IDisposable
{
    private const int BlockSamples = 960;            // 20 ms @ 48 kHz (host TX mic block)
    private readonly IAudioPlaybackSink _sink;
    private Thread? _thread;
    private volatile bool _playing;
    private volatile bool _wentOnAir;
    private string? _currentFile;

    public Player(IAudioPlaybackSink sink) => _sink = sink;

    public bool IsPlaying => _playing;
    public string? CurrentFile => _currentFile;
    public bool WentOnAir => _wentOnAir;

    /// <summary>Start playing the given file. Returns false if already playing.</summary>
    public bool Start(string path)
    {
        if (_playing) return false;
        var (samples, rate) = WavReader.ReadAllSamples(path);
        _currentFile = Path.GetFileName(path);
        _wentOnAir = false;
        _playing = true;
        _thread = new Thread(() => Pump(samples, rate)) { IsBackground = true, Name = "recorder-playback" };
        _thread.Start();
        return true;
    }

    public void Stop()
    {
        _playing = false;
        _thread?.Join(1500);
        _thread = null;
        _currentFile = null;
    }

    private void Pump(float[] samples, int rate)
    {
        // Decide once at the start (like the built-in recorder): keyed = on the
        // air for the whole clip; otherwise a local monitor session. Unkeying
        // mid-clip stops an on-air playback.
        bool onAir = _sink.IsMoxOn;
        _wentOnAir = onAir;
        IDisposable? monitor = onAir ? null : _sink.BeginLocalMonitor();
        try
        {
            double blockMs = 1000.0 * BlockSamples / rate;
            int pos = 0;
            long startTick = Environment.TickCount64;
            int blocks = 0;

            while (_playing && pos < samples.Length)
            {
                if (onAir && !_sink.IsMoxOn) break; // operator unkeyed
                int n = Math.Min(BlockSamples, samples.Length - pos);
                var span = samples.AsSpan(pos, n);

                if (onAir) _sink.PlayOnAir(span, rate);
                else _sink.PlayLocal(span, rate);
                pos += n;
                blocks++;

                long target = startTick + (long)(blocks * blockMs);
                long wait = target - Environment.TickCount64;
                if (wait > 0) Thread.Sleep((int)wait);
            }
        }
        finally
        {
            monitor?.Dispose();
            _playing = false;
            _currentFile = null;
        }
    }

    public void Dispose() => Stop();
}

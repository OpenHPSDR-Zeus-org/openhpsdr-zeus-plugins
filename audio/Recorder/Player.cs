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
            if (onAir) PumpOnAir(samples, rate);
            else PumpLocal(samples, rate);
        }
        finally
        {
            monitor?.Dispose();
            _playing = false;
            _currentFile = null;
        }
    }

    // Local monitor: no wall-clock pacing. We push blocks as fast as the host's
    // monitor ring accepts them; when it's full PlayLocal returns false and we
    // wait briefly, so the host's RX tick clock (the consumer) paces playback.
    // One clock drives both sides → no drift, no periodic drops, no clicks.
    private void PumpLocal(float[] samples, int rate)
    {
        int pos = 0;
        while (_playing && pos < samples.Length)
        {
            int n = Math.Min(BlockSamples, samples.Length - pos);
            if (_sink.PlayLocal(samples.AsSpan(pos, n), rate)) pos += n;
            else Thread.Sleep(2); // ring full — let the consumer drain (backpressure)
        }
        // Wait out the buffered tail so we don't report 'finished' (and clear
        // the panel) while the last ~few hundred ms are still being heard.
        int guard = 0;
        while (_playing && _sink.LocalMonitorBacklog > 0 && guard++ < 500)
            Thread.Sleep(10);
    }

    // On-air: paced to real time so the TX mic accumulator isn't overrun, and
    // stops if the operator unkeys mid-clip. Mirrors the built-in recorder.
    private void PumpOnAir(float[] samples, int rate)
    {
        double blockMs = 1000.0 * BlockSamples / rate;
        int pos = 0;
        long startTick = Environment.TickCount64;
        int blocks = 0;
        while (_playing && pos < samples.Length)
        {
            if (!_sink.IsMoxOn) break; // operator unkeyed
            int n = Math.Min(BlockSamples, samples.Length - pos);
            _sink.PlayOnAir(samples.AsSpan(pos, n), rate);
            pos += n;
            blocks++;
            long wait = startTick + (long)(blocks * blockMs) - Environment.TickCount64;
            if (wait > 0) Thread.Sleep((int)wait);
        }
    }

    public void Dispose() => Stop();
}

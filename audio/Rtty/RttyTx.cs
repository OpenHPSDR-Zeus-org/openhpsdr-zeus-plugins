// SPDX-License-Identifier: GPL-2.0-or-later
//
// RttyTx — transmit pump. Keys the radio through the host RadioController
// (MoxSource.Plugin), streams the modulated AFSK to the playback sink paced at
// real time so the TX mic accumulator isn't overrun, then unkeys. The host
// keys via the plugin's ControlRadio grant; on-air audio still flows through
// the normal TX chain. Abort cuts the stream and drops MOX. The radio must be
// in a voice/digital mode (USB/DIGU) for the AFSK to modulate.
// Copyright (C) 2026 contributors.

using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Audio;

namespace Openhpsdr.Zeus.Samples.Rtty;

public sealed class RttyTx : IDisposable
{
    private const int BlockSamples = 960;        // 20 ms @ 48 kHz
    private const int KeyLeadMs = 200;           // MOX up before audio
    private const int DrainTailMs = 250;         // let last audio out before unkey

    private readonly IAudioPlaybackSink _sink;
    private readonly IRadioController _radio;
    private readonly RttyModulator _mod;
    private readonly Action<string> _log;

    private Thread? _thread;
    private volatile bool _sending;
    private volatile bool _abort;
    private volatile string _current = "";

    public RttyTx(IAudioPlaybackSink sink, IRadioController radio, int sampleRate, Action<string> log)
    {
        _sink = sink;
        _radio = radio;
        _mod = new RttyModulator(sampleRate);
        _log = log;
    }

    public bool IsSending => _sending;
    public string Current => _current;

    /// <summary>Begin transmitting. Returns false if already sending or text empty.</summary>
    public bool Send(string text, double markHz, double shiftHz, double baud, bool reverse, bool usos)
    {
        if (_sending || string.IsNullOrEmpty(text)) return false;
        var samples = _mod.Render(text, markHz, shiftHz, baud, reverse, usos);
        if (samples.Length == 0) return false;
        _current = text;
        _abort = false;
        _sending = true;
        _thread = new Thread(() => Pump(samples)) { IsBackground = true, Name = "rtty-tx" };
        _thread.Start();
        return true;
    }

    public void Abort() => _abort = true;

    private void Pump(float[] samples)
    {
        double blockMs = 1000.0 * BlockSamples / 48000.0;
        try
        {
            _radio.SetMoxAsync(true).GetAwaiter().GetResult();
            Thread.Sleep(KeyLeadMs);

            long start = Environment.TickCount64;
            int pos = 0, blocks = 0;
            while (!_abort && pos < samples.Length)
            {
                int n = Math.Min(BlockSamples, samples.Length - pos);
                _sink.PlayOnAir(samples.AsSpan(pos, n), 48000);
                pos += n; blocks++;
                long wait = start + (long)(blocks * blockMs) - Environment.TickCount64;
                if (wait > 0) Thread.Sleep((int)wait);
            }
            Thread.Sleep(DrainTailMs);
        }
        catch (Exception ex)
        {
            _log($"RTTY TX error: {ex.Message}");
        }
        finally
        {
            try { _radio.SetMoxAsync(false).GetAwaiter().GetResult(); } catch { /* best effort */ }
            _current = "";
            _sending = false;
        }
    }

    public void Dispose()
    {
        Abort();
        _thread?.Join(2000);
    }
}

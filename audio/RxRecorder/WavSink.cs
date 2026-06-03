// SPDX-License-Identifier: GPL-2.0-or-later
//
// Minimal streaming RIFF/WAVE writer (mono, 32-bit IEEE float). Self-contained
// so the plugin carries no host-internal dependency. NOT thread-safe — the
// owning RecorderEngine writes from a single background flush thread.
// Copyright (C) 2026 contributors.

using System.Text;

namespace Openhpsdr.Zeus.Samples.RxRecorder;

public sealed class WavSink : IDisposable
{
    private readonly FileStream _fs;
    private readonly BinaryWriter _bw;
    private long _dataBytes;
    private bool _disposed;

    public WavSink(string path, int sampleRate)
    {
        _fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        _bw = new BinaryWriter(_fs, Encoding.ASCII, leaveOpen: false);
        WriteHeaderPlaceholder(sampleRate);
    }

    public long SampleCount { get; private set; }

    /// <summary>Append a block of mono float32 samples. Little-endian.</summary>
    public void Append(ReadOnlySpan<float> samples)
    {
        for (int i = 0; i < samples.Length; i++)
            _bw.Write(samples[i]); // BinaryWriter.Write(float) is little-endian
        _dataBytes += (long)samples.Length * 4;
        SampleCount += samples.Length;
    }

    private void WriteHeaderPlaceholder(int sampleRate)
    {
        const short channels = 1;
        const short bitsPerSample = 32;
        int byteRate = sampleRate * channels * (bitsPerSample / 8);
        short blockAlign = (short)(channels * (bitsPerSample / 8));

        _bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        _bw.Write(0);                                  // chunk size — patched on dispose
        _bw.Write(Encoding.ASCII.GetBytes("WAVE"));
        _bw.Write(Encoding.ASCII.GetBytes("fmt "));
        _bw.Write(16);                                 // PCM/float fmt chunk size
        _bw.Write((short)3);                           // WAVE_FORMAT_IEEE_FLOAT
        _bw.Write(channels);
        _bw.Write(sampleRate);
        _bw.Write(byteRate);
        _bw.Write(blockAlign);
        _bw.Write(bitsPerSample);
        _bw.Write(Encoding.ASCII.GetBytes("data"));
        _bw.Write(0);                                  // data size — patched on dispose
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _bw.Flush();
            // Patch RIFF chunk size (file length - 8) and data size.
            _fs.Seek(4, SeekOrigin.Begin);
            _bw.Write((int)(_dataBytes + 36));
            _fs.Seek(40, SeekOrigin.Begin);
            _bw.Write((int)_dataBytes);
            _bw.Flush();
        }
        catch { /* best effort header patch */ }
        finally
        {
            _bw.Dispose();
        }
    }
}

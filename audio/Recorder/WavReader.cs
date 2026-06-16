// SPDX-License-Identifier: GPL-2.0-or-later
//
// Minimal RIFF/WAVE reader → mono float32. Tolerates extra chunks; accepts
// float32 (tag 3) or 16-bit PCM (tag 1); downmixes stereo by averaging.
// Copyright (C) 2026 contributors.

using System.Text;

namespace Openhpsdr.Zeus.Samples.Recorder;

public static class WavReader
{
    public static (float[] Samples, int SampleRate) ReadAllSamples(string path)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs, Encoding.ASCII, leaveOpen: false);

        if (new string(br.ReadChars(4)) != "RIFF") throw new InvalidDataException("not RIFF");
        br.ReadInt32(); // riff size
        if (new string(br.ReadChars(4)) != "WAVE") throw new InvalidDataException("not WAVE");

        short format = 0, channels = 1, bits = 16;
        int sampleRate = 48000;
        byte[]? data = null;

        while (fs.Position + 8 <= fs.Length)
        {
            var id = new string(br.ReadChars(4));
            int size = br.ReadInt32();
            if (id == "fmt ")
            {
                format = br.ReadInt16();
                channels = br.ReadInt16();
                sampleRate = br.ReadInt32();
                br.ReadInt32();            // byte rate
                br.ReadInt16();            // block align
                bits = br.ReadInt16();
                int rest = size - 16;
                if (rest > 0) br.ReadBytes(rest);
            }
            else if (id == "data")
            {
                data = br.ReadBytes(size);
            }
            else
            {
                br.ReadBytes(size + (size & 1)); // skip (chunks are word-aligned)
            }
        }

        if (data is null) throw new InvalidDataException("no data chunk");

        int ch = Math.Max((short)1, channels);
        float[] interleaved;
        if (format == 3 && bits == 32)
        {
            int n = data.Length / 4;
            interleaved = new float[n];
            Buffer.BlockCopy(data, 0, interleaved, 0, n * 4);
        }
        else if (format == 1 && bits == 16)
        {
            int n = data.Length / 2;
            interleaved = new float[n];
            for (int i = 0; i < n; i++)
                interleaved[i] = (short)(data[i * 2] | (data[i * 2 + 1] << 8)) / 32768f;
        }
        else
        {
            throw new InvalidDataException($"unsupported WAV format tag={format} bits={bits}");
        }

        if (ch == 1) return (interleaved, sampleRate);

        int frames = interleaved.Length / ch;
        var mono = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            float acc = 0f;
            for (int c = 0; c < ch; c++) acc += interleaved[f * ch + c];
            mono[f] = acc / ch;
        }
        return (mono, sampleRate);
    }
}

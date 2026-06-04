// SPDX-License-Identifier: GPL-2.0-or-later
//
// RttyModulator — turns text into a continuous-phase AFSK Baudot waveform
// (mono float32 @ host rate) for transmission through the host playback sink.
// Mirrors the demodulator's conventions exactly (LSB-first data bits, ITA2
// LETTERS/FIGURES shift, unshift-on-space, reverse polarity) so a loopback
// decodes cleanly. Continuous phase across tone changes keeps the spectrum
// clean (no key clicks). Copyright (C) 2026 contributors.

using System.Collections.Generic;

namespace Openhpsdr.Zeus.Samples.Rtty;

public sealed class RttyModulator
{
    private readonly int _fs;
    public RttyModulator(int sampleRate) => _fs = sampleRate;

    /// <summary>Render a message to AFSK samples. Idle (mark) lead-in and
    /// tail bracket the data so the receiver locks before the first start bit
    /// and isn't truncated on the last stop bit.</summary>
    public float[] Render(
        string text, double markHz, double shiftHz, double baud,
        bool reverse, bool usos, double amplitude = 0.4,
        int preambleBits = 24, int tailBits = 6)
    {
        double spaceHz = markHz + shiftHz;
        double markTone = reverse ? spaceHz : markHz;   // logic 1 (mark)
        double spaceTone = reverse ? markHz : spaceHz;  // logic 0 (space)
        double samplesPerBit = _fs / (baud <= 0 ? 45.45 : baud);

        // Bit list: (isMark, lengthInBits). 1.5 stop bits use a fractional len.
        var bits = new List<(bool mark, double len)>();
        for (int i = 0; i < preambleBits; i++) bits.Add((true, 1.0));

        bool figs = false;
        void Frame(int code)
        {
            bits.Add((false, 1.0));                          // start bit (space)
            for (int i = 0; i < 5; i++) bits.Add((((code >> i) & 1) == 1, 1.0)); // data LSB-first
            bits.Add((true, 1.5));                           // 1.5 stop bits (mark)
        }

        foreach (char ch in text)
        {
            if (ch == '\n') { Frame(0x08); Frame(0x02); continue; } // CR + LF
            if (ch == '\r') continue;
            if (!Baudot.TryEncode(ch, out int code, out bool needFigs)) continue;
            if (needFigs != figs) { Frame(needFigs ? Baudot.Figs : Baudot.Ltrs); figs = needFigs; }
            Frame(code);
            if (usos && ch == ' ') figs = false; // receiver unshifts on space — mirror it
        }

        for (int i = 0; i < tailBits; i++) bits.Add((true, 1.0));

        // Continuous-phase tone rendering.
        double acc = 0;
        foreach (var (_, len) in bits) { acc += samplesPerBit * len; }
        var outv = new float[(int)acc + 4];
        int pos = 0; double phase = 0, frac = 0;
        foreach (var (mark, len) in bits)
        {
            double f = mark ? markTone : spaceTone;
            frac += samplesPerBit * len;
            int n = (int)frac; frac -= n;
            double w = 2 * System.Math.PI * f / _fs;
            for (int k = 0; k < n && pos < outv.Length; k++)
            {
                outv[pos++] = (float)(amplitude * System.Math.Sin(phase));
                phase += w;
            }
        }
        if (pos == outv.Length) return outv;
        var trimmed = new float[pos];
        System.Array.Copy(outv, trimmed, pos);
        return trimmed;
    }
}

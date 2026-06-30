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

using System.Buffers.Binary;
using Zeus.Server.Voyeur;
using Xunit;

namespace Openhpsdr.Zeus.Plugins.Voyeur.Tests.Vad;

/// <summary>
/// Proves the VAD-refine HARD FAIL-SAFE: when VAD returns null/empty spans (or a
/// trim can't be produced) the pipeline falls through to the ORIGINAL full WAV —
/// VAD NEVER skips transcription. Only when VAD yields usable speech is the saved
/// over trimmed to a throw-away temp; the original recording is never modified.
/// Exercises <see cref="VoyeurTranscriptionService.ResolveSttWav"/> directly (no
/// process, no socket, no store).
/// </summary>
public sealed class VadTrimFallthroughTests
{
    [Fact]
    public void NullSpans_FallsThroughToFullWav_NoTemp()
    {
        var resolved = VoyeurTranscriptionService.ResolveSttWav(
            "/orig/over.wav", spans: null, overSeconds: 5.0, out var temp);

        Assert.Equal("/orig/over.wav", resolved); // original WAV is transcribed
        Assert.Null(temp);                          // nothing to clean up
    }

    [Fact]
    public void EmptySpans_FallsThroughToFullWav_NoTemp()
    {
        var resolved = VoyeurTranscriptionService.ResolveSttWav(
            "/orig/over.wav", spans: Array.Empty<SileroVad.SpeechSpan>(), overSeconds: 5.0, out var temp);

        Assert.Equal("/orig/over.wav", resolved);
        Assert.Null(temp);
    }

    [Fact]
    public void UnreadableWav_WithValidSpans_FallsThroughToFullWav_NoTemp()
    {
        // Spans are usable but the source can't be read → trim fails → still the
        // original WAV, and we still transcribe it.
        var missing = "/no/such/over-" + Guid.NewGuid().ToString("N") + ".wav";
        var spans = new[] { new SileroVad.SpeechSpan(0.5, 2.0) };

        var resolved = VoyeurTranscriptionService.ResolveSttWav(missing, spans, overSeconds: 5.0, out var temp);

        Assert.Equal(missing, resolved);
        Assert.Null(temp);
    }

    [Fact]
    public void ValidSpans_TrimsToShorterTempWav_OriginalUntouched()
    {
        var orig = WriteFloatWav(rate: 16000, seconds: 3.0); // 3 s of float32 mono
        var origLen = new FileInfo(orig).Length;
        try
        {
            // Speech only in the middle second → bracket ~[0.8,2.2] after guard.
            var spans = new[] { new SileroVad.SpeechSpan(1.0, 2.0) };
            var resolved = VoyeurTranscriptionService.ResolveSttWav(orig, spans, overSeconds: 3.0, out var temp);

            Assert.NotNull(temp);
            Assert.Equal(temp, resolved);          // STT runs on the trimmed copy
            Assert.True(File.Exists(temp));
            Assert.True(new FileInfo(temp!).Length < origLen); // genuinely trimmed
            Assert.Equal(origLen, new FileInfo(orig).Length);  // original untouched

            File.Delete(temp!);
        }
        finally { File.Delete(orig); }
    }

    // Minimal 32-bit-float mono WAV (matches WavWriter's output format) so
    // WhisperWav.TrimToTemp can read it back and re-window it.
    private static string WriteFloatWav(int rate, double seconds)
    {
        int frames = Math.Max(1, (int)(rate * seconds));
        int dataBytes = frames * 4;
        var path = Path.Combine(Path.GetTempPath(), "zeus-trim-src-" + Guid.NewGuid().ToString("N") + ".wav");

        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        bw.Write("RIFF"u8);
        bw.Write((uint)(36 + dataBytes));
        bw.Write("WAVE"u8);
        bw.Write("fmt "u8);
        bw.Write(16u);
        bw.Write((ushort)3);          // IEEE float
        bw.Write((ushort)1);          // mono
        bw.Write((uint)rate);
        bw.Write((uint)(rate * 4));   // byte rate
        bw.Write((ushort)4);          // block align
        bw.Write((ushort)32);         // bits
        bw.Write("data"u8);
        bw.Write((uint)dataBytes);

        Span<byte> buf = stackalloc byte[4];
        for (int i = 0; i < frames; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(buf, (float)Math.Sin(i * 0.01));
            bw.Write(buf);
        }
        return path;
    }
}

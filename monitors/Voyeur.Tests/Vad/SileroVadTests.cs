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
using Microsoft.Extensions.Logging;
using Zeus.Server.Voyeur;

namespace Openhpsdr.Zeus.Plugins.Voyeur.Tests.Vad;

public sealed class SileroVadTests
{
    // No-op logger so we add no logging package dependency to the test project.
    private sealed class NoopLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) { }
    }

    // ---- ParseSpans (pure) ---------------------------------------------------

    [Fact]
    public void ParseSpans_SingleLine()
    {
        var spans = SileroVad.ParseSpans("1.230 -- 4.560\n");
        var s = Assert.Single(spans);
        Assert.Equal(1.230, s.StartSeconds, 3);
        Assert.Equal(4.560, s.EndSeconds, 3);
        Assert.Equal(3.330, s.DurationSeconds, 3);
    }

    [Fact]
    public void ParseSpans_MultipleLines_IgnoresNoise()
    {
        const string stderr =
            "Loading model...\n" +
            "0.000 -- 1.500\n" +
            "some progress 42%\n" +
            "  2.250 -- 3.875  \n" +
            "Done.\n";
        var spans = SileroVad.ParseSpans(stderr);
        Assert.Equal(2, spans.Count);
        Assert.Equal(0.000, spans[0].StartSeconds, 3);
        Assert.Equal(1.500, spans[0].EndSeconds, 3);
        Assert.Equal(2.250, spans[1].StartSeconds, 3);
        Assert.Equal(3.875, spans[1].EndSeconds, 3);
    }

    [Fact]
    public void ParseSpans_HandlesCrLf()
    {
        var spans = SileroVad.ParseSpans("0.100 -- 0.900\r\n1.000 -- 2.000\r\n");
        Assert.Equal(2, spans.Count);
    }

    [Fact]
    public void ParseSpans_DropsReversedAndZeroLength()
    {
        var spans = SileroVad.ParseSpans("5.000 -- 5.000\n4.000 -- 3.000\n1.0 -- 2.0\n");
        var s = Assert.Single(spans);
        Assert.Equal(1.0, s.StartSeconds, 3);
        Assert.Equal(2.0, s.EndSeconds, 3);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no spans here\njust banners\n")]
    public void ParseSpans_EmptyOrNoMatch_ReturnsEmpty(string? stderr)
    {
        Assert.Empty(SileroVad.ParseSpans(stderr));
    }

    // ---- Bracket (pure) ------------------------------------------------------

    [Fact]
    public void Bracket_Empty_ReturnsNull()
    {
        Assert.Null(SileroVad.Bracket(Array.Empty<SileroVad.SpeechSpan>()));
    }

    [Fact]
    public void Bracket_SpansMinStartMaxEnd()
    {
        var spans = new[]
        {
            new SileroVad.SpeechSpan(1.0, 2.0),
            new SileroVad.SpeechSpan(3.0, 5.0),
            new SileroVad.SpeechSpan(2.5, 2.9),
        };
        var b = SileroVad.Bracket(spans);
        Assert.NotNull(b);
        Assert.Equal(1.0, b!.Value.StartSeconds, 3);
        Assert.Equal(5.0, b.Value.EndSeconds, 3);
    }

    [Fact]
    public void Bracket_GuardPadsButClampsAtZeroAndEnd()
    {
        var spans = new[] { new SileroVad.SpeechSpan(0.2, 4.9) };
        var b = SileroVad.Bracket(spans, guardSeconds: 0.5, clampEndSeconds: 5.0);
        Assert.NotNull(b);
        Assert.Equal(0.0, b!.Value.StartSeconds, 3);  // 0.2 - 0.5 clamped to 0
        Assert.Equal(5.0, b.Value.EndSeconds, 3);     // 4.9 + 0.5 clamped to 5.0
    }

    // ---- RunAsync orchestration (fake runner, no real process) --------------

    [Fact]
    public async Task RunAsync_HappyPath_ReturnsParsedSpans()
    {
        var wav = WriteWav16kMono(0.25); // 250 ms, already 16 kHz → no resample
        try
        {
            IReadOnlyList<string>? captured = null;
            var vad = new SileroVad(new NoopLogger(),
                (cli, args, timeout, ct) =>
                {
                    captured = args;
                    return Task.FromResult((0, "0.000 -- 0.120\n0.150 -- 0.240\n"));
                });

            var spans = await vad.RunAsync(
                "/fake/sherpa-onnx-vad", "/fake/silero_vad.onnx", wav,
                new SegSettings { MaxOverSeconds = 300 }, TimeSpan.FromSeconds(5), CancellationToken.None);

            Assert.NotNull(spans);
            Assert.Equal(2, spans!.Count);
            Assert.NotNull(captured);
            Assert.Contains(captured!, a => a == "--silero-vad-model=/fake/silero_vad.onnx");
            Assert.Contains(captured!, a => a.StartsWith("--silero-vad-max-speech-duration="));
            // input + throw-away output are the two trailing positional args.
            Assert.Equal(wav, captured![^2]);
        }
        finally { File.Delete(wav); }
    }

    [Fact]
    public async Task RunAsync_NonZeroExit_ReturnsNull()
    {
        var wav = WriteWav16kMono(0.1);
        try
        {
            var vad = new SileroVad(new NoopLogger(),
                (cli, args, timeout, ct) => Task.FromResult((1, "0.0 -- 1.0\n")));
            var spans = await vad.RunAsync("/fake/cli", "/fake/model", wav,
                new SegSettings(), TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.Null(spans);
        }
        finally { File.Delete(wav); }
    }

    [Fact]
    public async Task RunAsync_RunnerThrows_ReturnsNullNeverPropagates()
    {
        var wav = WriteWav16kMono(0.1);
        try
        {
            var vad = new SileroVad(new NoopLogger(),
                (cli, args, timeout, ct) => throw new InvalidOperationException("boom"));
            var spans = await vad.RunAsync("/fake/cli", "/fake/model", wav,
                new SegSettings(), TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.Null(spans);
        }
        finally { File.Delete(wav); }
    }

    [Fact]
    public async Task RunAsync_MissingWav_ReturnsNull()
    {
        bool ranCli = false;
        var vad = new SileroVad(new NoopLogger(),
            (cli, args, timeout, ct) => { ranCli = true; return Task.FromResult((0, "")); });
        var spans = await vad.RunAsync("/fake/cli", "/fake/model",
            Path.Combine(Path.GetTempPath(), "zeus-vad-missing-" + Guid.NewGuid().ToString("N") + ".wav"),
            new SegSettings(), TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Null(spans);
        Assert.False(ranCli); // never launched the child for an unreadable input
    }

    [Fact]
    public async Task RunAsync_OmitsMaxDurationArg_WhenNonPositive()
    {
        var wav = WriteWav16kMono(0.1);
        try
        {
            IReadOnlyList<string>? captured = null;
            var vad = new SileroVad(new NoopLogger(),
                (cli, args, timeout, ct) => { captured = args; return Task.FromResult((0, "")); });
            await vad.RunAsync("/fake/cli", "/fake/model", wav,
                new SegSettings { MaxOverSeconds = 0 }, TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.NotNull(captured);
            Assert.DoesNotContain(captured!, a => a.StartsWith("--silero-vad-max-speech-duration="));
        }
        finally { File.Delete(wav); }
    }

    [Fact]
    public async Task DetectAsync_WhenUnavailableOrBogusInput_ReturnsNull()
    {
        // No engine/model installed in the test env (and a bogus path even if one
        // were) — must degrade to null so the caller keeps the energy gate.
        var vad = new SileroVad(new NoopLogger());
        var spans = await vad.DetectAsync(
            Path.Combine(Path.GetTempPath(), "zeus-vad-none-" + Guid.NewGuid().ToString("N") + ".wav"),
            new SegSettings(), TimeSpan.FromSeconds(2), CancellationToken.None);
        Assert.Null(spans);
    }

    // --- helpers --------------------------------------------------------------

    // Minimal 16 kHz mono 16-bit PCM WAV so WhisperWav.Prepare returns it
    // unchanged (no resample temp file). Content is a low-level sine, but VAD is
    // faked here so only the WAV header parse matters.
    private static string WriteWav16kMono(double seconds)
    {
        const int rate = 16000, channels = 1, bits = 16;
        int frames = Math.Max(1, (int)(rate * seconds));
        int dataBytes = frames * 2;
        var path = Path.Combine(Path.GetTempPath(), "zeus-vad-test-" + Guid.NewGuid().ToString("N") + ".wav");

        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        bw.Write("RIFF"u8);
        bw.Write((uint)(36 + dataBytes));
        bw.Write("WAVE"u8);
        bw.Write("fmt "u8);
        bw.Write(16u);
        bw.Write((ushort)1);          // PCM
        bw.Write((ushort)channels);
        bw.Write((uint)rate);
        bw.Write((uint)(rate * channels * (bits / 8)));
        bw.Write((ushort)(channels * (bits / 8)));
        bw.Write((ushort)bits);
        bw.Write("data"u8);
        bw.Write((uint)dataBytes);

        Span<byte> buf = stackalloc byte[2];
        for (int i = 0; i < frames; i++)
        {
            short v = (short)(Math.Sin(i * 0.05) * 4000);
            BinaryPrimitives.WriteInt16LittleEndian(buf, v);
            bw.Write(buf);
        }
        return path;
    }
}

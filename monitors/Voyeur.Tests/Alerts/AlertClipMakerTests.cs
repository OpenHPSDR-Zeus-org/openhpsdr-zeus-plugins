// SPDX-License-Identifier: GPL-2.0-or-later

using Zeus.Server.Voyeur.Alerts;
using Xunit;

namespace Openhpsdr.Zeus.Plugins.Voyeur.Tests.Alerts;

public class AlertClipMakerTests
{
    // 16 kHz mono PCM16: byteRate = 32000, blockAlign = 2.
    private const int ByteRate = 32000;
    private const int BlockAlign = 2;

    [Fact]
    public void CapBytes_caps_to_max_seconds()
    {
        int dataLen = ByteRate * 30;                 // 30 s
        Assert.Equal(ByteRate * 8, AlertClipMaker.CapBytes(dataLen, ByteRate, BlockAlign, 8));
    }

    [Fact]
    public void CapBytes_returns_full_when_shorter_than_cap()
    {
        int dataLen = ByteRate * 3;                   // 3 s
        Assert.Equal(dataLen, AlertClipMaker.CapBytes(dataLen, ByteRate, BlockAlign, 8));
    }

    [Fact]
    public void CapBytes_aligns_down_to_block()
    {
        // An odd byteRate forces a non-aligned want; result must be even (block=2).
        int capped = AlertClipMaker.CapBytes(100_001, 33_333, BlockAlign, 1);
        Assert.Equal(0, capped % BlockAlign);
    }

    [Fact]
    public void CapBytes_zero_data_is_zero()
        => Assert.Equal(0, AlertClipMaker.CapBytes(0, ByteRate, BlockAlign, 8));

    [Fact]
    public void TryMake_null_or_missing_source_returns_null()
    {
        Assert.Null(AlertClipMaker.TryMake(null, 8));
        Assert.Null(AlertClipMaker.TryMake(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N") + ".wav"), 8));
    }

    [Fact]
    public void TryMake_caps_a_long_over_to_the_requested_seconds()
    {
        var src = TestWav.WriteSilence(Temp(), seconds: 12);
        string? clip = null;
        try
        {
            clip = AlertClipMaker.TryMake(src, maxSeconds: 3);
            Assert.NotNull(clip);
            var (rate, dataBytes) = TestWav.ReadInfo(clip!);
            Assert.Equal(16000, rate);
            // ~3 s at 32000 B/s. Allow a small alignment slack.
            Assert.InRange(dataBytes, ByteRate * 3 - BlockAlign, ByteRate * 3 + BlockAlign);
        }
        finally
        {
            File.Delete(src);
            if (clip is not null) File.Delete(clip);
        }
    }

    [Fact]
    public void TryMake_leaves_original_untouched()
    {
        var src = TestWav.WriteSilence(Temp(), seconds: 5);
        var before = new FileInfo(src).Length;
        string? clip = null;
        try
        {
            clip = AlertClipMaker.TryMake(src, maxSeconds: 1);
            Assert.NotNull(clip);
            Assert.NotEqual(Path.GetFullPath(src), Path.GetFullPath(clip!)); // distinct file
            Assert.Equal(before, new FileInfo(src).Length);                  // unchanged
        }
        finally
        {
            File.Delete(src);
            if (clip is not null) File.Delete(clip);
        }
    }

    [Fact]
    public void TryMake_short_over_round_trips_full_length()
    {
        var src = TestWav.WriteSilence(Temp(), seconds: 2);
        string? clip = null;
        try
        {
            clip = AlertClipMaker.TryMake(src, maxSeconds: 30);
            Assert.NotNull(clip);
            var (_, dataBytes) = TestWav.ReadInfo(clip!);
            Assert.Equal(ByteRate * 2, dataBytes);
        }
        finally
        {
            File.Delete(src);
            if (clip is not null) File.Delete(clip);
        }
    }

    private static string Temp() =>
        Path.Combine(Path.GetTempPath(), "voyeur-cliptest-" + Guid.NewGuid().ToString("N") + ".wav");
}

// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus Digital plugin — ported from the in-core tests/Zeus.Server.Tests/Wsjtx/
// WsjtxLiveEmitterTests.cs. The in-core WsjtxManagementService/WsjtxConfigStore
// (LiteDB) is replaced by the plugin's Func<WsjtxLiveConfig> seam; the core's
// two-flag gate (Enabled && SendLiveDecodes) collapses to the plugin config's
// single Enabled. Field-mapping assertions are otherwise verbatim.
// GPL-2.0-or-later.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Pins the live WSJT-X emitter's safety gate and field mapping:
//   - DISABLED ⇒ no datagram is built/sent (the opt-in-egress invariant the
//     whole subsystem rests on);
//   - ENABLED ⇒ exactly one Decode/WSPRDecode datagram per line/spot, with the
//     FT8/FT4 fields mapped correctly.

using System.Buffers.Binary;
using Microsoft.Extensions.Logging.Abstractions;
using Openhpsdr.Zeus.Plugins.Digital;
using Openhpsdr.Zeus.Plugins.Digital.Dsp;
using Openhpsdr.Zeus.Plugins.Digital.Wsjtx;

namespace Openhpsdr.Zeus.Plugins.Digital.Tests;

public sealed class WsjtxLiveEmitterTests
{
    private sealed class MutableConfig
    {
        public WsjtxLiveConfig Value = new();
    }

    private static (WsjtxLiveEmitter emitter, List<byte[]> sent, MutableConfig cfg) Build()
    {
        var cfg = new MutableConfig();
        var sent = new List<byte[]>();
        var emitter = new WsjtxLiveEmitter(
            NullLogger.Instance,
            () => cfg.Value,
            (dg, _) => { sent.Add(dg); return Task.CompletedTask; });
        return (emitter, sent, cfg);
    }

    private static Ft8DecodeBatch SampleFt8Batch() => new(
        Receiver: 0,
        SlotStartUtc: new DateTime(2024, 6, 15, 12, 30, 0, DateTimeKind.Utc),
        Protocol: Ft8Protocol.Ft8,
        Decodes: new[]
        {
            new Ft8DecodeResult(SnrDb: -7.4f, DtSec: 0.2f, FreqHz: 1500.6f, Score: 50, LdpcErrors: 0, Text: "CQ K1ABC FN42"),
            new Ft8DecodeResult(SnrDb: -15f, DtSec: -0.1f, FreqHz: 800f, Score: 10, LdpcErrors: 1, Text: "KB2UKA W1AW -05"),
        });

    private static WsprSpotBatch SampleWsprBatch() => new(
        Receiver: 0,
        SlotStartUtc: new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc),
        DialFreqMhz: 14.0956,
        Spots: new[] { new WsprSpot(SnrDb: -24f, DtSec: 1.5f, FreqMhz: 14.0971f, DriftHz: -1, Message: "KB2UKA FN12 30") });

    [Fact]
    public void Disabled_Emits_Nothing()
    {
        var (emitter, sent, cfg) = Build();
        using (emitter)
        {
            cfg.Value = new WsjtxLiveConfig(Enabled: false);
            emitter.HandleDecodes(SampleFt8Batch());
            emitter.HandleSpots(SampleWsprBatch());
            Assert.Empty(sent);
        }
    }

    [Fact]
    public void Enabled_Emits_One_Datagram_Per_Line()
    {
        var (emitter, sent, cfg) = Build();
        using (emitter)
        {
            cfg.Value = new WsjtxLiveConfig(Enabled: true);
            emitter.HandleDecodes(SampleFt8Batch()); // 2 lines
            emitter.HandleSpots(SampleWsprBatch());   // 1 spot
            Assert.Equal(3, sent.Count);
            // Each is a well-formed WSJT-X datagram (magic header).
            foreach (var dg in sent)
                Assert.Equal(new byte[] { 0xAD, 0xBC, 0xCB, 0xDA }, dg[..4]);
        }
    }

    [Fact]
    public void ReEnable_Resumes_Emitting()
    {
        // The config is read live per batch — flipping the flag off then on must
        // gate and resume without rebuilding the emitter.
        var (emitter, sent, cfg) = Build();
        using (emitter)
        {
            cfg.Value = new WsjtxLiveConfig(Enabled: true);
            emitter.HandleDecodes(SampleFt8Batch());
            Assert.Equal(2, sent.Count);

            cfg.Value = new WsjtxLiveConfig(Enabled: false);
            emitter.HandleDecodes(SampleFt8Batch());
            Assert.Equal(2, sent.Count);   // gated

            cfg.Value = new WsjtxLiveConfig(Enabled: true);
            emitter.HandleDecodes(SampleFt8Batch());
            Assert.Equal(4, sent.Count);   // resumed
        }
    }

    [Fact]
    public void BuildDecodeDatagrams_Maps_Type_And_Fields()
    {
        var dgs = WsjtxLiveEmitter.BuildDecodeDatagrams("Zeus", SampleFt8Batch());
        Assert.Equal(2, dgs.Count);

        // Decode type (2) in the header.
        Assert.Equal(WsjtxMessage.DecodeType, BinaryPrimitives.ReadUInt32BigEndian(dgs[0].AsSpan(8, 4)));

        // 12:30:00 UTC == 45_000_000 ms since midnight.
        Assert.Equal(45_000_000u, WsjtxLiveEmitter.MsSinceUtcMidnight(SampleFt8Batch().SlotStartUtc));
    }

    [Fact]
    public void BuildWsprDatagrams_Sets_Type_10()
    {
        var dgs = WsjtxLiveEmitter.BuildWsprDatagrams("Zeus", SampleWsprBatch());
        Assert.Single(dgs);
        Assert.Equal(WsjtxMessage.WsprDecodeType, BinaryPrimitives.ReadUInt32BigEndian(dgs[0].AsSpan(8, 4)));
    }

    [Theory]
    [InlineData("KB2UKA FN12 30", "KB2UKA", "FN12", 30)]
    [InlineData("VK7/KB2UKA 27", "VK7/KB2UKA", "", 27)]
    [InlineData("<KB2UKA> FN12AB 23", "KB2UKA", "FN12AB", 23)]
    [InlineData("", "", "", 0)]
    public void ParseWsprMessage_Tokenizes(string msg, string call, string grid, int power)
    {
        WsjtxLiveEmitter.ParseWsprMessage(msg, out var c, out var g, out var p);
        Assert.Equal(call, c);
        Assert.Equal(grid, g);
        Assert.Equal(power, p);
    }

    [Theory]
    [InlineData("K1ABC KB2UKA FN12", "KB2UKA", "K1ABC")]    // standard reply: DX is the target
    [InlineData("CQ KB2UKA FN12", "KB2UKA", "")]            // calling CQ: no DX
    [InlineData("", "KB2UKA", "")]
    public void ParseDxCall_Extracts_Target(string txMsg, string deCall, string expected)
    {
        Assert.Equal(expected, WsjtxLiveEmitter.ParseDxCall(txMsg, deCall));
    }
}

// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus Digital plugin — extracted from Zeus core.
// GPL-2.0-or-later.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Pins the SSE fan-out semantics the decode/TX threads rely on: Publish never
// blocks (bounded DropOldest per subscriber), CompleteAll ends every open
// reader promptly, and a Subscribe after shutdown drains immediately instead
// of hanging an HTTP response.

using Openhpsdr.Zeus.Plugins.Digital;

namespace Openhpsdr.Zeus.Plugins.Digital.Tests;

public class DigitalEventStreamTests
{
    [Fact]
    public void Publish_ReachesEverySubscriber_InOrder()
    {
        var stream = new DigitalEventStream();
        using var subA = stream.Subscribe(out var readerA);
        using var subB = stream.Subscribe(out var readerB);

        stream.Publish("ft8decode", "{\"a\":1}");
        stream.Publish("txstatus", "{\"b\":2}");

        foreach (var reader in new[] { readerA, readerB })
        {
            Assert.True(reader.TryRead(out var first));
            Assert.Equal("ft8decode", first.Name);
            Assert.True(reader.TryRead(out var second));
            Assert.Equal("txstatus", second.Name);
            Assert.False(reader.TryRead(out _));
        }
    }

    [Fact]
    public void Publish_WedgedSubscriber_DropsOldest_NeverBlocks()
    {
        var stream = new DigitalEventStream();
        using var sub = stream.Subscribe(out var reader);

        // 70 events into a 64-deep bounded channel with no reads: the six
        // oldest fall off; the writer (decode thread) never blocks.
        for (var i = 0; i < 70; i++)
            stream.Publish("ft8decode", $"{{\"seq\":{i}}}");

        var seen = new List<string>();
        while (reader.TryRead(out var ev)) seen.Add(ev.Json);

        Assert.Equal(64, seen.Count);
        Assert.Equal("{\"seq\":6}", seen[0]);   // 0..5 dropped
        Assert.Equal("{\"seq\":69}", seen[^1]); // newest survives
    }

    [Fact]
    public async Task CompleteAll_EndsOpenReaders()
    {
        var stream = new DigitalEventStream();
        using var sub = stream.Subscribe(out var reader);

        stream.CompleteAll();

        Assert.False(await reader.WaitToReadAsync());
        Assert.Equal(0, stream.SubscriberCount);
    }

    [Fact]
    public async Task Subscribe_AfterCompleteAll_DrainsImmediately()
    {
        var stream = new DigitalEventStream();
        stream.CompleteAll();

        using var sub = stream.Subscribe(out var reader);

        Assert.False(await reader.WaitToReadAsync());
        Assert.Equal(0, stream.SubscriberCount);
    }

    [Fact]
    public void DisposedSubscriber_StopsReceiving_OthersUnaffected()
    {
        var stream = new DigitalEventStream();
        var subA = stream.Subscribe(out var readerA);
        using var subB = stream.Subscribe(out var readerB);

        subA.Dispose();
        stream.Publish("wsprspot", "{}");

        Assert.Equal(1, stream.SubscriberCount);
        Assert.False(readerA.TryRead(out _));
        Assert.True(readerB.TryRead(out var ev));
        Assert.Equal("wsprspot", ev.Name);
    }
}

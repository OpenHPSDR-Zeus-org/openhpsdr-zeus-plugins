// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus Digital · FT8/FT4 — SSE event fan-out.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Replaces the in-core WebSocket broadcasts (0x38 Ft8Decode / 0x39 WsprSpot /
// 0x3A Ft8TxStatus frames).
//
// Publish is called from the decode worker / keyer threads and MUST never
// block them: the subscriber list is snapshotted under a short lock and each
// subscriber is a bounded channel written with TryWrite (DropOldest), so a
// slow or stalled SSE client only loses ITS OWN oldest events — it can never
// stall the decode pipeline or another client. Event payloads are pre-
// serialized JSON strings, byte-shape-identical to the old frame payloads.

using System.Threading.Channels;

namespace Openhpsdr.Zeus.Plugins.Digital;

internal sealed class DigitalEventStream
{
    /// <summary>Per-subscriber buffer depth. Decode batches arrive at most once
    /// per 7.5 s slot and TX status only on edges, so 64 events of headroom is
    /// minutes of backlog before a wedged client starts losing its oldest.</summary>
    private const int SubscriberCapacity = 64;

    internal readonly record struct SseEvent(string Name, string Json);

    private readonly object _sync = new();
    private readonly List<Channel<SseEvent>> _subscribers = new();
    private bool _completed;

    /// <summary>Number of connected SSE subscribers (diagnostic / test).</summary>
    internal int SubscriberCount { get { lock (_sync) return _subscribers.Count; } }

    /// <summary>Register a subscriber. Dispose the returned token to detach.</summary>
    public IDisposable Subscribe(out ChannelReader<SseEvent> reader)
    {
        var channel = Channel.CreateBounded<SseEvent>(new BoundedChannelOptions(SubscriberCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        lock (_sync)
        {
            if (_completed)
                channel.Writer.TryComplete(); // shut down — reader drains to end immediately
            else
                _subscribers.Add(channel);
        }
        reader = channel.Reader;
        return new Subscription(this, channel);
    }

    /// <summary>Fan an event out to every subscriber. Never blocks, never throws.</summary>
    public void Publish(string name, string json)
    {
        Channel<SseEvent>[] targets;
        lock (_sync)
        {
            if (_subscribers.Count == 0) return;
            targets = _subscribers.ToArray();
        }
        var ev = new SseEvent(name, json);
        foreach (var t in targets)
            t.Writer.TryWrite(ev); // bounded DropOldest — a full client loses its oldest
    }

    /// <summary>Plugin shutdown: complete every subscriber channel so open SSE
    /// responses drain and end promptly instead of idling until their next
    /// heartbeat. New Subscribe calls after this complete immediately.</summary>
    public void CompleteAll()
    {
        Channel<SseEvent>[] targets;
        lock (_sync)
        {
            _completed = true;
            targets = _subscribers.ToArray();
            _subscribers.Clear();
        }
        foreach (var t in targets)
            t.Writer.TryComplete();
    }

    private void Unsubscribe(Channel<SseEvent> channel)
    {
        lock (_sync) _subscribers.Remove(channel);
        channel.Writer.TryComplete();
    }

    private sealed class Subscription : IDisposable
    {
        private readonly DigitalEventStream _owner;
        private Channel<SseEvent>? _channel;

        public Subscription(DigitalEventStream owner, Channel<SseEvent> channel)
        {
            _owner = owner;
            _channel = channel;
        }

        public void Dispose()
        {
            var ch = Interlocked.Exchange(ref _channel, null);
            if (ch is not null) _owner.Unsubscribe(ch);
        }
    }
}

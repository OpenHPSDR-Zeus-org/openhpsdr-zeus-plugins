// SPDX-License-Identifier: GPL-2.0-or-later
//
// Test doubles + helpers for the Watchword Alerts module. NO real sockets,
// network, or LiteDB — every external seam is faked in-memory.

using System.Buffers.Binary;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Zeus.Plugins.Contracts;
using Zeus.Server.Voyeur.Alerts;

namespace Openhpsdr.Zeus.Plugins.Voyeur.Tests.Alerts;

/// <summary>An IAlertChannel that records what it was asked to send and can be
/// told to throw the first N attempts (to exercise retry/isolation).</summary>
internal sealed class FakeAlertChannel : IAlertChannel
{
    private int _throwsRemaining;

    public FakeAlertChannel(string name = "fake", bool configured = true, int throwFirst = 0)
    {
        Name = name;
        Configured = configured;
        _throwsRemaining = throwFirst;
    }

    public string Name { get; }
    public bool Configured { get; set; }
    public List<AlertPayload> Sent { get; } = new();
    public int Attempts { get; private set; }

    public bool IsConfigured(AlertSettings settings) => Configured;

    public Task SendAsync(AlertPayload payload, AlertSettings settings, CancellationToken ct)
    {
        Attempts++;
        if (_throwsRemaining > 0) { _throwsRemaining--; throw new InvalidOperationException("fake transient failure"); }
        Sent.Add(payload);
        return Task.CompletedTask;
    }
}

/// <summary>In-memory IPluginSettings (no LiteDB). Round-trips the exact stored
/// object so service tests can inspect what was persisted.</summary>
internal sealed class FakePluginSettings : IPluginSettings
{
    private readonly ConcurrentDictionary<string, object?> _store = new();

    public Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
        => Task.FromResult(_store.TryGetValue(key, out var v) && v is T t ? t : default);

    public Task SetAsync<T>(string key, T value, CancellationToken ct = default)
    {
        _store[key] = value;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        _store.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public T? Peek<T>(string key) => _store.TryGetValue(key, out var v) && v is T t ? t : default;
}

/// <summary>Fake over-context source so the service needs no real VoyeurStore.</summary>
internal sealed class FakeOverContextSource : IOverContextSource
{
    public OverContext? Context { get; set; }
    public string? AudioPath { get; set; }

    public OverContext? GetContext(string sessionId, string segmentId) => Context;
    public string? GetSegmentAudioPath(string segmentId) => AudioPath;
}

/// <summary>ILogger that captures every formatted message so tests can assert a
/// secret was never logged.</summary>
internal sealed class CapturingLogger : ILogger
{
    public List<string> Messages { get; } = new();

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Messages.Add(formatter(state, exception));
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}

/// <summary>Writes a 16 kHz mono 16-bit PCM WAV of the requested duration of
/// silence — used by the clip-maker tests.</summary>
internal static class TestWav
{
    public const int Rate = 16000;

    public static string WriteSilence(string path, double seconds)
    {
        int samples = (int)(Rate * seconds);
        int dataBytes = samples * 2;
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        bw.Write("RIFF"u8);
        bw.Write((uint)(36 + dataBytes));
        bw.Write("WAVE"u8);
        bw.Write("fmt "u8);
        bw.Write(16u);
        bw.Write((ushort)1);                       // PCM
        bw.Write((ushort)1);                       // mono
        bw.Write((uint)Rate);
        bw.Write((uint)(Rate * 2));                // byte rate
        bw.Write((ushort)2);                       // block align
        bw.Write((ushort)16);                      // bits
        bw.Write("data"u8);
        bw.Write((uint)dataBytes);
        Span<byte> z = stackalloc byte[2];
        for (int i = 0; i < samples; i++) bw.Write(z);
        return path;
    }

    /// <summary>Reads (rate, dataBytes) from a RIFF/WAVE file.</summary>
    public static (int rate, int dataBytes) ReadInfo(string path)
    {
        byte[] b = File.ReadAllBytes(path);
        int rate = 0, dataBytes = 0, p = 12;
        while (p + 8 <= b.Length)
        {
            var id = b.AsSpan(p, 4);
            int size = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p + 4, 4));
            int body = p + 8;
            if (size < 0 || body + size > b.Length) size = b.Length - body;
            if (id.SequenceEqual("fmt "u8)) rate = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(body + 4, 4));
            else if (id.SequenceEqual("data"u8)) dataBytes = size;
            p = body + size + (size & 1);
        }
        return (rate, dataBytes);
    }
}

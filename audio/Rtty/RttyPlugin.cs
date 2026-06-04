// SPDX-License-Identifier: GPL-2.0-or-later
//
// RTTY — receive-only Baudot/ITA2 RTTY decoder. Reads the demodulated RX band
// audio through the host's read-only audio tap (no insert slot, never alters
// live audio) and deframes AFSK FSK into text. Standard amateur defaults:
// 2125 Hz mark, 170 Hz shift, 45.45 baud. The transmit half lands in a later
// change once the host exposes plugin TX keying. Copyright (C) 2026 contributors.

using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Audio;
using Zeus.Plugins.Contracts.Extensions;

namespace Openhpsdr.Zeus.Samples.Rtty;

public sealed class RttyPlugin : IZeusPlugin, IRxAudioTapPlugin, IBackendPlugin
{
    private const int MaxText = 4000; // rolling decoded-text window shown in the panel

    private IPluginContext? _ctx;
    private RttyDemod? _demod;

    // Decoded text drained off the demod's lock-free queue on the HTTP thread.
    // The audio thread never touches this lock — it only enqueues chars.
    private readonly object _textLock = new();
    private readonly StringBuilder _text = new(MaxText + 64);

    // Persisted tuning, with amateur defaults.
    private double _markHz = 2125, _shiftHz = 170, _baud = 45.45;
    private bool _reverse, _usos = true;

    // ------------------------------------------------------------------ IZeusPlugin

    public async Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        _ctx = context;
        _markHz = await context.Settings.GetAsync<double?>("markHz", ct) ?? 2125.0;
        _shiftHz = await context.Settings.GetAsync<double?>("shiftHz", ct) ?? 170.0;
        _baud = await context.Settings.GetAsync<double?>("baud", ct) ?? 45.45;
        _reverse = await context.Settings.GetAsync<bool?>("reverse", ct) ?? false;
        _usos = await context.Settings.GetAsync<bool?>("usos", ct) ?? true;
        context.Logger.LogInformation(
            "RTTY initialising; mark={Mark}Hz shift={Shift}Hz baud={Baud} reverse={Rev} usos={Usos}",
            _markHz, _shiftHz, _baud, _reverse, _usos);
    }

    public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;

    // ------------------------------------------------------------------ RX tap

    public AudioPluginRequirements Requirements => new(SampleRate: 48000, Channels: 1, BlockSize: 2048);

    public Task InitializeTapAsync(IAudioHost host, CancellationToken ct)
    {
        var d = new RttyDemod(host.CurrentSampleRate, _markHz, _shiftHz, _baud);
        d.SetParams(_markHz, _shiftHz, _baud, _reverse, _usos);
        _demod = d;
        _ctx?.Logger.LogInformation("RTTY tap init: SR={SampleRate} Hz", host.CurrentSampleRate);
        return Task.CompletedTask;
    }

    public void OnRxAudio(ReadOnlySpan<float> samples, AudioBlockContext ctx)
        => _demod?.Process(samples);

    public Task ShutdownTapAsync(CancellationToken ct)
    {
        _demod = null;
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------ REST

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("status", GetStatus);
        endpoints.MapPost("params", SetParameters);
        endpoints.MapPost("clear", ClearText);
    }

    private IResult GetStatus()
    {
        var d = _demod;
        if (d is not null) DrainInto(d);

        string text;
        lock (_textLock) text = _text.ToString();

        return Results.Ok(new StatusDto
        {
            TapReady = d is not null,
            MarkHz = _markHz,
            ShiftHz = _shiftHz,
            Baud = _baud,
            Reverse = _reverse,
            Usos = _usos,
            MarkLevel = d?.MarkLevel ?? 0,
            SpaceLevel = d?.SpaceLevel ?? 0,
            MarkHigh = d?.MarkHigh ?? true,
            CharCount = d?.CharCount ?? 0,
            Text = text,
        });
    }

    // Drain completed characters into the rolling window. HTTP thread only.
    private void DrainInto(RttyDemod d)
    {
        lock (_textLock)
        {
            while (d.TryDequeue(out char c)) _text.Append(c);
            if (_text.Length > MaxText) _text.Remove(0, _text.Length - MaxText);
        }
    }

    private async Task<IResult> SetParameters(ParamsDto body, CancellationToken ct)
    {
        if (body.MarkHz is { } m && m is > 100 and < 6000) _markHz = m;
        if (body.ShiftHz is { } s && s is > 0 and < 2000) _shiftHz = s;
        if (body.Baud is { } b && b is > 10 and < 600) _baud = b;
        if (body.Reverse is { } r) _reverse = r;
        if (body.Usos is { } u) _usos = u;

        _demod?.SetParams(_markHz, _shiftHz, _baud, _reverse, _usos);

        if (_ctx is { } ctx)
        {
            await ctx.Settings.SetAsync("markHz", _markHz, ct);
            await ctx.Settings.SetAsync("shiftHz", _shiftHz, ct);
            await ctx.Settings.SetAsync("baud", _baud, ct);
            await ctx.Settings.SetAsync("reverse", _reverse, ct);
            await ctx.Settings.SetAsync("usos", _usos, ct);
        }
        return Results.Ok(new { markHz = _markHz, shiftHz = _shiftHz, baud = _baud, reverse = _reverse, usos = _usos });
    }

    private IResult ClearText()
    {
        _demod?.Reset();
        lock (_textLock) _text.Clear();
        return Results.Ok(new { cleared = true });
    }

    // ------------------------------------------------------------------ DTOs

    public sealed record ParamsDto
    {
        [JsonPropertyName("markHz")]  public double? MarkHz { get; init; }
        [JsonPropertyName("shiftHz")] public double? ShiftHz { get; init; }
        [JsonPropertyName("baud")]    public double? Baud { get; init; }
        [JsonPropertyName("reverse")] public bool? Reverse { get; init; }
        [JsonPropertyName("usos")]    public bool? Usos { get; init; }
    }

    public sealed record StatusDto
    {
        [JsonPropertyName("tapReady")]   public bool TapReady { get; init; }
        [JsonPropertyName("markHz")]     public double MarkHz { get; init; }
        [JsonPropertyName("shiftHz")]    public double ShiftHz { get; init; }
        [JsonPropertyName("baud")]       public double Baud { get; init; }
        [JsonPropertyName("reverse")]    public bool Reverse { get; init; }
        [JsonPropertyName("usos")]       public bool Usos { get; init; }
        [JsonPropertyName("markLevel")]  public double MarkLevel { get; init; }
        [JsonPropertyName("spaceLevel")] public double SpaceLevel { get; init; }
        [JsonPropertyName("markHigh")]   public bool MarkHigh { get; init; }
        [JsonPropertyName("charCount")]  public long CharCount { get; init; }
        [JsonPropertyName("text")]       public string Text { get; init; } = "";
    }
}

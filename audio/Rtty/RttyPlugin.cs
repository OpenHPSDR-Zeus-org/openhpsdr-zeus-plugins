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
    private RttyTx? _tx;

    // Decoded text drained off the demod's lock-free queue on the HTTP thread.
    // The audio thread never touches this lock — it only enqueues chars.
    private readonly object _textLock = new();
    private readonly StringBuilder _text = new(MaxText + 64);

    // Persisted tuning, with amateur defaults.
    private double _markHz = 2125, _shiftHz = 170, _baud = 45.45;
    private bool _reverse, _usos = true;

    // Decoder enable. When false, OnRxAudio returns without running the
    // per-sample DSP — no CPU spent while the decoder isn't in use. Volatile:
    // control thread writes, audio thread reads.
    private volatile bool _enabled = true;

    // ------------------------------------------------------------------ IZeusPlugin

    public async Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        _ctx = context;
        _markHz = await context.Settings.GetAsync<double?>("markHz", ct) ?? 2125.0;
        _shiftHz = await context.Settings.GetAsync<double?>("shiftHz", ct) ?? 170.0;
        _baud = await context.Settings.GetAsync<double?>("baud", ct) ?? 45.45;
        _reverse = await context.Settings.GetAsync<bool?>("reverse", ct) ?? false;
        _usos = await context.Settings.GetAsync<bool?>("usos", ct) ?? true;
        _enabled = await context.Settings.GetAsync<bool?>("enabled", ct) ?? true;
        context.Logger.LogInformation(
            "RTTY initialising; mark={Mark}Hz shift={Shift}Hz baud={Baud} reverse={Rev} usos={Usos}",
            _markHz, _shiftHz, _baud, _reverse, _usos);
    }

    public Task ShutdownAsync(CancellationToken ct)
    {
        _tx?.Dispose();
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------ RX tap

    public AudioPluginRequirements Requirements => new(SampleRate: 48000, Channels: 1, BlockSize: 2048);

    public Task InitializeTapAsync(IAudioHost host, CancellationToken ct)
    {
        var d = new RttyDemod(host.CurrentSampleRate, _markHz, _shiftHz, _baud);
        d.SetParams(_markHz, _shiftHz, _baud, _reverse, _usos);
        _demod = d;

        // TX is available only when the host granted ControlRadio (keying) AND
        // exposes a playback sink (on-air inject). Both null in, e.g., a host
        // without the radio-control seam → receive-only.
        _tx?.Dispose();
        if (_ctx?.Playback is { } sink && _ctx?.RadioController is { } radio)
        {
            _tx = new RttyTx(sink, radio, host.CurrentSampleRate,
                msg => _ctx?.Logger.LogWarning("{Msg}", msg));
            _ctx?.Logger.LogInformation("RTTY TX ready (ControlRadio + playback granted)");
        }
        else
        {
            _ctx?.Logger.LogInformation("RTTY receive-only (TX needs ControlRadio + playback)");
        }

        _ctx?.Logger.LogInformation("RTTY tap init: SR={SampleRate} Hz", host.CurrentSampleRate);
        return Task.CompletedTask;
    }

    public void OnRxAudio(ReadOnlySpan<float> samples, AudioBlockContext ctx)
    {
        if (!_enabled) return; // decoder off — spend no CPU
        _demod?.Process(samples);
    }

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
        endpoints.MapPost("tx/send", SendTx);
        endpoints.MapPost("tx/abort", AbortTx);
    }

    private IResult SendTx(TxSendDto body)
    {
        if (_tx is null)
            return Results.Problem("TX unavailable — needs ControlRadio capability + a host playback sink", statusCode: 409);
        if (string.IsNullOrWhiteSpace(body.Text))
            return Results.BadRequest(new { error = "text required" });
        if (!_tx.Send(body.Text!, _markHz, _shiftHz, _baud, _reverse, _usos))
            return Results.Conflict(new { error = "already sending" });
        _ctx?.Logger.LogInformation("RTTY TX start: {Len} chars (mark={Mark} shift={Shift} baud={Baud} rev={Rev})",
            body.Text!.Length, _markHz, _shiftHz, _baud, _reverse);
        return Results.Ok(new { sending = true });
    }

    private IResult AbortTx()
    {
        _tx?.Abort();
        return Results.Ok(new { aborted = true });
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
            Enabled = _enabled,
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
            TxCapable = _tx is not null,
            TxSending = _tx?.IsSending ?? false,
            TxCurrent = _tx?.Current ?? "",
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
        if (body.Enabled is { } en)
        {
            _enabled = en;
            if (!en) _demod?.Reset(); // clear decoder state so it doesn't resume stale
        }

        _demod?.SetParams(_markHz, _shiftHz, _baud, _reverse, _usos);

        if (_ctx is { } ctx)
        {
            await ctx.Settings.SetAsync("markHz", _markHz, ct);
            await ctx.Settings.SetAsync("shiftHz", _shiftHz, ct);
            await ctx.Settings.SetAsync("baud", _baud, ct);
            await ctx.Settings.SetAsync("reverse", _reverse, ct);
            await ctx.Settings.SetAsync("usos", _usos, ct);
            await ctx.Settings.SetAsync("enabled", _enabled, ct);
        }
        return Results.Ok(new { enabled = _enabled, markHz = _markHz, shiftHz = _shiftHz, baud = _baud, reverse = _reverse, usos = _usos });
    }

    private IResult ClearText()
    {
        _demod?.Reset();
        lock (_textLock) _text.Clear();
        return Results.Ok(new { cleared = true });
    }

    // ------------------------------------------------------------------ DTOs

    public sealed record TxSendDto
    {
        [JsonPropertyName("text")] public string? Text { get; init; }
    }

    public sealed record ParamsDto
    {
        [JsonPropertyName("markHz")]  public double? MarkHz { get; init; }
        [JsonPropertyName("shiftHz")] public double? ShiftHz { get; init; }
        [JsonPropertyName("baud")]    public double? Baud { get; init; }
        [JsonPropertyName("reverse")] public bool? Reverse { get; init; }
        [JsonPropertyName("usos")]    public bool? Usos { get; init; }
        [JsonPropertyName("enabled")] public bool? Enabled { get; init; }
    }

    public sealed record StatusDto
    {
        [JsonPropertyName("tapReady")]   public bool TapReady { get; init; }
        [JsonPropertyName("enabled")]    public bool Enabled { get; init; }
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
        [JsonPropertyName("txCapable")]  public bool TxCapable { get; init; }
        [JsonPropertyName("txSending")]  public bool TxSending { get; init; }
        [JsonPropertyName("txCurrent")]  public string TxCurrent { get; init; } = "";
    }
}

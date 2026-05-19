// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus voice audio chain v1 — Reverb plugin entry.
// Copyright (C) 2025-2026 KB2UKA and contributors.

using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Audio;
using Zeus.Plugins.Contracts.Extensions;

namespace Openhpsdr.Zeus.Samples.Reverb;

public sealed class ReverbPlugin : IZeusPlugin, IAudioPlugin, IBackendPlugin
{
    private readonly ReverbDsp _dsp = new();
    private IPluginContext? _ctx;

    public async Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        _ctx = context;
        context.Logger.LogInformation("Reverb plugin initialising");
        await HydrateFromSettingsAsync(context.Settings, ct);
    }

    public Task ShutdownAsync(CancellationToken ct)
    {
        _ctx?.Logger.LogInformation("Reverb plugin shutting down");
        return Task.CompletedTask;
    }

    public string DisplayName => "Reverb";

    public AudioPluginRequirements Requirements => new(
        SampleRate: 48000,
        Channels:   1,
        BlockSize:  256);

    public Task InitializeAudioAsync(IAudioHost host, CancellationToken ct)
    {
        _dsp.Initialize(host.CurrentSampleRate);
        _ctx?.Logger.LogInformation(
            "Reverb audio init: SR={SampleRate} Hz, channels={Channels}, blockSize={BlockSize}, slot={Slot}",
            host.CurrentSampleRate,
            host.CurrentChannels,
            host.CurrentBlockSize,
            host.Slot);
        return Task.CompletedTask;
    }

    public void Process(ReadOnlySpan<float> input, Span<float> output, AudioBlockContext ctx)
    {
        _dsp.Process(input, output);
    }

    public Task ShutdownAudioAsync(CancellationToken ct)
    {
        _dsp.Reset();
        return Task.CompletedTask;
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("params",  GetParams);
        endpoints.MapPost("params", SetParams);
        endpoints.MapGet("meters",  GetMeters);
    }

    private IResult GetParams() => Results.Ok(SnapshotParams());

    private async Task<IResult> SetParams(ReverbParamsDto incoming, CancellationToken ct)
    {
        bool runtimeChanged = false;

        if (incoming.SizePct.HasValue)    { _dsp.SizePct    = ClampF(incoming.SizePct.Value,    0f, 100f); runtimeChanged = true; }
        if (incoming.DecayPct.HasValue)   { _dsp.DecayPct   = ClampF(incoming.DecayPct.Value,   0f, 100f); runtimeChanged = true; }
        if (incoming.DampPct.HasValue)    { _dsp.DampPct    = ClampF(incoming.DampPct.Value,    0f, 100f); runtimeChanged = true; }
        if (incoming.PreDelayMs.HasValue) { _dsp.PreDelayMs = ClampF(incoming.PreDelayMs.Value, 0f, 100f); runtimeChanged = true; }
        if (incoming.MixPct.HasValue)     { _dsp.MixPct     = ClampF(incoming.MixPct.Value,     0f, 100f);                       }
        if (incoming.InputDb.HasValue)    { _dsp.InputDb    = ClampF(incoming.InputDb.Value,  -24f,  12f);                       }
        if (incoming.OutputDb.HasValue)   { _dsp.OutputDb   = ClampF(incoming.OutputDb.Value, -24f,  12f);                       }
        if (incoming.Bypass.HasValue)     { _dsp.Bypass     = incoming.Bypass.Value;                                                }

        if (runtimeChanged) _dsp.MarkParamsDirty();

        await PersistAsync(ct);
        return Results.Ok(SnapshotParams());
    }

    private IResult GetMeters() => Results.Ok(new ReverbMetersDto
    {
        InputPeakDb  = _dsp.LastInputPeakDb,
        OutputPeakDb = _dsp.LastOutputPeakDb,
        WetLevelDb   = _dsp.LastWetLevelDb,
    });

    private async Task HydrateFromSettingsAsync(IPluginSettings settings, CancellationToken ct)
    {
        var size    = await settings.GetAsync<float?>("size_pct",      ct);
        var decay   = await settings.GetAsync<float?>("decay_pct",     ct);
        var damp    = await settings.GetAsync<float?>("damp_pct",      ct);
        var pre     = await settings.GetAsync<float?>("predelay_ms",   ct);
        var mix     = await settings.GetAsync<float?>("mix_pct",       ct);
        var inputDb = await settings.GetAsync<float?>("input_db",      ct);
        var outputDb= await settings.GetAsync<float?>("output_db",     ct);
        var byp     = await settings.GetAsync<bool?>("bypass",         ct);

        if (size.HasValue)    _dsp.SizePct    = size.Value;
        if (decay.HasValue)   _dsp.DecayPct   = decay.Value;
        if (damp.HasValue)    _dsp.DampPct    = damp.Value;
        if (pre.HasValue)     _dsp.PreDelayMs = pre.Value;
        if (mix.HasValue)     _dsp.MixPct     = mix.Value;
        if (inputDb.HasValue) _dsp.InputDb    = inputDb.Value;
        if (outputDb.HasValue)_dsp.OutputDb   = outputDb.Value;
        if (byp.HasValue)     _dsp.Bypass     = byp.Value;

        _dsp.MarkParamsDirty();
    }

    private async Task PersistAsync(CancellationToken ct)
    {
        if (_ctx is null) return;
        var s = _ctx.Settings;
        await s.SetAsync("size_pct",    _dsp.SizePct,    ct);
        await s.SetAsync("decay_pct",   _dsp.DecayPct,   ct);
        await s.SetAsync("damp_pct",    _dsp.DampPct,    ct);
        await s.SetAsync("predelay_ms", _dsp.PreDelayMs, ct);
        await s.SetAsync("mix_pct",     _dsp.MixPct,     ct);
        await s.SetAsync("input_db",    _dsp.InputDb,    ct);
        await s.SetAsync("output_db",   _dsp.OutputDb,   ct);
        await s.SetAsync("bypass",      _dsp.Bypass,     ct);
    }

    private ReverbParamsDto SnapshotParams() => new()
    {
        SizePct    = _dsp.SizePct,
        DecayPct   = _dsp.DecayPct,
        DampPct    = _dsp.DampPct,
        PreDelayMs = _dsp.PreDelayMs,
        MixPct     = _dsp.MixPct,
        InputDb    = _dsp.InputDb,
        OutputDb   = _dsp.OutputDb,
        Bypass     = _dsp.Bypass,
    };

    private static float ClampF(float v, float lo, float hi) => MathF.Max(lo, MathF.Min(hi, v));

    public sealed record ReverbParamsDto
    {
        [JsonPropertyName("sizePct")]    public float? SizePct    { get; init; }
        [JsonPropertyName("decayPct")]   public float? DecayPct   { get; init; }
        [JsonPropertyName("dampPct")]    public float? DampPct    { get; init; }
        [JsonPropertyName("preDelayMs")] public float? PreDelayMs { get; init; }
        [JsonPropertyName("mixPct")]     public float? MixPct     { get; init; }
        [JsonPropertyName("inputDb")]    public float? InputDb    { get; init; }
        [JsonPropertyName("outputDb")]   public float? OutputDb   { get; init; }
        [JsonPropertyName("bypass")]     public bool?  Bypass     { get; init; }
    }

    public sealed record ReverbMetersDto
    {
        [JsonPropertyName("inputPeakDb")]  public float InputPeakDb  { get; init; }
        [JsonPropertyName("outputPeakDb")] public float OutputPeakDb { get; init; }
        [JsonPropertyName("wetLevelDb")]   public float WetLevelDb   { get; init; }
    }
}

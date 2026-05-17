// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus voice audio chain v1 — Bass Enhancer plugin entry.
// Copyright (C) 2025-2026 KB2UKA and contributors.

using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Audio;
using Zeus.Plugins.Contracts.Extensions;

namespace Openhpsdr.Zeus.Samples.Bass;

public sealed class BassPlugin : IZeusPlugin, IAudioPlugin, IBackendPlugin
{
    private readonly BassDsp _dsp = new();
    private IPluginContext? _ctx;

    public async Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        _ctx = context;
        context.Logger.LogInformation("Bass Enhancer plugin initialising");
        await HydrateFromSettingsAsync(context.Settings, ct);
    }

    public Task ShutdownAsync(CancellationToken ct)
    {
        _ctx?.Logger.LogInformation("Bass Enhancer plugin shutting down");
        return Task.CompletedTask;
    }

    public string DisplayName => "Bass Enhancer";

    public AudioPluginRequirements Requirements => new(
        SampleRate: 48000,
        Channels:   1,
        BlockSize:  256);

    public Task InitializeAudioAsync(IAudioHost host, CancellationToken ct)
    {
        _dsp.Initialize(host.CurrentSampleRate);
        _ctx?.Logger.LogInformation(
            "Bass Enhancer audio init: SR={SampleRate} Hz, channels={Channels}, blockSize={BlockSize}, slot={Slot}",
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

    private async Task<IResult> SetParams(BassParamsDto incoming, CancellationToken ct)
    {
        if (incoming.FrequencyHz.HasValue) _dsp.FrequencyHz = ClampF(incoming.FrequencyHz.Value, 40f, 500f);
        if (incoming.AmountPct.HasValue)   _dsp.AmountPct   = ClampF(incoming.AmountPct.Value,    0f, 100f);
        if (incoming.MixPct.HasValue)      _dsp.MixPct      = ClampF(incoming.MixPct.Value,       0f, 100f);
        if (incoming.Bypass.HasValue)      _dsp.Bypass      = incoming.Bypass.Value;
        if (incoming.FrequencyHz.HasValue) _dsp.MarkParamsDirty();

        await PersistAsync(ct);
        return Results.Ok(SnapshotParams());
    }

    private IResult GetMeters() => Results.Ok(new BassMetersDto
    {
        InputPeakDb     = _dsp.LastInputPeakDb,
        OutputPeakDb    = _dsp.LastOutputPeakDb,
        HarmonicLevelDb = _dsp.LastHarmonicLevelDb,
    });

    private async Task HydrateFromSettingsAsync(IPluginSettings settings, CancellationToken ct)
    {
        var freq   = await settings.GetAsync<float?>("frequency_hz", ct);
        var amount = await settings.GetAsync<float?>("amount_pct",   ct);
        var mix    = await settings.GetAsync<float?>("mix_pct",      ct);
        var bypass = await settings.GetAsync<bool?>("bypass",         ct);

        if (freq.HasValue)   _dsp.FrequencyHz = freq.Value;
        if (amount.HasValue) _dsp.AmountPct   = amount.Value;
        if (mix.HasValue)    _dsp.MixPct      = mix.Value;
        if (bypass.HasValue) _dsp.Bypass      = bypass.Value;

        _dsp.MarkParamsDirty();
    }

    private async Task PersistAsync(CancellationToken ct)
    {
        if (_ctx is null) return;
        var s = _ctx.Settings;
        await s.SetAsync("frequency_hz", _dsp.FrequencyHz, ct);
        await s.SetAsync("amount_pct",   _dsp.AmountPct,   ct);
        await s.SetAsync("mix_pct",      _dsp.MixPct,      ct);
        await s.SetAsync("bypass",       _dsp.Bypass,      ct);
    }

    private BassParamsDto SnapshotParams() => new()
    {
        FrequencyHz = _dsp.FrequencyHz,
        AmountPct   = _dsp.AmountPct,
        MixPct      = _dsp.MixPct,
        Bypass      = _dsp.Bypass,
    };

    private static float ClampF(float v, float lo, float hi) => MathF.Max(lo, MathF.Min(hi, v));

    public sealed record BassParamsDto
    {
        [JsonPropertyName("freqHz")]    public float? FrequencyHz { get; init; }
        [JsonPropertyName("amountPct")] public float? AmountPct   { get; init; }
        [JsonPropertyName("mixPct")]    public float? MixPct      { get; init; }
        [JsonPropertyName("bypass")]    public bool?  Bypass      { get; init; }
    }

    public sealed record BassMetersDto
    {
        [JsonPropertyName("inputPeakDb")]     public float InputPeakDb     { get; init; }
        [JsonPropertyName("outputPeakDb")]    public float OutputPeakDb    { get; init; }
        [JsonPropertyName("harmonicLevelDb")] public float HarmonicLevelDb { get; init; }
    }
}

// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus voice audio chain — Noise Gate plugin entry.
// Copyright (C) 2025-2026 KB2UKA and contributors.

using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Audio;
using Zeus.Plugins.Contracts.Extensions;

namespace Openhpsdr.Zeus.Samples.NoiseGate;

/// <summary>
/// Noise Gate plugin entry. Same plumbing as Compressor / EQ / Exciter
/// (IZeusPlugin + IAudioPlugin + IBackendPlugin); the chain-plugin bypass
/// convention is implemented on <see cref="NoiseGateDsp"/>.
///
/// REST surface (mapped under /api/plugins/com.openhpsdr.zeus.samples.noisegate/):
///   GET  params   → full state
///   POST params   → partial update (any subset of fields)
///   GET  meters   → IN / OUT peak dB + gate gain (dB) + state (0/1/2)
/// </summary>
public sealed class NoiseGatePlugin : IZeusPlugin, IAudioPlugin, IBackendPlugin
{
    private readonly NoiseGateDsp _dsp = new();
    private IPluginContext? _ctx;

    // ------------------------------------------------------------------
    // IZeusPlugin
    // ------------------------------------------------------------------

    public async Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        _ctx = context;
        context.Logger.LogInformation("Noise Gate plugin initialising");
        await HydrateFromSettingsAsync(context.Settings, ct);
    }

    public Task ShutdownAsync(CancellationToken ct)
    {
        _ctx?.Logger.LogInformation("Noise Gate plugin shutting down");
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------
    // IAudioPlugin
    // ------------------------------------------------------------------

    public string DisplayName => "Noise Gate";

    public AudioPluginRequirements Requirements => new(
        SampleRate: 48000,
        Channels:   1,
        BlockSize:  256);

    public Task InitializeAudioAsync(IAudioHost host, CancellationToken ct)
    {
        _dsp.Initialize(host.CurrentSampleRate);
        _ctx?.Logger.LogInformation(
            "Noise Gate audio init: SR={SampleRate} Hz, channels={Channels}, blockSize={BlockSize}, slot={Slot}",
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

    // ------------------------------------------------------------------
    // IBackendPlugin
    // ------------------------------------------------------------------

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("params",  GetParams);
        endpoints.MapPost("params", SetParams);
        endpoints.MapGet("meters",  GetMeters);
    }

    private IResult GetParams() => Results.Ok(SnapshotParams());

    private async Task<IResult> SetParams(NoiseGateParamsDto incoming, CancellationToken ct)
    {
        bool coefDirty = false;

        if (incoming.ThresholdDb.HasValue) _dsp.ThresholdDb = ClampF(incoming.ThresholdDb.Value, -80f,  0f);
        if (incoming.AttackMs.HasValue)    { _dsp.AttackMs    = ClampF(incoming.AttackMs.Value,    0.1f, 50f);   coefDirty = true; }
        if (incoming.HoldMs.HasValue)      _dsp.HoldMs       = ClampF(incoming.HoldMs.Value,      0f,    500f);
        if (incoming.ReleaseMs.HasValue)   { _dsp.ReleaseMs   = ClampF(incoming.ReleaseMs.Value,   5f,    1000f); coefDirty = true; }
        if (incoming.RangeDb.HasValue)     _dsp.RangeDb      = ClampF(incoming.RangeDb.Value,    -80f,   0f);
        if (incoming.OutputDb.HasValue)    _dsp.OutputDb     = ClampF(incoming.OutputDb.Value,   -24f,  12f);
        if (incoming.Bypass.HasValue)      _dsp.Bypass       = incoming.Bypass.Value;

        if (coefDirty) _dsp.MarkParamsDirty();

        await PersistAsync(ct);
        return Results.Ok(SnapshotParams());
    }

    private IResult GetMeters() => Results.Ok(new NoiseGateMetersDto
    {
        InputPeakDb  = _dsp.LastInputPeakDb,
        OutputPeakDb = _dsp.LastOutputPeakDb,
        GainDb       = _dsp.LastGainDb,
        State        = _dsp.LastState,
    });

    // ------------------------------------------------------------------
    // Settings persistence
    // ------------------------------------------------------------------

    private async Task HydrateFromSettingsAsync(IPluginSettings settings, CancellationToken ct)
    {
        var threshold = await settings.GetAsync<float?>("threshold_db", ct);
        var attack    = await settings.GetAsync<float?>("attack_ms",    ct);
        var hold      = await settings.GetAsync<float?>("hold_ms",      ct);
        var release   = await settings.GetAsync<float?>("release_ms",   ct);
        var range     = await settings.GetAsync<float?>("range_db",     ct);
        var output    = await settings.GetAsync<float?>("output_db",    ct);
        var bypass    = await settings.GetAsync<bool?>("bypass",        ct);

        if (threshold.HasValue) _dsp.ThresholdDb = threshold.Value;
        if (attack.HasValue)    _dsp.AttackMs    = attack.Value;
        if (hold.HasValue)      _dsp.HoldMs      = hold.Value;
        if (release.HasValue)   _dsp.ReleaseMs   = release.Value;
        if (range.HasValue)     _dsp.RangeDb     = range.Value;
        if (output.HasValue)    _dsp.OutputDb    = output.Value;
        if (bypass.HasValue)    _dsp.Bypass      = bypass.Value;

        _dsp.MarkParamsDirty();
    }

    private async Task PersistAsync(CancellationToken ct)
    {
        if (_ctx is null) return;
        var s = _ctx.Settings;
        await s.SetAsync("threshold_db", _dsp.ThresholdDb, ct);
        await s.SetAsync("attack_ms",    _dsp.AttackMs,    ct);
        await s.SetAsync("hold_ms",      _dsp.HoldMs,      ct);
        await s.SetAsync("release_ms",   _dsp.ReleaseMs,   ct);
        await s.SetAsync("range_db",     _dsp.RangeDb,     ct);
        await s.SetAsync("output_db",    _dsp.OutputDb,    ct);
        await s.SetAsync("bypass",       _dsp.Bypass,      ct);
    }

    // ------------------------------------------------------------------
    // DTOs
    // ------------------------------------------------------------------

    private NoiseGateParamsDto SnapshotParams() => new()
    {
        ThresholdDb = _dsp.ThresholdDb,
        AttackMs    = _dsp.AttackMs,
        HoldMs      = _dsp.HoldMs,
        ReleaseMs   = _dsp.ReleaseMs,
        RangeDb     = _dsp.RangeDb,
        OutputDb    = _dsp.OutputDb,
        Bypass      = _dsp.Bypass,
    };

    private static float ClampF(float v, float lo, float hi) => MathF.Max(lo, MathF.Min(hi, v));

    public sealed record NoiseGateParamsDto
    {
        [JsonPropertyName("thresholdDb")] public float? ThresholdDb { get; init; }
        [JsonPropertyName("attackMs")]    public float? AttackMs    { get; init; }
        [JsonPropertyName("holdMs")]      public float? HoldMs      { get; init; }
        [JsonPropertyName("releaseMs")]   public float? ReleaseMs   { get; init; }
        [JsonPropertyName("rangeDb")]     public float? RangeDb     { get; init; }
        [JsonPropertyName("outputDb")]    public float? OutputDb    { get; init; }
        [JsonPropertyName("bypass")]      public bool?  Bypass      { get; init; }
    }

    public sealed record NoiseGateMetersDto
    {
        [JsonPropertyName("inputPeakDb")]  public float InputPeakDb  { get; init; }
        [JsonPropertyName("outputPeakDb")] public float OutputPeakDb { get; init; }
        [JsonPropertyName("gainDb")]       public float GainDb       { get; init; }
        /// <summary>0 = closed, 1 = hold, 2 = open.</summary>
        [JsonPropertyName("state")]        public int   State        { get; init; }
    }
}

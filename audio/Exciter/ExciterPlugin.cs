// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus voice audio chain v1 — Aural Exciter plugin entry.
// Copyright (C) 2025-2026 KB2UKA and contributors.

using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Audio;
using Zeus.Plugins.Contracts.Extensions;

namespace Openhpsdr.Zeus.Samples.Exciter;

/// <summary>
/// Aural Exciter plugin entry. Same shape as Compressor / EQ
/// (IZeusPlugin + IAudioPlugin + IBackendPlugin) and the bypass
/// convention from feedback_audio_plugin_bypass_convention.
///
/// REST surface:
///   GET  /api/plugins/com.openhpsdr.zeus.samples.exciter/params   → full state
///   POST /api/plugins/com.openhpsdr.zeus.samples.exciter/params   → partial update
///   GET  /api/plugins/com.openhpsdr.zeus.samples.exciter/meters   → IN/OUT peak + harmonic level
/// </summary>
public sealed class ExciterPlugin : IZeusPlugin, IAudioPlugin, IBackendPlugin
{
    private readonly ExciterDsp _dsp = new();
    private IPluginContext? _ctx;

    // ------------------------------------------------------------------
    // IZeusPlugin
    // ------------------------------------------------------------------

    public async Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        _ctx = context;
        context.Logger.LogInformation("Aural Exciter plugin initialising");
        await HydrateFromSettingsAsync(context.Settings, ct);
    }

    public Task ShutdownAsync(CancellationToken ct)
    {
        _ctx?.Logger.LogInformation("Aural Exciter plugin shutting down");
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------
    // IAudioPlugin
    // ------------------------------------------------------------------

    public string DisplayName => "Aural Exciter";

    public AudioPluginRequirements Requirements => new(
        SampleRate: 48000,
        Channels:   1,
        BlockSize:  256);

    public Task InitializeAudioAsync(IAudioHost host, CancellationToken ct)
    {
        _dsp.Initialize(host.CurrentSampleRate);
        _ctx?.Logger.LogInformation(
            "Aural Exciter audio init: SR={SampleRate} Hz, channels={Channels}, blockSize={BlockSize}, slot={Slot}",
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

    private async Task<IResult> SetParams(ExciterParamsDto incoming, CancellationToken ct)
    {
        if (incoming.FrequencyHz.HasValue) _dsp.FrequencyHz = ClampF(incoming.FrequencyHz.Value, 100f, 18000f);
        if (incoming.DriveDb.HasValue)     _dsp.DriveDb     = ClampF(incoming.DriveDb.Value,        0f, 36f);
        if (incoming.MixPercent.HasValue)  _dsp.MixPercent  = ClampF(incoming.MixPercent.Value,     0f, 100f);
        if (incoming.InputDb.HasValue)     _dsp.InputDb     = ClampF(incoming.InputDb.Value,      -24f, 12f);
        if (incoming.OutputDb.HasValue)    _dsp.OutputDb    = ClampF(incoming.OutputDb.Value,     -24f, 12f);
        if (incoming.Bypass.HasValue)      _dsp.Bypass      = incoming.Bypass.Value;

        // Frequency change requires biquad coefficient recompute.
        if (incoming.FrequencyHz.HasValue) _dsp.MarkParamsDirty();

        await PersistAsync(ct);
        return Results.Ok(SnapshotParams());
    }

    private IResult GetMeters() => Results.Ok(new ExciterMetersDto
    {
        InputPeakDb       = _dsp.LastInputPeakDb,
        OutputPeakDb      = _dsp.LastOutputPeakDb,
        HarmonicLevelDb   = _dsp.LastHarmonicLevelDb,
    });

    // ------------------------------------------------------------------
    // Settings persistence
    // ------------------------------------------------------------------

    private async Task HydrateFromSettingsAsync(IPluginSettings settings, CancellationToken ct)
    {
        var freq    = await settings.GetAsync<float?>("frequency_hz", ct);
        var drive   = await settings.GetAsync<float?>("drive_db",     ct);
        var mix     = await settings.GetAsync<float?>("mix_pct",      ct);
        var inputDb = await settings.GetAsync<float?>("input_db",     ct);
        var outputDb= await settings.GetAsync<float?>("output_db",    ct);
        var bypass  = await settings.GetAsync<bool?>("bypass",         ct);

        if (freq.HasValue)    _dsp.FrequencyHz = freq.Value;
        if (drive.HasValue)   _dsp.DriveDb     = drive.Value;
        if (mix.HasValue)     _dsp.MixPercent  = mix.Value;
        if (inputDb.HasValue) _dsp.InputDb     = inputDb.Value;
        if (outputDb.HasValue)_dsp.OutputDb    = outputDb.Value;
        if (bypass.HasValue)  _dsp.Bypass      = bypass.Value;

        _dsp.MarkParamsDirty();
    }

    private async Task PersistAsync(CancellationToken ct)
    {
        if (_ctx is null) return;
        var s = _ctx.Settings;
        await s.SetAsync("frequency_hz", _dsp.FrequencyHz, ct);
        await s.SetAsync("drive_db",     _dsp.DriveDb,     ct);
        await s.SetAsync("mix_pct",      _dsp.MixPercent,  ct);
        await s.SetAsync("input_db",     _dsp.InputDb,     ct);
        await s.SetAsync("output_db",    _dsp.OutputDb,    ct);
        await s.SetAsync("bypass",       _dsp.Bypass,      ct);
    }

    // ------------------------------------------------------------------
    // DTOs
    // ------------------------------------------------------------------

    private ExciterParamsDto SnapshotParams() => new()
    {
        FrequencyHz = _dsp.FrequencyHz,
        DriveDb     = _dsp.DriveDb,
        MixPercent  = _dsp.MixPercent,
        InputDb     = _dsp.InputDb,
        OutputDb    = _dsp.OutputDb,
        Bypass      = _dsp.Bypass,
    };

    private static float ClampF(float v, float lo, float hi) => MathF.Max(lo, MathF.Min(hi, v));

    public sealed record ExciterParamsDto
    {
        [JsonPropertyName("freqHz")]   public float? FrequencyHz { get; init; }
        [JsonPropertyName("driveDb")]  public float? DriveDb     { get; init; }
        [JsonPropertyName("mixPct")]   public float? MixPercent  { get; init; }
        [JsonPropertyName("inputDb")]  public float? InputDb     { get; init; }
        [JsonPropertyName("outputDb")] public float? OutputDb    { get; init; }
        [JsonPropertyName("bypass")]   public bool?  Bypass      { get; init; }
    }

    public sealed record ExciterMetersDto
    {
        [JsonPropertyName("inputPeakDb")]     public float InputPeakDb     { get; init; }
        [JsonPropertyName("outputPeakDb")]    public float OutputPeakDb    { get; init; }
        [JsonPropertyName("harmonicLevelDb")] public float HarmonicLevelDb { get; init; }
    }
}

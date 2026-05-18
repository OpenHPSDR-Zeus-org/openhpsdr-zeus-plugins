// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus voice audio chain — Tube Preamp plugin entry.
// Copyright (C) 2025-2026 KB2UKA and contributors.

using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Audio;
using Zeus.Plugins.Contracts.Extensions;

namespace Openhpsdr.Zeus.Samples.TubePreamp;

/// <summary>
/// Tube Preamp plugin entry. Same plumbing as Compressor / EQ / Exciter
/// (IZeusPlugin + IAudioPlugin + IBackendPlugin); the chain-plugin bypass
/// convention (feedback_audio_plugin_bypass_convention) is implemented on
/// <see cref="TubePreampDsp"/>.
///
/// REST surface (mapped under /api/plugins/com.openhpsdr.zeus.samples.tubepreamp/):
///   GET  params   → full state
///   POST params   → partial update (any subset of fields)
///   GET  meters   → IN / OUT peak dB + drive-stage peak dB
/// </summary>
public sealed class TubePreampPlugin : IZeusPlugin, IAudioPlugin, IBackendPlugin
{
    private readonly TubePreampDsp _dsp = new();
    private IPluginContext? _ctx;

    // ------------------------------------------------------------------
    // IZeusPlugin
    // ------------------------------------------------------------------

    public async Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        _ctx = context;
        context.Logger.LogInformation("Tube Preamp plugin initialising");
        await HydrateFromSettingsAsync(context.Settings, ct);
    }

    public Task ShutdownAsync(CancellationToken ct)
    {
        _ctx?.Logger.LogInformation("Tube Preamp plugin shutting down");
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------
    // IAudioPlugin
    // ------------------------------------------------------------------

    public string DisplayName => "Tube Preamp";

    public AudioPluginRequirements Requirements => new(
        SampleRate: 48000,
        Channels:   1,
        BlockSize:  256);

    public Task InitializeAudioAsync(IAudioHost host, CancellationToken ct)
    {
        _dsp.Initialize(host.CurrentSampleRate);
        _ctx?.Logger.LogInformation(
            "Tube Preamp audio init: SR={SampleRate} Hz, channels={Channels}, blockSize={BlockSize}, slot={Slot}",
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

    private async Task<IResult> SetParams(TubePreampParamsDto incoming, CancellationToken ct)
    {
        bool coefDirty = false;

        if (incoming.DriveDb.HasValue)  _dsp.DriveDb  = ClampF(incoming.DriveDb.Value,    0f,  36f);
        if (incoming.Bias.HasValue)     _dsp.Bias     = ClampF(incoming.Bias.Value,      -1f,   1f);
        if (incoming.WarmthHz.HasValue) { _dsp.WarmthHz = ClampF(incoming.WarmthHz.Value, 4000f, 20000f); coefDirty = true; }
        if (incoming.OutputDb.HasValue) _dsp.OutputDb = ClampF(incoming.OutputDb.Value, -24f,  12f);
        if (incoming.Bypass.HasValue)   _dsp.Bypass   = incoming.Bypass.Value;

        if (coefDirty) _dsp.MarkParamsDirty();

        await PersistAsync(ct);
        return Results.Ok(SnapshotParams());
    }

    private IResult GetMeters() => Results.Ok(new TubePreampMetersDto
    {
        InputPeakDb  = _dsp.LastInputPeakDb,
        OutputPeakDb = _dsp.LastOutputPeakDb,
        DrivePeakDb  = _dsp.LastDrivePeakDb,
    });

    // ------------------------------------------------------------------
    // Settings persistence
    // ------------------------------------------------------------------

    private async Task HydrateFromSettingsAsync(IPluginSettings settings, CancellationToken ct)
    {
        var drive  = await settings.GetAsync<float?>("drive_db",   ct);
        var bias   = await settings.GetAsync<float?>("bias",       ct);
        var warmth = await settings.GetAsync<float?>("warmth_hz",  ct);
        var output = await settings.GetAsync<float?>("output_db",  ct);
        var bypass = await settings.GetAsync<bool?>("bypass",      ct);

        if (drive.HasValue)  _dsp.DriveDb  = drive.Value;
        if (bias.HasValue)   _dsp.Bias     = bias.Value;
        if (warmth.HasValue) _dsp.WarmthHz = warmth.Value;
        if (output.HasValue) _dsp.OutputDb = output.Value;
        if (bypass.HasValue) _dsp.Bypass   = bypass.Value;

        _dsp.MarkParamsDirty();
    }

    private async Task PersistAsync(CancellationToken ct)
    {
        if (_ctx is null) return;
        var s = _ctx.Settings;
        await s.SetAsync("drive_db",  _dsp.DriveDb,  ct);
        await s.SetAsync("bias",      _dsp.Bias,     ct);
        await s.SetAsync("warmth_hz", _dsp.WarmthHz, ct);
        await s.SetAsync("output_db", _dsp.OutputDb, ct);
        await s.SetAsync("bypass",    _dsp.Bypass,   ct);
    }

    // ------------------------------------------------------------------
    // DTOs
    // ------------------------------------------------------------------

    private TubePreampParamsDto SnapshotParams() => new()
    {
        DriveDb  = _dsp.DriveDb,
        Bias     = _dsp.Bias,
        WarmthHz = _dsp.WarmthHz,
        OutputDb = _dsp.OutputDb,
        Bypass   = _dsp.Bypass,
    };

    private static float ClampF(float v, float lo, float hi) => MathF.Max(lo, MathF.Min(hi, v));

    public sealed record TubePreampParamsDto
    {
        [JsonPropertyName("driveDb")]  public float? DriveDb  { get; init; }
        [JsonPropertyName("bias")]     public float? Bias     { get; init; }
        [JsonPropertyName("warmthHz")] public float? WarmthHz { get; init; }
        [JsonPropertyName("outputDb")] public float? OutputDb { get; init; }
        [JsonPropertyName("bypass")]   public bool?  Bypass   { get; init; }
    }

    public sealed record TubePreampMetersDto
    {
        [JsonPropertyName("inputPeakDb")]  public float InputPeakDb  { get; init; }
        [JsonPropertyName("outputPeakDb")] public float OutputPeakDb { get; init; }
        [JsonPropertyName("drivePeakDb")]  public float DrivePeakDb  { get; init; }
    }
}

// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus voice audio chain v1 — 10-Band Parametric EQ plugin entry.
// Copyright (C) 2025-2026 KB2UKA and contributors.

using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Audio;
using Zeus.Plugins.Contracts.Extensions;

namespace Openhpsdr.Zeus.Samples.Eq;

/// <summary>
/// 10-band parametric EQ plugin entry. Mirrors the Compressor's shape
/// (IZeusPlugin + IAudioPlugin + IBackendPlugin) and the bypass
/// convention from feedback_audio_plugin_bypass_convention.
///
/// REST surface:
///   GET  /api/plugins/com.openhpsdr.zeus.samples.eq/params  → full state
///   POST /api/plugins/com.openhpsdr.zeus.samples.eq/params  → partial update
///   GET  /api/plugins/com.openhpsdr.zeus.samples.eq/meters  → IN/OUT peak
///
/// Partial-update semantics on POST: if <c>bands</c> is omitted, no band
/// is touched; if <c>bands</c> is present, the WHOLE array of 10 is
/// replaced. Per-band partial updates are not in v1 — the 30-param
/// payload is small enough that batching the whole array is fine and
/// keeps the wire shape simple.
/// </summary>
public sealed class EqPlugin : IZeusPlugin, IAudioPlugin, IBackendPlugin
{
    private readonly EqDsp _dsp = new();
    private IPluginContext? _ctx;

    // ------------------------------------------------------------------
    // IZeusPlugin
    // ------------------------------------------------------------------

    public async Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        _ctx = context;
        context.Logger.LogInformation("EQ plugin initialising");
        await HydrateFromSettingsAsync(context.Settings, ct);
    }

    public Task ShutdownAsync(CancellationToken ct)
    {
        _ctx?.Logger.LogInformation("EQ plugin shutting down");
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------
    // IAudioPlugin
    // ------------------------------------------------------------------

    public string DisplayName => "10-Band EQ";

    public AudioPluginRequirements Requirements => new(
        SampleRate: 48000,
        Channels:   1,
        BlockSize:  256);

    public Task InitializeAudioAsync(IAudioHost host, CancellationToken ct)
    {
        _dsp.Initialize(host.CurrentSampleRate);
        _ctx?.Logger.LogInformation(
            "EQ audio init: SR={SampleRate} Hz, channels={Channels}, blockSize={BlockSize}, slot={Slot}",
            host.CurrentSampleRate,
            host.CurrentChannels,
            host.CurrentBlockSize,
            host.Slot);
        return Task.CompletedTask;
    }

    public void Process(ReadOnlySpan<float> input, Span<float> output, AudioBlockContext ctx)
    {
        // Mono only in v1.
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

    private async Task<IResult> SetParams(EqParamsDto incoming, CancellationToken ct)
    {
        // Bypass.
        if (incoming.Bypass.HasValue) _dsp.Bypass = incoming.Bypass.Value;

        // Bands — when present, the WHOLE array is the new state. Caller
        // sends 10 bands; we clamp each into sensible operator ranges.
        if (incoming.Bands is { } bands && bands.Length == EqDsp.BandCount)
        {
            for (int i = 0; i < EqDsp.BandCount; i++)
            {
                var b = bands[i];
                if (b is null) continue;
                _dsp.Bands[i].FrequencyHz = ClampF(b.FrequencyHz, 20f, 20000f);
                _dsp.Bands[i].GainDb      = ClampF(b.GainDb,     -24f, 24f);
                _dsp.Bands[i].Q           = ClampF(b.Q,          0.1f, 10f);
            }
            _dsp.MarkParamsDirty();
        }

        await PersistAsync(ct);
        return Results.Ok(SnapshotParams());
    }

    private IResult GetMeters() => Results.Ok(new EqMetersDto
    {
        InputPeakDb  = _dsp.LastInputPeakDb,
        OutputPeakDb = _dsp.LastOutputPeakDb,
    });

    // ------------------------------------------------------------------
    // Settings persistence
    // ------------------------------------------------------------------

    private async Task HydrateFromSettingsAsync(IPluginSettings settings, CancellationToken ct)
    {
        var bypass = await settings.GetAsync<bool?>("bypass", ct);
        if (bypass.HasValue) _dsp.Bypass = bypass.Value;

        // Per-band keys — one float per dimension. Keeps the LiteDB layout
        // legible (one row per key) and survives schema changes simpler
        // than serialising the band array as JSON.
        for (int i = 0; i < EqDsp.BandCount; i++)
        {
            var freq = await settings.GetAsync<float?>($"band_{i}_freq",   ct);
            var gain = await settings.GetAsync<float?>($"band_{i}_gain",   ct);
            var q    = await settings.GetAsync<float?>($"band_{i}_q",      ct);
            if (freq.HasValue) _dsp.Bands[i].FrequencyHz = freq.Value;
            if (gain.HasValue) _dsp.Bands[i].GainDb      = gain.Value;
            if (q.HasValue)    _dsp.Bands[i].Q           = q.Value;
        }

        _dsp.MarkParamsDirty();
    }

    private async Task PersistAsync(CancellationToken ct)
    {
        if (_ctx is null) return;
        var s = _ctx.Settings;
        await s.SetAsync("bypass", _dsp.Bypass, ct);
        for (int i = 0; i < EqDsp.BandCount; i++)
        {
            await s.SetAsync($"band_{i}_freq", _dsp.Bands[i].FrequencyHz, ct);
            await s.SetAsync($"band_{i}_gain", _dsp.Bands[i].GainDb,      ct);
            await s.SetAsync($"band_{i}_q",    _dsp.Bands[i].Q,           ct);
        }
    }

    // ------------------------------------------------------------------
    // Snapshot helpers + DTOs
    // ------------------------------------------------------------------

    private EqParamsDto SnapshotParams()
    {
        var bands = new EqBandDto[EqDsp.BandCount];
        for (int i = 0; i < EqDsp.BandCount; i++)
        {
            bands[i] = new EqBandDto
            {
                FrequencyHz = _dsp.Bands[i].FrequencyHz,
                GainDb      = _dsp.Bands[i].GainDb,
                Q           = _dsp.Bands[i].Q,
            };
        }
        return new EqParamsDto { Bands = bands, Bypass = _dsp.Bypass };
    }

    private static float ClampF(float v, float lo, float hi) => MathF.Max(lo, MathF.Min(hi, v));

    public sealed record EqBandDto
    {
        [JsonPropertyName("freqHz")] public float FrequencyHz { get; init; }
        [JsonPropertyName("gainDb")] public float GainDb      { get; init; }
        [JsonPropertyName("q")]      public float Q           { get; init; }
    }

    public sealed record EqParamsDto
    {
        [JsonPropertyName("bands")]  public EqBandDto[]? Bands  { get; init; }
        [JsonPropertyName("bypass")] public bool?         Bypass { get; init; }
    }

    public sealed record EqMetersDto
    {
        [JsonPropertyName("inputPeakDb")]  public float InputPeakDb  { get; init; }
        [JsonPropertyName("outputPeakDb")] public float OutputPeakDb { get; init; }
    }
}

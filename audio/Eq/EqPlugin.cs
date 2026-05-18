// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus voice audio chain v2 — 10-Band Parametric EQ plugin entry.
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
/// v0.2.0 additions:
///   * Input + Output gain stages (-24 dB … +24 dB each) for proper
///     chain gain staging. Bypass-still-applies-gain so the plugin
///     can be used as a pure gain trim with the cascade off.
///   * Live FFT spectrum analyser (input + output) for the
///     "see what the EQ is doing to my voice" visualisation. Polled
///     by the panel via GET /spectrum at ~30 Hz.
///
/// REST surface:
///   GET  /api/plugins/com.openhpsdr.zeus.samples.eq/params    → full state
///   POST /api/plugins/com.openhpsdr.zeus.samples.eq/params    → partial update
///   GET  /api/plugins/com.openhpsdr.zeus.samples.eq/meters    → IN/OUT peak
///   GET  /api/plugins/com.openhpsdr.zeus.samples.eq/spectrum  → input + output dB bins
///
/// Partial-update semantics on POST: if <c>bands</c> is omitted, no band
/// is touched; if <c>bands</c> is present, the WHOLE array of 10 is
/// replaced. <c>inputGainDb</c> / <c>outputGainDb</c> / <c>bypass</c>
/// are also optional — only the fields supplied are written.
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
        context.Logger.LogInformation("EQ plugin initialising (v0.2.0)");
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
        endpoints.MapGet("params",   GetParams);
        endpoints.MapPost("params",  SetParams);
        endpoints.MapGet("meters",   GetMeters);
        endpoints.MapGet("spectrum", GetSpectrum);
    }

    private IResult GetParams() => Results.Ok(SnapshotParams());

    private async Task<IResult> SetParams(EqParamsDto incoming, CancellationToken ct)
    {
        // Bypass.
        if (incoming.Bypass.HasValue) _dsp.Bypass = incoming.Bypass.Value;

        // I/O gain stages. EqDsp setters clamp to [-24, +24] dB.
        if (incoming.InputGainDb.HasValue)  _dsp.InputGainDb  = incoming.InputGainDb.Value;
        if (incoming.OutputGainDb.HasValue) _dsp.OutputGainDb = incoming.OutputGainDb.Value;

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

    private IResult GetSpectrum()
    {
        // Snapshot the bin arrays into freshly-allocated DTO arrays so
        // the client gets a stable view even if the audio thread writes
        // mid-serialisation. EqDsp's public bin arrays are mutated in
        // place; copying here is the cost of "no locks on the audio
        // thread". 256 floats × 2 = ~2 KiB per request, negligible
        // at 30 Hz polling.
        var inSpec  = new float[EqDsp.BinCount];
        var outSpec = new float[EqDsp.BinCount];
        Array.Copy(_dsp.LastInputSpectrumDb,  inSpec,  EqDsp.BinCount);
        Array.Copy(_dsp.LastOutputSpectrumDb, outSpec, EqDsp.BinCount);
        return Results.Ok(new EqSpectrumDto
        {
            InputBinsDb  = inSpec,
            OutputBinsDb = outSpec,
            BinCount     = EqDsp.BinCount,
            FMinHz       = EqDsp.SpectrumFMinHz,
            FMaxHz       = EqDsp.SpectrumFMaxHz,
            DbFloor      = EqDsp.SpectrumDbFloor,
        });
    }

    // ------------------------------------------------------------------
    // Settings persistence
    // ------------------------------------------------------------------

    private async Task HydrateFromSettingsAsync(IPluginSettings settings, CancellationToken ct)
    {
        var bypass = await settings.GetAsync<bool?>("bypass", ct);
        if (bypass.HasValue) _dsp.Bypass = bypass.Value;

        // v0.2.0 I/O gain stages — missing in v0.1.0 saved settings,
        // defaults to 0 dB which preserves v0.1.0 behaviour exactly.
        var inGain  = await settings.GetAsync<float?>("input_gain_db",  ct);
        var outGain = await settings.GetAsync<float?>("output_gain_db", ct);
        if (inGain.HasValue)  _dsp.InputGainDb  = inGain.Value;
        if (outGain.HasValue) _dsp.OutputGainDb = outGain.Value;

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
        await s.SetAsync("input_gain_db",  _dsp.InputGainDb,  ct);
        await s.SetAsync("output_gain_db", _dsp.OutputGainDb, ct);
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
        return new EqParamsDto
        {
            Bands        = bands,
            Bypass       = _dsp.Bypass,
            InputGainDb  = _dsp.InputGainDb,
            OutputGainDb = _dsp.OutputGainDb,
        };
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
        [JsonPropertyName("bands")]        public EqBandDto[]? Bands        { get; init; }
        [JsonPropertyName("bypass")]       public bool?        Bypass       { get; init; }
        [JsonPropertyName("inputGainDb")]  public float?       InputGainDb  { get; init; }
        [JsonPropertyName("outputGainDb")] public float?       OutputGainDb { get; init; }
    }

    public sealed record EqMetersDto
    {
        [JsonPropertyName("inputPeakDb")]  public float InputPeakDb  { get; init; }
        [JsonPropertyName("outputPeakDb")] public float OutputPeakDb { get; init; }
    }

    public sealed record EqSpectrumDto
    {
        [JsonPropertyName("inputBinsDb")]  public float[] InputBinsDb  { get; init; } = Array.Empty<float>();
        [JsonPropertyName("outputBinsDb")] public float[] OutputBinsDb { get; init; } = Array.Empty<float>();
        [JsonPropertyName("binCount")]     public int     BinCount     { get; init; }
        [JsonPropertyName("fMinHz")]       public float   FMinHz       { get; init; }
        [JsonPropertyName("fMaxHz")]       public float   FMaxHz       { get; init; }
        [JsonPropertyName("dbFloor")]      public float   DbFloor      { get; init; }
    }
}

// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus voice audio chain v1 — Compressor plugin entry point (issue #332 Phase 1).
// Copyright (C) 2025-2026 KB2UKA and contributors.

using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Audio;
using Zeus.Plugins.Contracts.Extensions;

namespace Openhpsdr.Zeus.Samples.Compressor;

/// <summary>
/// Plugin entry point. Wires the realtime <see cref="CompressorDsp"/> into the
/// Zeus audio chain at the manifest-declared slot (<c>tx.pre-cfc</c>) and
/// surfaces parameter + meter REST under <c>/api/plugins/{id}/...</c>.
///
/// Parameters persist via <see cref="IPluginSettings"/> so operator dial-in
/// survives backend restarts — same pattern Zeus already uses for drive /
/// TUN-drive after PR #359.
///
/// IUiPlugin (the operator-facing panel) ships in a follow-up commit. Once
/// Zeus's TxAudioToolsPanel queries plugins by the <c>tx-audio-tools.chain</c>
/// slot (Brian has that queued — TxAudioToolsPanel.tsx today renders CFC only),
/// the panel will appear in Settings → TX Audio Tools.
/// </summary>
public sealed class CompressorPlugin : IZeusPlugin, IAudioPlugin, IBackendPlugin
{
    private readonly CompressorDsp _dsp = new();
    private IPluginContext? _ctx;

    // ------------------------------------------------------------------
    // IZeusPlugin
    // ------------------------------------------------------------------

    public async Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        _ctx = context;
        context.Logger.LogInformation("Compressor plugin initialising");
        await HydrateFromSettingsAsync(context.Settings, ct);
    }

    public Task ShutdownAsync(CancellationToken ct)
    {
        _ctx?.Logger.LogInformation("Compressor plugin shutting down");
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------
    // IAudioPlugin
    // ------------------------------------------------------------------

    public string DisplayName => "Compressor";

    public AudioPluginRequirements Requirements => new(
        SampleRate: 48000,
        Channels:   1,
        BlockSize:  256);

    public Task InitializeAudioAsync(IAudioHost host, CancellationToken ct)
    {
        _dsp.Initialize(host.CurrentSampleRate);
        _ctx?.Logger.LogInformation(
            "Compressor audio init: SR={SampleRate} Hz, channels={Channels}, blockSize={BlockSize}, slot={Slot}",
            host.CurrentSampleRate,
            host.CurrentChannels,
            host.CurrentBlockSize,
            host.Slot);
        return Task.CompletedTask;
    }

    public void Process(ReadOnlySpan<float> input, Span<float> output, AudioBlockContext ctx)
    {
        // Mono only in v1 — manifest declares channels=1; host honours it.
        // Per IAudioPlugin contract, bypassed slots SHOULD copy through rather
        // than skip the call. AudioChain handles master-bypass short-circuit,
        // so we always run when invoked.
        _dsp.Process(input, output);
    }

    public Task ShutdownAudioAsync(CancellationToken ct)
    {
        _dsp.Reset();
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------
    // IBackendPlugin — params + meters REST surface
    // ------------------------------------------------------------------

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Endpoint route builder is already scoped under the plugin id, so
        // `MapGet("params", ...)` lands at /api/plugins/{id}/params.
        endpoints.MapGet("params",  GetParams);
        endpoints.MapPost("params", SetParams);
        endpoints.MapGet("meters",  GetMeters);
    }

    private IResult GetParams() => Results.Ok(SnapshotParams());

    private async Task<IResult> SetParams(CompressorParamsDto incoming, CancellationToken ct)
    {
        // Apply the operator's update to the realtime DSP. Each setter on
        // CompressorDsp is a single 32-bit write (or 1-byte bool) — fine
        // to do from this thread.
        if (incoming.ThresholdDb.HasValue) _dsp.ThresholdDb = ClampF(incoming.ThresholdDb.Value, -80f, 0f);
        if (incoming.Ratio.HasValue)       _dsp.Ratio       = ClampF(incoming.Ratio.Value,        1f, 20f);
        if (incoming.AttackMs.HasValue)    _dsp.AttackMs    = ClampF(incoming.AttackMs.Value,  0.1f, 100f);
        if (incoming.ReleaseMs.HasValue)   _dsp.ReleaseMs   = ClampF(incoming.ReleaseMs.Value,   1f, 2000f);
        if (incoming.KneeDb.HasValue)      _dsp.KneeDb      = ClampF(incoming.KneeDb.Value,      0f, 24f);
        if (incoming.MakeupDb.HasValue)    _dsp.MakeupDb    = ClampF(incoming.MakeupDb.Value,    0f, 24f);
        if (incoming.Bypass.HasValue)      _dsp.Bypass      = incoming.Bypass.Value;

        await PersistAsync(ct);
        return Results.Ok(SnapshotParams());
    }

    private IResult GetMeters() => Results.Ok(new CompressorMetersDto
    {
        InputPeakDb     = _dsp.LastInputPeakDb,
        OutputPeakDb    = _dsp.LastOutputPeakDb,
        GainReductionDb = _dsp.LastGainReductionDb,
    });

    // ------------------------------------------------------------------
    // Settings persistence (hydrate on init, write on each Set)
    // ------------------------------------------------------------------

    private async Task HydrateFromSettingsAsync(IPluginSettings settings, CancellationToken ct)
    {
        // Each Get returns the persisted value or null on first run; null
        // keeps the CompressorDsp field-initialiser default.
        var threshold = await settings.GetAsync<float?>("threshold_db", ct);
        var ratio     = await settings.GetAsync<float?>("ratio",        ct);
        var attack    = await settings.GetAsync<float?>("attack_ms",    ct);
        var release   = await settings.GetAsync<float?>("release_ms",   ct);
        var knee      = await settings.GetAsync<float?>("knee_db",      ct);
        var makeup    = await settings.GetAsync<float?>("makeup_db",    ct);
        var bypass    = await settings.GetAsync<bool?>("bypass",        ct);

        if (threshold.HasValue) _dsp.ThresholdDb = threshold.Value;
        if (ratio.HasValue)     _dsp.Ratio       = ratio.Value;
        if (attack.HasValue)    _dsp.AttackMs    = attack.Value;
        if (release.HasValue)   _dsp.ReleaseMs   = release.Value;
        if (knee.HasValue)      _dsp.KneeDb      = knee.Value;
        if (makeup.HasValue)    _dsp.MakeupDb    = makeup.Value;
        if (bypass.HasValue)    _dsp.Bypass      = bypass.Value;
    }

    private async Task PersistAsync(CancellationToken ct)
    {
        if (_ctx is null) return;
        var s = _ctx.Settings;
        await s.SetAsync("threshold_db", _dsp.ThresholdDb, ct);
        await s.SetAsync("ratio",        _dsp.Ratio,       ct);
        await s.SetAsync("attack_ms",    _dsp.AttackMs,    ct);
        await s.SetAsync("release_ms",   _dsp.ReleaseMs,   ct);
        await s.SetAsync("knee_db",      _dsp.KneeDb,      ct);
        await s.SetAsync("makeup_db",    _dsp.MakeupDb,    ct);
        await s.SetAsync("bypass",       _dsp.Bypass,      ct);
    }

    private CompressorParamsDto SnapshotParams() => new()
    {
        ThresholdDb = _dsp.ThresholdDb,
        Ratio       = _dsp.Ratio,
        AttackMs    = _dsp.AttackMs,
        ReleaseMs   = _dsp.ReleaseMs,
        KneeDb      = _dsp.KneeDb,
        MakeupDb    = _dsp.MakeupDb,
        Bypass      = _dsp.Bypass,
    };

    private static float ClampF(float v, float lo, float hi) => MathF.Max(lo, MathF.Min(hi, v));

    // ------------------------------------------------------------------
    // DTOs — wire-format for the params + meters endpoints
    // ------------------------------------------------------------------

    /// <summary>
    /// Operator-facing parameter set. Nullable on the wire so partial updates
    /// land cleanly (PATCH-style semantics on top of POST).
    /// </summary>
    public sealed record CompressorParamsDto
    {
        [JsonPropertyName("thresholdDb")] public float? ThresholdDb { get; init; }
        [JsonPropertyName("ratio")]       public float? Ratio       { get; init; }
        [JsonPropertyName("attackMs")]    public float? AttackMs    { get; init; }
        [JsonPropertyName("releaseMs")]   public float? ReleaseMs   { get; init; }
        [JsonPropertyName("kneeDb")]      public float? KneeDb      { get; init; }
        [JsonPropertyName("makeupDb")]    public float? MakeupDb    { get; init; }
        [JsonPropertyName("bypass")]      public bool?  Bypass      { get; init; }
    }

    public sealed record CompressorMetersDto
    {
        [JsonPropertyName("inputPeakDb")]     public float InputPeakDb     { get; init; }
        [JsonPropertyName("outputPeakDb")]    public float OutputPeakDb    { get; init; }
        [JsonPropertyName("gainReductionDb")] public float GainReductionDb { get; init; }
    }
}

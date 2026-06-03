// SPDX-License-Identifier: GPL-2.0-or-later
//
// CW SCAF audio filter — plugin entry point. Wires the realtime ScafDsp into
// the Zeus RX audio chain at the manifest-declared slot (rx.post-demod) and
// surfaces parameter + meter REST under /api/plugins/{id}/...
// Copyright (C) 2026 contributors.

using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Audio;
using Zeus.Plugins.Contracts.Extensions;

namespace Openhpsdr.Zeus.Samples.CwScaf;

/// <summary>
/// CW Switched-Capacitor Audio Filter. A variable-bandwidth audio bandpass on
/// the RX (post-demod) audio path, tuned to the operator's CW pitch to lift
/// weak CW out of the noise and reject adjacent signals — the DSP equivalent
/// of bolting an outboard SCAF onto the receiver's audio output.
///
/// Runs in the dedicated RX insert chain (rx.post-demod slot) — separate from
/// the TX voice-processing chain, with its own filter state. Parameters persist
/// via <see cref="IPluginSettings"/> so the operator's pitch/width survive
/// backend restarts.
/// </summary>
public sealed class CwScafPlugin : IZeusPlugin, IAudioPlugin, IBackendPlugin
{
    private readonly ScafDsp _dsp = new();
    private IPluginContext? _ctx;

    // ------------------------------------------------------------------
    // IZeusPlugin
    // ------------------------------------------------------------------

    public async Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        _ctx = context;
        context.Logger.LogInformation("CW SCAF plugin initialising");
        await HydrateFromSettingsAsync(context.Settings, ct);
    }

    public Task ShutdownAsync(CancellationToken ct)
    {
        _ctx?.Logger.LogInformation("CW SCAF plugin shutting down");
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------
    // IAudioPlugin
    // ------------------------------------------------------------------

    public string DisplayName => "CW SCAF";

    public AudioPluginRequirements Requirements => new(
        SampleRate: 48000,
        Channels:   1,
        BlockSize:  256);

    public Task InitializeAudioAsync(IAudioHost host, CancellationToken ct)
    {
        _dsp.Initialize(host.CurrentSampleRate);
        _ctx?.Logger.LogInformation(
            "CW SCAF audio init: SR={SampleRate} Hz, channels={Channels}, blockSize={BlockSize}, slot={Slot}",
            host.CurrentSampleRate, host.CurrentChannels, host.CurrentBlockSize, host.Slot);
        return Task.CompletedTask;
    }

    public void Process(ReadOnlySpan<float> input, Span<float> output, AudioBlockContext ctx)
    {
        // Mono RX audio. AudioChain handles master-bypass short-circuit; the
        // per-plugin Bypass toggle is honoured inside ScafDsp.Process.
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
        endpoints.MapGet("params",  GetParams);
        endpoints.MapPost("params", SetParams);
        endpoints.MapGet("meters",  GetMeters);
    }

    private IResult GetParams() => Results.Ok(SnapshotParams());

    private async Task<IResult> SetParams(ScafParamsDto incoming, CancellationToken ct)
    {
        if (incoming.CenterHz.HasValue)    _dsp.CenterHz    = ClampF(incoming.CenterHz.Value,    100f, 4000f);
        if (incoming.BandwidthHz.HasValue) _dsp.BandwidthHz = ClampF(incoming.BandwidthHz.Value,   25f, 2000f);
        if (incoming.Sections.HasValue)    _dsp.Sections    = Math.Clamp(incoming.Sections.Value,   1, ScafDsp.MaxSections);
        if (incoming.Bypass.HasValue)      _dsp.Bypass      = incoming.Bypass.Value;

        await PersistAsync(ct);
        return Results.Ok(SnapshotParams());
    }

    private IResult GetMeters() => Results.Ok(new ScafMetersDto
    {
        InputPeak  = _dsp.LastInputPeak,
        OutputPeak = _dsp.LastOutputPeak,
    });

    // ------------------------------------------------------------------
    // Settings persistence
    // ------------------------------------------------------------------

    private async Task HydrateFromSettingsAsync(IPluginSettings settings, CancellationToken ct)
    {
        var center   = await settings.GetAsync<float?>("center_hz",    ct);
        var width    = await settings.GetAsync<float?>("bandwidth_hz", ct);
        var sections = await settings.GetAsync<int?>("sections",       ct);
        var bypass   = await settings.GetAsync<bool?>("bypass",        ct);

        if (center.HasValue)   _dsp.CenterHz    = center.Value;
        if (width.HasValue)    _dsp.BandwidthHz = width.Value;
        if (sections.HasValue) _dsp.Sections    = sections.Value;
        if (bypass.HasValue)   _dsp.Bypass      = bypass.Value;
    }

    private async Task PersistAsync(CancellationToken ct)
    {
        if (_ctx is null) return;
        var s = _ctx.Settings;
        await s.SetAsync("center_hz",    _dsp.CenterHz,    ct);
        await s.SetAsync("bandwidth_hz", _dsp.BandwidthHz, ct);
        await s.SetAsync("sections",     _dsp.Sections,    ct);
        await s.SetAsync("bypass",       _dsp.Bypass,      ct);
    }

    private ScafParamsDto SnapshotParams() => new()
    {
        CenterHz    = _dsp.CenterHz,
        BandwidthHz = _dsp.BandwidthHz,
        Sections    = _dsp.Sections,
        Bypass      = _dsp.Bypass,
    };

    private static float ClampF(float v, float lo, float hi) => MathF.Max(lo, MathF.Min(hi, v));

    // ------------------------------------------------------------------
    // DTOs
    // ------------------------------------------------------------------

    /// <summary>Operator-facing parameters. Nullable on the wire for PATCH-style
    /// partial updates on top of POST.</summary>
    public sealed record ScafParamsDto
    {
        [JsonPropertyName("centerHz")]    public float? CenterHz    { get; init; }
        [JsonPropertyName("bandwidthHz")] public float? BandwidthHz { get; init; }
        [JsonPropertyName("sections")]    public int?   Sections    { get; init; }
        [JsonPropertyName("bypass")]      public bool?  Bypass      { get; init; }
    }

    public sealed record ScafMetersDto
    {
        [JsonPropertyName("inputPeak")]  public float InputPeak  { get; init; }
        [JsonPropertyName("outputPeak")] public float OutputPeak { get; init; }
    }
}

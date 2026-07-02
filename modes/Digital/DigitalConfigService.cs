// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus Digital · FT8/FT4 — plugin-side config state.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Replaces the in-core SpottingManagementService/SpottingSettingsStore (LiteDB)
// and the live-decode half of WsjtxManagementService/WsjtxConfigStore.
//
// Three pieces of state, each persisted through IPluginContext.Settings (the
// plugin key/value store — never LiteDB directly):
//   * spotting config  — PSK Reporter / WSPRnet enables + identity override
//                        (today's SpottingRuntimeConfig shape, both DEFAULT OFF)
//   * operator identity — callsign + grid PUSHED by the core UI, which resolves
//                        its override → QRZ-home fallback before pushing (the
//                        core-side OperatorIdentityResolver stays in core)
//   * wsjtx-live config — the live WSJT-X UDP stream target (DEFAULT OFF)
//
// Like the WSJT-X broadcaster (and unlike CAT/TCI) all consumers only SEND, so
// config changes apply immediately — there is no current/pending split and no
// RequiresRestart. Persists are awaited by the config endpoints so a POST that
// returned 200 is actually on disk.

using Microsoft.Extensions.Logging;
using Zeus.Plugins.Contracts;

namespace Openhpsdr.Zeus.Plugins.Digital;

public sealed class DigitalConfigService
{
    internal const string SpottingKey = "spotting_config";
    internal const string IdentityKey = "operator_identity";
    internal const string WsjtxLiveKey = "wsjtx_live";

    private readonly ILogger _log;
    private readonly IPluginSettings? _settings;
    private readonly object _sync = new();
    private SpottingRuntimeConfig _spotting = new();
    private OperatorIdentityConfig _identity = new("", "");
    private WsjtxLiveConfig _wsjtxLive = new();

    /// <summary>Test seam: in-memory only (no persistence).</summary>
    internal DigitalConfigService(ILogger log, IPluginSettings? settings = null)
    {
        _log = log;
        _settings = settings;
    }

    /// <summary>Load persisted state (defaults when nothing is stored — all
    /// egress OFF, empty identity). Called once from plugin init.</summary>
    public static async Task<DigitalConfigService> LoadAsync(
        IPluginSettings settings, ILogger log, CancellationToken ct)
    {
        var svc = new DigitalConfigService(log, settings);
        // Default OFF when nothing is persisted — network egress is opt-in.
        svc._spotting = Normalize(await settings.GetAsync<SpottingRuntimeConfig>(SpottingKey, ct)
            .ConfigureAwait(false) ?? new SpottingRuntimeConfig());
        svc._identity = Normalize(await settings.GetAsync<OperatorIdentityConfig>(IdentityKey, ct)
            .ConfigureAwait(false) ?? new OperatorIdentityConfig("", ""));
        svc._wsjtxLive = Normalize(await settings.GetAsync<WsjtxLiveConfig>(WsjtxLiveKey, ct)
            .ConfigureAwait(false) ?? new WsjtxLiveConfig());
        return svc;
    }

    // ---- spotting -------------------------------------------------------------

    public SpottingRuntimeConfig GetSpottingConfig()
    {
        lock (_sync) return _spotting;
    }

    public SpottingStatus GetSpottingStatus()
    {
        var c = GetSpottingConfig();
        var (call, grid) = ResolveOperator();
        return new SpottingStatus(
            PskReporterEnabled: c.PskReporterEnabled,
            WsprnetEnabled: c.WsprnetEnabled,
            Callsign: call,
            Grid: grid,
            IdentityResolved: !string.IsNullOrWhiteSpace(call) && !string.IsNullOrWhiteSpace(grid));
    }

    public async Task<SpottingStatus> SetSpottingConfigAsync(SpottingRuntimeConfig config, CancellationToken ct)
    {
        var normalized = Normalize(config);
        lock (_sync) _spotting = normalized;
        await PersistAsync(SpottingKey, normalized, ct).ConfigureAwait(false);
        _log.LogInformation(
            "digital.spotting.config psk={Psk} wsprnet={Wspr} call={Call} grid={Grid}",
            normalized.PskReporterEnabled, normalized.WsprnetEnabled,
            normalized.Callsign, normalized.Grid);
        return GetSpottingStatus();
    }

    // ---- operator identity ------------------------------------------------------

    public OperatorIdentityConfig GetIdentity()
    {
        lock (_sync) return _identity;
    }

    public async Task<OperatorIdentityConfig> SetIdentityAsync(OperatorIdentityConfig identity, CancellationToken ct)
    {
        var normalized = Normalize(identity);
        lock (_sync) _identity = normalized;
        await PersistAsync(IdentityKey, normalized, ct).ConfigureAwait(false);
        _log.LogInformation("digital.identity.set call={Call} grid={Grid}",
            normalized.Call, normalized.Grid);
        return normalized;
    }

    /// <summary>
    /// Operator callsign/grid. Precedence per field: the identity the core UI
    /// pushed (already resolved override → QRZ home core-side) first, then the
    /// spotting config's own persisted override (legacy/secondary, kept
    /// additively — matching the in-core SpottingManagementService order).
    /// Returns ("","") when no source has them; consumers no-op on that.
    /// </summary>
    public (string Call, string Grid) ResolveOperator()
    {
        OperatorIdentityConfig id;
        SpottingRuntimeConfig c;
        lock (_sync) { id = _identity; c = _spotting; }
        string call = !string.IsNullOrWhiteSpace(id.Call) ? id.Call! : c.Callsign;
        string grid = !string.IsNullOrWhiteSpace(id.Grid) ? id.Grid! : c.Grid;
        return (call, grid);
    }

    // ---- wsjtx-live -------------------------------------------------------------

    public WsjtxLiveConfig GetWsjtxLiveConfig()
    {
        lock (_sync) return _wsjtxLive;
    }

    public async Task<WsjtxLiveConfig> SetWsjtxLiveConfigAsync(WsjtxLiveConfig config, CancellationToken ct)
    {
        var normalized = Normalize(config);
        lock (_sync) _wsjtxLive = normalized;
        await PersistAsync(WsjtxLiveKey, normalized, ct).ConfigureAwait(false);
        _log.LogInformation(
            "digital.wsjtx-live.config enabled={Enabled} host={Host} port={Port} multicast={Mc}",
            normalized.Enabled, normalized.Host, normalized.Port, normalized.Multicast);
        return normalized;
    }

    // ---- helpers ---------------------------------------------------------------

    private async Task PersistAsync<T>(string key, T value, CancellationToken ct)
    {
        if (_settings is null) return;
        try
        {
            await _settings.SetAsync(key, value, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A failed persist only loses the value across a restart — the live
            // config above already applied.
            _log.LogWarning(ex, "digital.config.persist failed key={Key}", key);
        }
    }

    private static SpottingRuntimeConfig Normalize(SpottingRuntimeConfig c) => new(
        PskReporterEnabled: c.PskReporterEnabled,
        WsprnetEnabled: c.WsprnetEnabled,
        Callsign: NormalizeCall(c.Callsign),
        Grid: NormalizeGrid(c.Grid));

    private static OperatorIdentityConfig Normalize(OperatorIdentityConfig id) => new(
        Call: NormalizeCall(id.Call),
        Grid: NormalizeGrid(id.Grid));

    private static WsjtxLiveConfig Normalize(WsjtxLiveConfig c) => new(
        Enabled: c.Enabled,
        Host: string.IsNullOrWhiteSpace(c.Host) ? "127.0.0.1" : c.Host.Trim(),
        Port: c.Port is > 0 and <= 65535 ? c.Port : 2237,
        Multicast: c.Multicast,
        InstanceId: string.IsNullOrWhiteSpace(c.InstanceId) ? "WSJT-X" : c.InstanceId.Trim(),
        MulticastTtl: Math.Clamp(c.MulticastTtl, 1, 255));

    private static string NormalizeCall(string? call) =>
        string.IsNullOrWhiteSpace(call) ? "" : call.Trim().ToUpperInvariant();

    private static string NormalizeGrid(string? grid)
    {
        if (string.IsNullOrWhiteSpace(grid)) return "";
        var g = grid.Trim().ToUpperInvariant();
        return g.Length > 6 ? g[..6] : g;
    }
}

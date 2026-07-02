// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus Digital plugin — extracted from Zeus core
// (Zeus.Server.Hosting/Wsjtx/WsjtxLiveEmitter.cs). GPL-2.0-or-later.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// WsjtxLiveEmitter — the live WSJT-X UDP stream GridTracker / JTAlert need for
// map / roster / alerts: Heartbeat (0) ~15 s, Status (1) on MOX change + a slow
// periodic tick, Decode (2) one per decoded FT8/FT4 line, WSPRDecode (10) one per
// WSPR spot.
//
// Same proven leaf-subscriber seam as PskReporterReporter / WsprnetReporter: the
// decode/spot handlers ONLY read existing events + the host radio reader and
// ENQUEUE a UDP send — they never call back into the radio/DSP/TX path, and they
// swallow their own errors, so a reporter fault can never disturb decode or TX.
//
// Extraction deltas only at the seams: dial freq + mode come from
// IRadioStateReader (FrequencyHz / Mode / MoxChanged) instead of
// RadioService.Snapshot(); config is the plugin-side WsjtxLiveConfig (single
// Enabled flag — the core Enabled && SendLiveDecodes pair collapses to it);
// identity comes from DigitalConfigService (pushed by the core UI); the hosted-
// service shell is replaced by a self-owned loop task.
//
// SAFETY: SEND-ONLY (the sender has no listener). Gated on cfg.Enabled — the
// disabled-default path never touches the radio reader on the decode thread.
// PureSignal / drive / power are never touched.

using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Openhpsdr.Zeus.Plugins.Digital.Dsp;
using Zeus.Plugins.Contracts;

namespace Openhpsdr.Zeus.Plugins.Digital.Wsjtx;

public sealed partial class WsjtxLiveEmitter : IDisposable
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StatusInterval = TimeSpan.FromSeconds(5);
    private static readonly string ZeusVersion = BuildVersion();

    private readonly ILogger _log;
    private readonly Func<WsjtxLiveConfig> _config;
    private readonly Func<byte[], CancellationToken, Task> _send;
    private readonly Ft8Service? _ft8;
    private readonly WsprService? _wspr;
    private readonly IRadioStateReader? _radio;
    private readonly Ft8TxService? _tx;
    private readonly DigitalConfigService? _operator;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _disposed;

    public WsjtxLiveEmitter(
        ILogger log,
        DigitalConfigService config,
        Func<byte[], CancellationToken, Task> send,
        Ft8Service ft8,
        WsprService wspr,
        IRadioStateReader? radio,
        Ft8TxService tx)
    {
        _log = log;
        _config = config.GetWsjtxLiveConfig;
        _send = send;
        _ft8 = ft8;
        _wspr = wspr;
        _radio = radio;
        _tx = tx;
        _operator = config;

        _ft8.DecodesReady += OnDecodes;
        _wspr.SpotsReady += OnSpots;
        if (_radio is not null) _radio.MoxChanged += OnMoxChanged;
    }

    // Test seam: no DSP/radio wiring. Drives the gate + field mapping via the
    // OnDecodes/OnSpots handlers and a captured send delegate.
    internal WsjtxLiveEmitter(
        ILogger log,
        Func<WsjtxLiveConfig> config,
        Func<byte[], CancellationToken, Task> send)
    {
        _log = log;
        _config = config;
        _send = send;
        _ft8 = null;
        _wspr = null;
        _radio = null;
        _tx = null;
        _operator = null;
    }

    /// <summary>Start the heartbeat/status loop. Called once from plugin init;
    /// tests skip it and drive the handlers directly.</summary>
    public void Start()
    {
        if (_loop is not null) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _loop = Task.Run(() => RunAsync(ct));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ft8 is not null) _ft8.DecodesReady -= OnDecodes;
        if (_wspr is not null) _wspr.SpotsReady -= OnSpots;
        if (_radio is not null) _radio.MoxChanged -= OnMoxChanged;
        _cts?.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { /* shutting down */ }
        _cts?.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var lastHeartbeat = DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var cfg = _config();
                if (cfg.Enabled)
                {
                    var now = DateTime.UtcNow;
                    if (now - lastHeartbeat >= HeartbeatInterval)
                    {
                        lastHeartbeat = now;
                        await SendHeartbeatAsync(cfg, ct).ConfigureAwait(false);
                    }
                    await SendStatusAsync(cfg, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "wsjtx.live periodic tick failed");
            }

            try { await Task.Delay(StatusInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    // ---- event handlers (decode worker / state thread) -----------------------

    private void OnDecodes(Ft8DecodeBatch batch) => HandleDecodes(batch);

    private void OnSpots(WsprSpotBatch batch) => HandleSpots(batch);

    // Gate (enable flag) then build + send. Internal so the gating is
    // unit-testable without a DSP graph: disabled => nothing sent,
    // enabled => one datagram per decoded line.
    internal void HandleDecodes(Ft8DecodeBatch batch)
    {
        try
        {
            var cfg = _config();
            if (!cfg.Enabled) return; // zero cost on the default path
            foreach (var d in BuildDecodeDatagrams(cfg.InstanceId, batch))
                _ = _send(d, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "wsjtx.live decode emit failed");
        }
    }

    internal void HandleSpots(WsprSpotBatch batch)
    {
        try
        {
            var cfg = _config();
            if (!cfg.Enabled) return;
            foreach (var d in BuildWsprDatagrams(cfg.InstanceId, batch))
                _ = _send(d, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "wsjtx.live wspr emit failed");
        }
    }

    private void OnMoxChanged(bool mox)
    {
        try
        {
            var cfg = _config();
            if (!cfg.Enabled) return;
            _ = SendStatusAsync(cfg, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "wsjtx.live mox status failed");
        }
    }

    // ---- senders -------------------------------------------------------------

    private Task SendHeartbeatAsync(WsjtxLiveConfig cfg, CancellationToken ct)
    {
        var dg = WsjtxMessage.EncodeHeartbeat(cfg.InstanceId, maxSchema: 3, version: ZeusVersion, revision: "");
        return _send(dg, ct);
    }

    private Task SendStatusAsync(WsjtxLiveConfig cfg, CancellationToken ct)
    {
        if (_radio is null) return Task.CompletedTask;

        long dialHz = _radio.FrequencyHz;
        var (deCall, deGrid) = _operator?.ResolveOperator() ?? ("", "");

        string mode;
        uint trPeriodMs;
        if (_wspr?.IsEnabled == true)
        {
            mode = "WSPR";
            trPeriodMs = 120_000;
        }
        else if (_ft8?.IsEnabled == true)
        {
            mode = _ft8.ActiveProtocol == Ft8Protocol.Ft4 ? "FT4" : "FT8";
            trPeriodMs = _ft8.ActiveProtocol == Ft8Protocol.Ft4 ? 7_500u : 15_000u;
        }
        else
        {
            mode = _radio.Mode.ToUpperInvariant();
            trPeriodMs = 0;
        }

        var txStatus = _tx?.Status();
        bool txEnabled = txStatus?.Armed ?? false;
        bool transmitting = txStatus?.Transmitting ?? false;
        string txMessage = txStatus?.Message ?? "";
        uint txDf = (uint)Math.Max(0, txStatus?.AudioHz ?? 0);
        string dxCall = ParseDxCall(txMessage, deCall);

        var dg = WsjtxMessage.EncodeStatus(
            instanceId: cfg.InstanceId,
            dialFrequencyHz: (ulong)Math.Max(0, dialHz),
            mode: mode,
            dxCall: dxCall,
            report: "",
            txMode: mode,
            txEnabled: txEnabled,
            transmitting: transmitting,
            decoding: (_ft8?.IsEnabled ?? false) || (_wspr?.IsEnabled ?? false),
            rxDf: 0,
            txDf: txDf,
            deCall: deCall,
            deGrid: deGrid,
            dxGrid: "",
            txWatchdog: false,
            subMode: "",
            fastMode: false,
            specialOperationMode: 0,
            frequencyTolerance: 0,
            trPeriod: trPeriodMs,
            configurationName: "Zeus",
            txMessage: txMessage);
        return _send(dg, ct);
    }

    // ---- pure field mappers (unit-tested directly) ---------------------------

    /// <summary>Map an FT8/FT4 decode batch to one Decode (type 2) datagram per line.</summary>
    internal static IReadOnlyList<byte[]> BuildDecodeDatagrams(string instanceId, Ft8DecodeBatch batch)
    {
        uint timeMs = MsSinceUtcMidnight(batch.SlotStartUtc);
        string mode = batch.Protocol == Ft8Protocol.Ft4 ? "FT4" : "FT8";
        var list = new List<byte[]>(batch.Decodes.Count);
        foreach (var d in batch.Decodes)
        {
            uint df = (uint)Math.Clamp((int)Math.Round(d.FreqHz), 0, int.MaxValue);
            list.Add(WsjtxMessage.EncodeDecode(
                instanceId,
                isNew: true,
                timeMsSinceMidnight: timeMs,
                snr: (int)Math.Round(d.SnrDb),
                deltaTimeSec: d.DtSec,
                deltaFrequencyHz: df,
                mode: mode,
                message: d.Text ?? "",
                lowConfidence: false,
                offAir: false));
        }
        return list;
    }

    /// <summary>Map a WSPR spot batch to one WSPRDecode (type 10) datagram per spot.</summary>
    internal static IReadOnlyList<byte[]> BuildWsprDatagrams(string instanceId, WsprSpotBatch batch)
    {
        uint timeMs = MsSinceUtcMidnight(batch.SlotStartUtc);
        var list = new List<byte[]>(batch.Spots.Count);
        foreach (var sp in batch.Spots)
        {
            ParseWsprMessage(sp.Message, out string call, out string grid, out int power);
            ulong freqHz = sp.FreqMhz > 0 ? (ulong)Math.Round(sp.FreqMhz * 1_000_000.0) : 0UL;
            list.Add(WsjtxMessage.EncodeWsprDecode(
                instanceId,
                isNew: true,
                timeMsSinceMidnight: timeMs,
                snr: (int)Math.Round(sp.SnrDb),
                deltaTimeSec: sp.DtSec,
                frequencyHz: freqHz,
                drift: sp.DriftHz,
                callsign: call,
                grid: grid,
                power: power,
                offAir: false));
        }
        return list;
    }

    internal static uint MsSinceUtcMidnight(DateTime slotStart)
    {
        var utc = slotStart.Kind switch
        {
            DateTimeKind.Utc => slotStart,
            DateTimeKind.Local => slotStart.ToUniversalTime(),
            _ => DateTime.SpecifyKind(slotStart, DateTimeKind.Utc),
        };
        return (uint)(((utc.Hour * 60 + utc.Minute) * 60 + utc.Second) * 1000 + utc.Millisecond);
    }

    [GeneratedRegex(@"^[A-R]{2}[0-9]{2}([A-X]{2})?$")]
    private static partial Regex GridRegex();

    // Tokenise a WSPR message ("CALL GRID DBM", "PFX/CALL DBM", "<CALL> GRID6 DBM").
    // call = first token (hash markers stripped), grid = a Maidenhead token if
    // present, power = the trailing integer (dBm) if present. Never throws.
    internal static void ParseWsprMessage(string? message, out string call, out string grid, out int power)
    {
        call = "";
        grid = "";
        power = 0;
        if (string.IsNullOrWhiteSpace(message)) return;

        var tokens = message.Trim().ToUpperInvariant()
            .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return;

        call = tokens[0].Trim('<', '>');

        if (tokens.Length >= 2 && int.TryParse(tokens[^1], out int p))
            power = p;

        foreach (var t in tokens)
        {
            if (GridRegex().IsMatch(t)) { grid = t; break; }
        }
    }

    // Extract the DX (target) call from OUR staged TX message: the first token
    // that looks like a callsign and is not our own. Returns "" when none.
    internal static string ParseDxCall(string? txMessage, string deCall)
    {
        if (string.IsNullOrWhiteSpace(txMessage)) return "";
        var tokens = txMessage.Trim().ToUpperInvariant()
            .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var t in tokens)
        {
            if (t == "CQ" || t == "DE") continue;
            var core = t.Trim('<', '>');
            if (LooksLikeCall(core) && !string.Equals(core, deCall, StringComparison.OrdinalIgnoreCase))
                return core;
        }
        return "";
    }

    private static bool LooksLikeCall(string s)
    {
        if (s.Length < 3) return false;
        bool hasLetter = false, hasDigit = false;
        foreach (var c in s)
        {
            if (c is >= 'A' and <= 'Z') hasLetter = true;
            else if (c is >= '0' and <= '9') hasDigit = true;
            else if (c != '/') return false;
        }
        if (!hasLetter || !hasDigit) return false;
        return !GridRegex().IsMatch(s); // a bare locator is not a call
    }

    private static string BuildVersion()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        return v is null ? "Zeus" : $"Zeus {v.Major}.{v.Minor}.{v.Build}";
    }
}

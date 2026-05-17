// SPDX-License-Identifier: GPL-2.0-or-later
//
// RF-Kit RF2K-S amplifier plugin for Openhpsdr-Zeus.
//
// Lifted from the in-tree Rf2kService / Rf2kVncClient / Rf2kSettingsStore
// trio that previously lived in Zeus.Server.Hosting. Behaviour and wire
// format are preserved verbatim so the existing UI calls and operator
// muscle memory continue to work.
//
//   * REST poll loop (Bottle/Python REST server on TCP/8080) for
//     /info, /data, /power, /tuner, /operate-mode, /operational-interface,
//     /antennas, /antennas/active.
//   * Mutating endpoints PUT /operate-mode, /operational-interface,
//     /antennas/active and POST /error/reset.
//   * Minimal RFB (VNC) PointerEvent injector on TCP/5900 for the two
//     front-panel actions the REST API doesn't expose: Tune and Bypass.
//     See the VNC client section below for the protocol notes.
//
// Settings are persisted through IPluginContext.Settings (the LiteDB-
// backed plugin-scoped store) under the key "config", replacing the
// previous Rf2kSettingsStore that wrote to its own collection in
// zeus-prefs.db.
//
// All DTOs are inlined here so the plugin assembly is self-contained.
// Wire field names are unchanged — the UI module dynamic-loaded by Zeus
// still receives the same JSON shape.

using System.Buffers.Binary;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Extensions;

namespace Openhpsdr.Zeus.Plugins.Rf2k;

// ============================================================================
//  DTOs — inlined from Zeus.Contracts/Rf2kDtos.cs
// ============================================================================

public sealed record Rf2kConfig(
    bool Enabled = false,
    string Host = "10.70.120.41",
    int Port = 8080,
    int VncPort = 5900,
    string VncPassword = "",
    int PollingIntervalMs = 500,
    int TuneClickX = 0,
    int TuneClickY = 0,
    int BypassClickX = 0,
    int BypassClickY = 0);

public sealed record Rf2kReading(double Value, string? Unit);

public sealed record Rf2kPeakReading(double Value, double MaxValue, string? Unit);

public sealed record Rf2kInfo(string? Device, Rf2kSoftwareVersion? SoftwareVersion, string? CustomDeviceName);

public sealed record Rf2kSoftwareVersion(int? Gui, int? Controller);

public sealed record Rf2kData(Rf2kReading? Band, Rf2kReading? Frequency, string? Status);

public sealed record Rf2kPower(
    Rf2kReading? Temperature,
    Rf2kReading? Voltage,
    Rf2kReading? Current,
    Rf2kPeakReading? Forward,
    Rf2kPeakReading? Reflected,
    Rf2kPeakReading? Swr);

public sealed record Rf2kTuner(string? Mode, string? Setup, double? L, double? C, double? TunedFrequency);

public sealed record Rf2kOperateMode(string OperateMode);

public sealed record Rf2kOperationalInterface(string OperationalInterface, string? Error);

public sealed record Rf2kAntenna(string? Type, int? Number, string? State);

public sealed record Rf2kAntennaList(IReadOnlyList<Rf2kAntenna> Antennas);

public sealed record Rf2kActiveAntenna(string? Type, int? Number);

public sealed record Rf2kSetOperateRequest(string Mode);

public sealed record Rf2kSetInterfaceRequest(string Interface);

public sealed record Rf2kSetAntennaRequest(string Type, int? Number);

public sealed record Rf2kTestRequest(string Host, int Port);

public sealed record Rf2kClickRequest(int X, int Y);

public sealed record Rf2kStatus(
    bool Enabled,
    bool Connected,
    string Host,
    int Port,
    Rf2kInfo? Info,
    Rf2kData? Data,
    Rf2kPower? Power,
    Rf2kTuner? Tuner,
    string? OperateMode,
    string? OperationalInterface,
    string? OperationalInterfaceError,
    Rf2kActiveAntenna? ActiveAntenna,
    IReadOnlyList<Rf2kAntenna>? Antennas,
    string? Error,
    DateTimeOffset? LastSampleUtc);

public sealed record Rf2kTestResult(bool Ok, string? Error);

// ============================================================================
//  Plugin entry — wires IZeusPlugin + IBackendPlugin around the service
// ============================================================================

public sealed class Rf2kPlugin : IZeusPlugin, IBackendPlugin
{
    private const string ConfigKey = "config";

    private IPluginContext? _ctx;
    private Rf2kService? _service;
    private CancellationTokenSource? _runCts;
    private Task? _runTask;

    public async Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        _ctx = context;

        // Hydrate persisted config (or default Rf2kConfig if first run).
        var persisted = await SafeGetConfigAsync(context, ct).ConfigureAwait(false);

        _service = new Rf2kService(
            context.Logger,
            persisted ?? new Rf2kConfig(),
            persistAsync: cfg => PersistConfigAsync(context, cfg));

        _runCts = new CancellationTokenSource();
        _runTask = _service.RunAsync(_runCts.Token);

        context.Logger.LogInformation(
            "rf2k.plugin.init host={Host}:{Port} enabled={Enabled}",
            _service.GetConfig().Host, _service.GetConfig().Port, _service.GetConfig().Enabled);
    }

    public async Task ShutdownAsync(CancellationToken ct)
    {
        try
        {
            _runCts?.Cancel();
            if (_runTask is not null)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                try { await _runTask.WaitAsync(timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { /* expected on shutdown */ }
            }
        }
        finally
        {
            _service?.Dispose();
            _runCts?.Dispose();
            _service = null;
            _runCts = null;
            _runTask = null;
        }
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("status", () => Results.Ok(GetService().GetStatus()));
        endpoints.MapGet("config", () => Results.Ok(GetService().GetConfig()));

        endpoints.MapPost("config", async (Rf2kConfig cfg, CancellationToken ct) =>
            Results.Ok(await GetService().SetConfigAsync(cfg, ct)));

        endpoints.MapPost("operate", async (Rf2kSetOperateRequest req, CancellationToken ct) =>
            Results.Ok(await GetService().SetOperateModeAsync(req.Mode, ct)));

        endpoints.MapPost("interface", async (Rf2kSetInterfaceRequest req, CancellationToken ct) =>
            Results.Ok(await GetService().SetOperationalInterfaceAsync(req.Interface, ct)));

        endpoints.MapPost("antenna", async (Rf2kSetAntennaRequest req, CancellationToken ct) =>
            Results.Ok(await GetService().SetActiveAntennaAsync(req.Type, req.Number, ct)));

        endpoints.MapPost("reset", async (CancellationToken ct) =>
            Results.Ok(await GetService().ResetErrorAsync(ct)));

        endpoints.MapPost("test", async (Rf2kTestRequest req, CancellationToken ct) =>
            Results.Ok(await GetService().TestAsync(req.Host, req.Port, ct)));

        endpoints.MapPost("tune", async (CancellationToken ct) =>
            Results.Ok(await GetService().SendTuneClickAsync(ct)));

        endpoints.MapPost("bypass", async (CancellationToken ct) =>
            Results.Ok(await GetService().SendBypassClickAsync(ct)));

        endpoints.MapPost("click", async (Rf2kClickRequest req, CancellationToken ct) =>
            Results.Ok(await GetService().SendTestClickAsync(req.X, req.Y, ct)));
    }

    private Rf2kService GetService() =>
        _service ?? throw new InvalidOperationException("Rf2kPlugin not initialised");

    private static async Task<Rf2kConfig?> SafeGetConfigAsync(IPluginContext context, CancellationToken ct)
    {
        try
        {
            return await context.Settings.GetAsync<Rf2kConfig>(ConfigKey, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(ex, "rf2k.settings.get failed; using defaults");
            return null;
        }
    }

    private static async Task PersistConfigAsync(IPluginContext context, Rf2kConfig cfg)
    {
        try
        {
            await context.Settings.SetAsync(ConfigKey, cfg).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(ex, "rf2k.settings.set failed; in-memory config kept");
        }
    }
}

// ============================================================================
//  Rf2kService — REST poll loop + command surface
// ============================================================================

internal sealed class Rf2kService : IDisposable
{
    private static readonly TimeSpan ReconnectBackoff = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);

    // Allow this many consecutive poll-cycle failures before we flip Connected
    // to false. Each cycle hits 8 sequential HTTP endpoints; if any one of them
    // blips the entire cycle fails. Flipping Connected on a single blip causes
    // a panel-visible flap every 5 s (the ReconnectBackoff window) even though
    // the next cycle reconnects cleanly. A small tolerance lets us ride out
    // transient blips while still detecting a real disconnect after
    // ~3 × PollingIntervalMs of sustained failure.
    private const int ConsecutiveFailuresBeforeDisconnect = 3;

    /// <summary>Match the firmware's snake_case field naming.</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ILogger _log;
    private readonly Func<Rf2kConfig, Task> _persistAsync;
    private readonly HttpClient _http;
    private readonly Rf2kVncClient _vnc;
    private readonly SemaphoreSlim _io = new(1, 1);
    private readonly SemaphoreSlim _configChanged = new(0, 1);

    private volatile Rf2kConfig _config;

    private readonly object _state = new();
    private bool _connected;
    private int _consecutiveFailures;
    private string? _error;
    private DateTimeOffset? _lastSampleUtc;
    private Rf2kInfo? _info;
    private Rf2kData? _data;
    private Rf2kPower? _power;
    private Rf2kTuner? _tuner;
    private string? _operateMode;
    private string? _operationalInterface;
    private string? _operationalInterfaceError;
    private Rf2kActiveAntenna? _activeAntenna;
    private IReadOnlyList<Rf2kAntenna>? _antennas;

    public Rf2kService(ILogger log, Rf2kConfig initialConfig, Func<Rf2kConfig, Task> persistAsync)
    {
        _log = log;
        _config = initialConfig;
        _persistAsync = persistAsync;
        _vnc = new Rf2kVncClient(log);
        _http = new HttpClient { Timeout = RequestTimeout };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    // ------------------------------------------------------------------------
    //  Public API
    // ------------------------------------------------------------------------

    public Rf2kStatus GetStatus()
    {
        var cfg = _config;
        lock (_state)
        {
            return new Rf2kStatus(
                Enabled: cfg.Enabled,
                Connected: _connected,
                Host: cfg.Host,
                Port: cfg.Port,
                Info: _info,
                Data: _data,
                Power: _power,
                Tuner: _tuner,
                OperateMode: _operateMode,
                OperationalInterface: _operationalInterface,
                OperationalInterfaceError: _operationalInterfaceError,
                ActiveAntenna: _activeAntenna,
                Antennas: _antennas,
                Error: _error,
                LastSampleUtc: _lastSampleUtc);
        }
    }

    public Rf2kConfig GetConfig() => _config;

    public async Task<Rf2kStatus> SetConfigAsync(Rf2kConfig next, CancellationToken ct)
    {
        await _io.WaitAsync(ct);
        try
        {
            var sanitized = next with
            {
                Host = string.IsNullOrWhiteSpace(next.Host) ? "10.70.120.41" : next.Host.Trim(),
                Port = next.Port is > 0 and < 65536 ? next.Port : 8080,
                VncPort = next.VncPort is > 0 and < 65536 ? next.VncPort : 5900,
                VncPassword = next.VncPassword ?? string.Empty,
                PollingIntervalMs = Math.Clamp(next.PollingIntervalMs, 250, 10_000),
                TuneClickX = next.TuneClickX is >= 0 and <= 65535 ? next.TuneClickX : 0,
                TuneClickY = next.TuneClickY is >= 0 and <= 65535 ? next.TuneClickY : 0,
                BypassClickX = next.BypassClickX is >= 0 and <= 65535 ? next.BypassClickX : 0,
                BypassClickY = next.BypassClickY is >= 0 and <= 65535 ? next.BypassClickY : 0,
            };
            _config = sanitized;
            try { await _persistAsync(sanitized).ConfigureAwait(false); }
            catch (Exception ex) { _log.LogWarning(ex, "rf2k.persist failed"); }

            // Clear stale snapshot on host change so the panel doesn't show
            // last-known-good values from a different amp.
            lock (_state) ClearSnapshotLocked();

            if (_configChanged.CurrentCount == 0) _configChanged.Release();
        }
        finally
        {
            _io.Release();
        }
        return GetStatus();
    }

    public async Task<Rf2kStatus> SetOperateModeAsync(string mode, CancellationToken ct)
    {
        var normalized = string.Equals(mode, "OPERATE", StringComparison.OrdinalIgnoreCase) ? "OPERATE" : "STANDBY";
        var body = new { operate_mode = normalized };
        var ok = await PutJsonAsync("/operate-mode", body, ct);
        if (ok) await PollOnceAsync(ct);
        return GetStatus();
    }

    public async Task<Rf2kStatus> SetOperationalInterfaceAsync(string iface, CancellationToken ct)
    {
        var normalized = iface?.Trim().ToUpperInvariant() switch
        {
            "UNIV" or "CAT" or "UDP" or "TCI" => iface!.Trim().ToUpperInvariant(),
            _ => "UNIV"
        };
        var body = new { operational_interface = normalized };
        var ok = await PutJsonAsync("/operational-interface", body, ct);
        if (ok) await PollOnceAsync(ct);
        return GetStatus();
    }

    public async Task<Rf2kStatus> SetActiveAntennaAsync(string type, int? number, CancellationToken ct)
    {
        var normalizedType = string.Equals(type, "EXTERNAL", StringComparison.OrdinalIgnoreCase) ? "EXTERNAL" : "INTERNAL";
        object body = normalizedType == "EXTERNAL"
            ? new { type = "EXTERNAL" }
            : new { type = "INTERNAL", number = number ?? 1 };
        var ok = await PutJsonAsync("/antennas/active", body, ct);
        if (ok) await PollOnceAsync(ct);
        return GetStatus();
    }

    public async Task<Rf2kStatus> ResetErrorAsync(CancellationToken ct)
    {
        var ok = await PostNoBodyAsync("/error/reset", ct);
        if (ok) await PollOnceAsync(ct);
        return GetStatus();
    }

    public async Task<Rf2kTestResult> TestAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using var client = new HttpClient { Timeout = RequestTimeout };
            var url = $"http://{host}:{port}/info";
            using var resp = await client.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
                return new Rf2kTestResult(false, $"HTTP {(int)resp.StatusCode}");
            var info = await resp.Content.ReadFromJsonAsync<Rf2kInfo>(Json, ct);
            if (info?.Device is null) return new Rf2kTestResult(false, "Bad payload");
            return new Rf2kTestResult(true, null);
        }
        catch (Exception ex)
        {
            return new Rf2kTestResult(false, ex.Message);
        }
    }

    public async Task<Rf2kTestResult> SendTuneClickAsync(CancellationToken ct)
    {
        var cfg = _config;
        if (cfg.TuneClickX <= 0 && cfg.TuneClickY <= 0)
            return new Rf2kTestResult(false, "Tune click coordinates not configured. Calibrate from the panel settings.");
        var err = await _vnc.SendClickAsync(cfg.Host, cfg.VncPort, (ushort)cfg.TuneClickX, (ushort)cfg.TuneClickY, cfg.VncPassword, ct);
        return new Rf2kTestResult(err is null, err);
    }

    public async Task<Rf2kTestResult> SendBypassClickAsync(CancellationToken ct)
    {
        var cfg = _config;
        if (cfg.BypassClickX <= 0 && cfg.BypassClickY <= 0)
            return new Rf2kTestResult(false, "Bypass click coordinates not configured. Calibrate from the panel settings.");
        var err = await _vnc.SendClickAsync(cfg.Host, cfg.VncPort, (ushort)cfg.BypassClickX, (ushort)cfg.BypassClickY, cfg.VncPassword, ct);
        return new Rf2kTestResult(err is null, err);
    }

    public async Task<Rf2kTestResult> SendTestClickAsync(int x, int y, CancellationToken ct)
    {
        if (x is < 0 or > 65535 || y is < 0 or > 65535)
            return new Rf2kTestResult(false, "Coordinates must be in 0..65535");
        var cfg = _config;
        var err = await _vnc.SendClickAsync(cfg.Host, cfg.VncPort, (ushort)x, (ushort)y, cfg.VncPassword, ct);
        return new Rf2kTestResult(err is null, err);
    }

    // ------------------------------------------------------------------------
    //  Background poll loop
    // ------------------------------------------------------------------------

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var cfg = _config;
            if (!cfg.Enabled)
            {
                lock (_state)
                {
                    if (_connected)
                    {
                        _connected = false;
                        ClearSnapshotLocked();
                    }
                }
                try { await _configChanged.WaitAsync(stoppingToken); } catch (OperationCanceledException) { return; }
                continue;
            }

            var ok = await PollOnceAsync(stoppingToken);
            if (!ok)
            {
                var delayTask = Task.Delay(ReconnectBackoff, stoppingToken);
                var configTask = _configChanged.WaitAsync(stoppingToken);
                await Task.WhenAny(delayTask, configTask);
                continue;
            }

            try { await Task.Delay(cfg.PollingIntervalMs, stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task<bool> PollOnceAsync(CancellationToken ct)
    {
        var cfg = _config;
        if (!cfg.Enabled) return false;
        try
        {
            var info = await GetJsonAsync<Rf2kInfo>("/info", ct);
            var data = await GetJsonAsync<Rf2kData>("/data", ct);
            var power = await GetJsonAsync<Rf2kPower>("/power", ct);
            var tuner = await GetJsonAsync<Rf2kTuner>("/tuner", ct);
            var operate = await GetJsonAsync<Rf2kOperateMode>("/operate-mode", ct);
            var iface = await GetJsonAsync<Rf2kOperationalInterface>("/operational-interface", ct);
            var antennas = await GetJsonAsync<Rf2kAntennaList>("/antennas", ct);
            var active = await GetJsonAsync<Rf2kActiveAntenna>("/antennas/active", ct);

            int priorFailures;
            bool wasDisconnected;
            lock (_state)
            {
                _info = info;
                _data = data;
                _power = power;
                _tuner = tuner;
                _operateMode = operate?.OperateMode;
                _operationalInterface = iface?.OperationalInterface;
                _operationalInterfaceError = iface?.Error;
                _antennas = antennas?.Antennas;
                _activeAntenna = active;
                priorFailures = _consecutiveFailures;
                wasDisconnected = !_connected;
                _connected = true;
                _consecutiveFailures = 0;
                _error = null;
                _lastSampleUtc = DateTimeOffset.UtcNow;
            }
            if (priorFailures > 0)
            {
                _log.LogInformation(
                    "rf2k.poll.recovered after={Failures} failed polls host={Host} wasDisconnected={Was}",
                    priorFailures, cfg.Host, wasDisconnected);
            }
            return true;
        }
        catch (Exception ex)
        {
            int failures;
            bool flippedDisconnected;
            lock (_state)
            {
                _consecutiveFailures++;
                failures = _consecutiveFailures;
                _error = ex.Message;
                if (_connected && failures >= ConsecutiveFailuresBeforeDisconnect)
                {
                    _connected = false;
                    flippedDisconnected = true;
                }
                else
                {
                    flippedDisconnected = false;
                }
            }
            _log.LogWarning(ex,
                "rf2k.poll.failed consecutive={Count}/{Threshold} host={Host} flippedDisconnected={Flipped}",
                failures, ConsecutiveFailuresBeforeDisconnect, cfg.Host, flippedDisconnected);
            return false;
        }
    }

    private async Task<T?> GetJsonAsync<T>(string path, CancellationToken ct) where T : class
    {
        var cfg = _config;
        var url = $"http://{cfg.Host}:{cfg.Port}{path}";
        using var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<T>(Json, ct);
    }

    private async Task<bool> PutJsonAsync(string path, object body, CancellationToken ct)
    {
        var cfg = _config;
        var url = $"http://{cfg.Host}:{cfg.Port}{path}";
        try
        {
            using var resp = await _http.PutAsJsonAsync(url, body, Json, ct);
            if (!resp.IsSuccessStatusCode)
            {
                lock (_state) { _error = $"PUT {path} → HTTP {(int)resp.StatusCode}"; }
                return false;
            }
            lock (_state) { _error = null; }
            return true;
        }
        catch (Exception ex)
        {
            lock (_state) { _error = $"PUT {path}: {ex.Message}"; }
            return false;
        }
    }

    private async Task<bool> PostNoBodyAsync(string path, CancellationToken ct)
    {
        var cfg = _config;
        var url = $"http://{cfg.Host}:{cfg.Port}{path}";
        try
        {
            using var resp = await _http.PostAsync(url, content: null, ct);
            if (!resp.IsSuccessStatusCode)
            {
                lock (_state) { _error = $"POST {path} → HTTP {(int)resp.StatusCode}"; }
                return false;
            }
            lock (_state) { _error = null; }
            return true;
        }
        catch (Exception ex)
        {
            lock (_state) { _error = $"POST {path}: {ex.Message}"; }
            return false;
        }
    }

    private void ClearSnapshotLocked()
    {
        _info = null; _data = null; _power = null; _tuner = null;
        _operateMode = null; _operationalInterface = null; _operationalInterfaceError = null;
        _antennas = null; _activeAntenna = null;
        _lastSampleUtc = null;
        _consecutiveFailures = 0;
    }

    public void Dispose()
    {
        _http.Dispose();
        _io.Dispose();
        _configChanged.Dispose();
    }
}

// ============================================================================
//  Rf2kVncClient — minimal RFB PointerEvent injector
// ============================================================================
//
// The RF2K-S REST API does NOT expose endpoints for the Tune button or for
// changing the tuner mode (BYPASS/AUTO). Those actions are implemented as
// local Tk button handlers in
//   CT1IQI/RF2K-S/.../main_screen/tuners.py:156-160
//   .../main_screen/operating_buttons.py:TuneButton.onAutotuneClicked
// The CAT, UDP, and TCI operational-interface handlers are receive-only
// (frequency-tracking) — they cannot carry control commands either.
//
// So the only mechanical path to remotely engage Tune/Bypass is to inject
// a mouse-click via the amp's VNC server (port 5900) at the on-screen
// coordinates of the Tk button.
//
// IMPORTANT: this is a MOUSE-EVENT-ONLY implementation. We do not request
// any framebuffer updates, never decode pixels, never hold a session open.
// CPU impact is microseconds per click. Connect → handshake → 2 PointerEvent
// packets (down, up) → close. That's the entire surface.
//
// Auth: supports RFB security types None (1) and VncAuth (2). VncAuth uses
// DES with the password truncated/padded to 8 bytes and each byte's bits
// reversed (legacy quirk preserved by every vncserver since RealVNC).
// The 16-byte challenge is encrypted as TWO consecutive 8-byte ECB blocks
// with the same key.
//
// RFB reference: https://datatracker.ietf.org/doc/html/rfc6143
// Tested-against versions: 3.3 and 3.7/3.8. RF2K-S has shipped non-standard
// vncservers (one observed "RFB 005.000"); we fall back to 3.8 for those.

internal sealed class Rf2kVncClient
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(3);
    private const int LeftButtonMask = 1;
    private const int NoButtonsMask = 0;

    private readonly ILogger _log;

    public Rf2kVncClient(ILogger log)
    {
        _log = log;
    }

    public async Task<string?> SendClickAsync(string host, int port, ushort x, ushort y, string? password, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var dialCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            dialCts.CancelAfter(ConnectTimeout);
            await client.ConnectAsync(host, port, dialCts.Token);

            using var stream = client.GetStream();
            stream.ReadTimeout = (int)ReadTimeout.TotalMilliseconds;
            stream.WriteTimeout = (int)ReadTimeout.TotalMilliseconds;

            var minor = await NegotiateProtocolVersionAsync(stream, ct);
            await NegotiateSecurityAsync(stream, minor, password ?? string.Empty, ct);
            await ClientInitAsync(stream, ct);
            await ConsumeServerInitAsync(stream, ct);

            // Press, brief hold, release. 50 ms is a common debounce-safe min.
            await SendPointerEventAsync(stream, LeftButtonMask, x, y, ct);
            await Task.Delay(50, ct);
            await SendPointerEventAsync(stream, NoButtonsMask, x, y, ct);

            await stream.FlushAsync(ct);
            _log.LogInformation("rf2k.vnc.click host={Host}:{Port} x={X} y={Y}", host, port, x, y);
            return null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "rf2k.vnc.click FAILED host={Host}:{Port} x={X} y={Y}", host, port, x, y);
            return ex.Message;
        }
    }

    private static async Task<int> NegotiateProtocolVersionAsync(NetworkStream stream, CancellationToken ct)
    {
        var serverVersion = await ReadExactlyAsync(stream, 12, ct);
        var versionStr = Encoding.ASCII.GetString(serverVersion).Trim();
        int minor = 8;
        if (versionStr.StartsWith("RFB ") && versionStr.Length >= 11)
        {
            if (int.TryParse(versionStr.AsSpan(8, 3), out var parsedMinor) &&
                int.TryParse(versionStr.AsSpan(4, 3), out var parsedMajor) &&
                parsedMajor == 3)
            {
                minor = parsedMinor switch { <= 3 => 3, < 7 => 3, 7 => 7, _ => 8 };
            }
        }

        var ourVersion = $"RFB 003.{minor:000}\n";
        var bytes = Encoding.ASCII.GetBytes(ourVersion);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
        return minor;
    }

    private static async Task NegotiateSecurityAsync(NetworkStream stream, int minor, string password, CancellationToken ct)
    {
        const byte SecNone = 1;
        const byte SecVncAuth = 2;
        bool havePassword = password.Length > 0;

        if (minor >= 7)
        {
            var countByte = await ReadExactlyAsync(stream, 1, ct);
            var count = countByte[0];
            if (count == 0)
            {
                var reason = await ReadFailureReasonAsync(stream, ct);
                throw new IOException($"VNC server rejected handshake: {reason}");
            }
            var types = await ReadExactlyAsync(stream, count, ct);

            byte chosen;
            if (havePassword && Array.IndexOf(types, SecVncAuth) >= 0)
                chosen = SecVncAuth;
            else if (Array.IndexOf(types, SecNone) >= 0)
                chosen = SecNone;
            else if (Array.IndexOf(types, SecVncAuth) >= 0)
                throw new NotSupportedException("VNC server requires a password (security type 2) — set VNC Password in the panel settings");
            else
                throw new NotSupportedException("VNC server does not offer 'None' (1) or 'VncAuth' (2) security — only those two are supported by Rf2kVncClient");

            await stream.WriteAsync(new byte[] { chosen }, ct);

            if (chosen == SecVncAuth)
            {
                await DoVncAuthAsync(stream, password, ct);
            }
        }
        else
        {
            var chosen = await ReadExactlyAsync(stream, 4, ct);
            var type = BinaryPrimitives.ReadUInt32BigEndian(chosen);
            if (type == 0)
            {
                var reason = await ReadFailureReasonAsync(stream, ct);
                throw new IOException($"VNC server rejected handshake: {reason}");
            }
            switch (type)
            {
                case SecNone:
                    return;
                case SecVncAuth:
                    if (!havePassword)
                        throw new NotSupportedException("VNC server requires a password (security type 2) — set VNC Password in the panel settings");
                    await DoVncAuthAsync(stream, password, ct);
                    break;
                default:
                    throw new NotSupportedException($"VNC server demanded security type {type} on RFB 3.3 — only None (1) and VncAuth (2) supported");
            }
        }

        var result = await ReadExactlyAsync(stream, 4, ct);
        var status = BinaryPrimitives.ReadUInt32BigEndian(result);
        if (status != 0)
        {
            string reason = "VNC server rejected security (likely wrong password)";
            if (minor >= 8)
                reason = await ReadFailureReasonAsync(stream, ct);
            throw new IOException($"VNC SecurityResult failure: {reason}");
        }
    }

    private static async Task DoVncAuthAsync(NetworkStream stream, string password, CancellationToken ct)
    {
        var challenge = await ReadExactlyAsync(stream, 16, ct);
        var key = DeriveVncDesKey(password);
        var response = DesEncryptEcb(key, challenge);
        await stream.WriteAsync(response, ct);
        await stream.FlushAsync(ct);
    }

    private static byte[] DeriveVncDesKey(string password)
    {
        var raw = Encoding.ASCII.GetBytes(password);
        var key = new byte[8];
        var copy = Math.Min(raw.Length, 8);
        Buffer.BlockCopy(raw, 0, key, 0, copy);
        for (var i = 0; i < 8; i++)
        {
            key[i] = ReverseBits(key[i]);
        }
        return key;
    }

    private static byte ReverseBits(byte b)
    {
        b = (byte)(((b & 0xF0) >> 4) | ((b & 0x0F) << 4));
        b = (byte)(((b & 0xCC) >> 2) | ((b & 0x33) << 2));
        b = (byte)(((b & 0xAA) >> 1) | ((b & 0x55) << 1));
        return b;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5351:Do not use broken cryptographic algorithms",
        Justification = "RFB VNC Authentication mandates DES — protocol-level requirement, not a security choice")]
    private static byte[] DesEncryptEcb(byte[] key, byte[] data)
    {
        using var des = DES.Create();
        des.Mode = CipherMode.ECB;
        des.Padding = PaddingMode.None;
        des.Key = key;
        using var enc = des.CreateEncryptor();
        return enc.TransformFinalBlock(data, 0, data.Length);
    }

    private static async Task ClientInitAsync(NetworkStream stream, CancellationToken ct)
    {
        await stream.WriteAsync(new byte[] { 1 }, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task ConsumeServerInitAsync(NetworkStream stream, CancellationToken ct)
    {
        var fixedPart = await ReadExactlyAsync(stream, 24, ct);
        var nameLen = BinaryPrimitives.ReadUInt32BigEndian(fixedPart.AsSpan(20, 4));
        if (nameLen > 0 && nameLen < 4096)
        {
            await ReadExactlyAsync(stream, (int)nameLen, ct);
        }
    }

    private static async Task SendPointerEventAsync(NetworkStream stream, int buttonMask, ushort x, ushort y, CancellationToken ct)
    {
        var packet = new byte[6];
        packet[0] = 5;
        packet[1] = (byte)(buttonMask & 0xFF);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), x);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4, 2), y);
        await stream.WriteAsync(packet, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task<string> ReadFailureReasonAsync(NetworkStream stream, CancellationToken ct)
    {
        try
        {
            var lenBytes = await ReadExactlyAsync(stream, 4, ct);
            var len = BinaryPrimitives.ReadUInt32BigEndian(lenBytes);
            if (len == 0 || len > 1024) return "(unknown)";
            var reason = await ReadExactlyAsync(stream, (int)len, ct);
            return Encoding.UTF8.GetString(reason);
        }
        catch
        {
            return "(unreadable)";
        }
    }

    private static async Task<byte[]> ReadExactlyAsync(NetworkStream stream, int count, CancellationToken ct)
    {
        var buf = new byte[count];
        var read = 0;
        while (read < count)
        {
            var got = await stream.ReadAsync(buf.AsMemory(read, count - read), ct);
            if (got == 0) throw new IOException($"VNC peer closed the connection (wanted {count} bytes, got {read})");
            read += got;
        }
        return buf;
    }
}

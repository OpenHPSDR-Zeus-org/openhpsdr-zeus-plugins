// SPDX-License-Identifier: GPL-2.0-or-later
//
// 4O3A Tuner Genius XL plugin for Openhpsdr-Zeus.
//
// Protocol (from official 4O3A API document):
//   Discovery: UDP broadcast on :9010 — "TunerGenius ip=... v=... serial=... nickname=..."
//   Control : TCP  on :9010
//     Commands:  C{seq}|{command}\n
//     Responses: R{seq}|{hex_result}|{message}
//     Status  :  S{seq}|status key1=val1 key2=val2 ... (space-delimited key=value)
//
// Key commands:
//   status               poll full status (use every ~100ms)
//   autotune             trigger an autotune cycle
//   bypass set=0|1       in-circuit / bypassed
//   operate set=0|1      operate / standby
//   activate ch=1|2      route Radio 1 or Radio 2 through the tuner
//
// Ported from Log4YM (TunerGeniusService + TunerGeniusConnection); SignalR
// push has been dropped in favour of polled REST so the plugin matches the
// Zeus sample-plugin pattern. The frontend polls /status; the backend keeps
// polling the device internally every 100 ms.

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Extensions;

namespace Openhpsdr.Zeus.Plugins.TunerGeniusXL;

public sealed class TgxlPlugin : IZeusPlugin, IBackendPlugin
{
    private const int DiscoveryPort = 9010;

    private readonly ConcurrentDictionary<string, TgxlConnection> _connections = new();
    private IPluginContext? _ctx;
    private CancellationTokenSource? _cts;
    private Task? _discoveryTask;

    public Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        _ctx = context;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        context.Logger.LogInformation(
            "TGXL plugin online; listening for UDP discovery on :{Port}", DiscoveryPort);

        _discoveryTask = Task.Run(() => RunDiscoveryListenerAsync(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    public async Task ShutdownAsync(CancellationToken ct)
    {
        _cts?.Cancel();
        try
        {
            if (_discoveryTask is { } t)
                await t.WaitAsync(ct);
        }
        catch (OperationCanceledException) { /* expected */ }
        catch (Exception ex)
        {
            _ctx?.Logger.LogWarning(ex, "TGXL discovery task did not stop cleanly");
        }

        foreach (var conn in _connections.Values)
            conn.Dispose();
        _connections.Clear();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("status", GetStatus);
        endpoints.MapPost("devices/{serial}/tune", TuneDevice);
        endpoints.MapPost("devices/{serial}/bypass", SetBypass);
        endpoints.MapPost("devices/{serial}/operate", SetOperate);
        endpoints.MapPost("devices/{serial}/activate", ActivateChannel);
    }

    // ── HTTP handlers ─────────────────────────────────────────────────────

    private IResult GetStatus()
    {
        var devices = _connections.Values.Select(c => c.GetStatus()).ToList();
        return Results.Ok(devices);
    }

    private async Task<IResult> TuneDevice(string serial)
    {
        if (!_connections.TryGetValue(serial, out var conn))
            return Results.NotFound(new { error = $"device {serial} not connected" });
        await conn.TuneAsync();
        return Results.Ok(conn.GetStatus());
    }

    private async Task<IResult> SetBypass(string serial, BypassRequest req)
    {
        if (!_connections.TryGetValue(serial, out var conn))
            return Results.NotFound(new { error = $"device {serial} not connected" });
        await conn.SetBypassAsync(req.Bypass);
        return Results.Ok(conn.GetStatus());
    }

    private async Task<IResult> SetOperate(string serial, OperateRequest req)
    {
        if (!_connections.TryGetValue(serial, out var conn))
            return Results.NotFound(new { error = $"device {serial} not connected" });
        await conn.SetOperateAsync(req.Operate);
        return Results.Ok(conn.GetStatus());
    }

    private async Task<IResult> ActivateChannel(string serial, ActivateRequest req)
    {
        if (req.Channel != 1 && req.Channel != 2)
            return Results.BadRequest(new { error = "channel must be 1 or 2" });
        if (!_connections.TryGetValue(serial, out var conn))
            return Results.NotFound(new { error = $"device {serial} not connected" });
        await conn.ActivateChannelAsync(req.Channel);
        return Results.Ok(conn.GetStatus());
    }

    // ── Discovery ─────────────────────────────────────────────────────────

    private async Task RunDiscoveryListenerAsync(CancellationToken ct)
    {
        try
        {
            using var udpClient = new UdpClient();
            udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var result = await udpClient.ReceiveAsync(ct);
                    var message = Encoding.ASCII.GetString(result.Buffer);

                    _ctx?.Logger.LogDebug("TGXL UDP: {Message}", message);

                    if (message.StartsWith("TunerGenius ", StringComparison.Ordinal))
                    {
                        var device = ParseDiscoveryMessage(message);
                        if (device != null)
                            HandleDeviceDiscovered(device, ct);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _ctx?.Logger.LogError(ex, "TGXL discovery listener error");
                    await Task.Delay(1000, ct);
                }
            }
        }
        catch (OperationCanceledException) { /* expected on shutdown */ }
        catch (Exception ex)
        {
            _ctx?.Logger.LogError(ex, "TGXL discovery listener failed to start");
        }
    }

    private DiscoveredDevice? ParseDiscoveryMessage(string message)
    {
        // Expected: TunerGenius ip=10.0.0.249 v=1.2.17 serial=241257-1 nickname=Tuner_Genius_XL
        try
        {
            var values = new Dictionary<string, string>();
            foreach (var part in message.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1))
            {
                var kv = part.Split('=', 2);
                if (kv.Length == 2)
                    values[kv[0]] = kv[1];
            }

            var ip = values.GetValueOrDefault("ip", "");
            if (string.IsNullOrEmpty(ip)) return null;

            var port = values.TryGetValue("port", out var ps) && int.TryParse(ps, out var p) ? p : 9010;

            return new DiscoveredDevice(
                IpAddress: ip,
                Port: port,
                Version: values.GetValueOrDefault("v", ""),
                Serial: values.GetValueOrDefault("serial", ""),
                Name: values.GetValueOrDefault("nickname", "Tuner Genius XL").Replace('_', ' '),
                Model: "TGXL"
            );
        }
        catch (Exception ex)
        {
            _ctx?.Logger.LogWarning(ex, "Failed to parse TGXL discovery message: {Message}", message);
            return null;
        }
    }

    private void HandleDeviceDiscovered(DiscoveredDevice device, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(device.Serial)) return;

        if (!_connections.TryGetValue(device.Serial, out var existing))
        {
            _ctx?.Logger.LogInformation(
                "Discovered TGXL: {Name} ({Serial}) at {Ip}:{Port} fw={Version}",
                device.Name, device.Serial, device.IpAddress, device.Port, device.Version);

            var logger = _ctx?.Logger;
            var conn = new TgxlConnection(device, logger);
            if (_connections.TryAdd(device.Serial, conn))
            {
                // Persist last-seen serial so the UI can show "previously seen" hints.
                _ = TryRememberSerialAsync(device.Serial, ct);
                _ = conn.RunAsync(ct);
            }
        }
        else
        {
            existing.UpdateDiscovery(device);
        }
    }

    private async Task TryRememberSerialAsync(string serial, CancellationToken ct)
    {
        try
        {
            if (_ctx?.Settings is { } s)
                await s.SetAsync("lastSeenSerial", serial, ct);
        }
        catch (Exception ex)
        {
            _ctx?.Logger.LogDebug(ex, "TGXL: could not persist lastSeenSerial");
        }
    }

    // ── Request DTOs ──────────────────────────────────────────────────────

    private sealed record BypassRequest(bool Bypass);
    private sealed record OperateRequest(bool Operate);
    private sealed record ActivateRequest(int Channel);
}

// ── Discovery DTO ─────────────────────────────────────────────────────────

internal sealed record DiscoveredDevice(
    string IpAddress,
    int Port,
    string Version,
    string Serial,
    string Name,
    string Model
);

// ── Status DTO surfaced via REST ──────────────────────────────────────────

public sealed record TgxlPortStatusDto
{
    [JsonPropertyName("portId")]         public int PortId { get; init; }
    [JsonPropertyName("auto")]           public bool Auto { get; init; }
    [JsonPropertyName("band")]           public string Band { get; init; } = "";
    [JsonPropertyName("frequencyMhz")]   public double FrequencyMhz { get; init; }
    [JsonPropertyName("swrX10")]         public int SwrX10 { get; init; }
    [JsonPropertyName("isTuning")]       public bool IsTuning { get; init; }
    [JsonPropertyName("isTransmitting")] public bool IsTransmitting { get; init; }
    [JsonPropertyName("tuneResult")]     public string TuneResult { get; init; } = "";
}

public sealed record TgxlStatusDto
{
    [JsonPropertyName("deviceSerial")]      public string DeviceSerial { get; init; } = "";
    [JsonPropertyName("deviceName")]        public string DeviceName { get; init; } = "";
    [JsonPropertyName("ipAddress")]         public string IpAddress { get; init; } = "";
    [JsonPropertyName("version")]           public string Version { get; init; } = "";
    [JsonPropertyName("model")]             public string Model { get; init; } = "";
    [JsonPropertyName("isConnected")]       public bool IsConnected { get; init; }
    [JsonPropertyName("isOperating")]       public bool IsOperating { get; init; }
    [JsonPropertyName("isBypassed")]        public bool IsBypassed { get; init; }
    [JsonPropertyName("isTuning")]          public bool IsTuning { get; init; }
    [JsonPropertyName("activeRadio")]       public int ActiveRadio { get; init; }
    [JsonPropertyName("forwardPowerWatts")] public double ForwardPowerWatts { get; init; }
    [JsonPropertyName("swr")]               public double Swr { get; init; }
    [JsonPropertyName("l")]                 public int L { get; init; }
    [JsonPropertyName("c1")]                public int C1 { get; init; }
    [JsonPropertyName("c2")]                public int C2 { get; init; }
    [JsonPropertyName("freqAMhz")]          public double FreqAMhz { get; init; }
    [JsonPropertyName("freqBMhz")]          public double FreqBMhz { get; init; }
    [JsonPropertyName("portA")]             public TgxlPortStatusDto? PortA { get; init; }
    [JsonPropertyName("portB")]             public TgxlPortStatusDto? PortB { get; init; }
}

// ── Connection class ──────────────────────────────────────────────────────

internal sealed class TgxlConnection : IDisposable
{
    private readonly ILogger? _logger;
    private DiscoveredDevice _device;

    private TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private int _seq;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    // Parsed state
    private bool _isOperating;
    private bool _isBypassed;
    private bool _isTuning;
    private int _activeRadio = 1;
    private double _forwardPowerWatts;
    private double _swr = 1.0;
    private int _L, _C1, _C2;
    private double _freqA, _freqB;
    private int _bandA, _bandB;
    private bool _pttA, _pttB;
    private string _version = "";

    public bool IsConnected => _tcpClient?.Connected ?? false;

    public TgxlConnection(DiscoveredDevice device, ILogger? logger)
    {
        _device = device;
        _logger = logger;
    }

    public void UpdateDiscovery(DiscoveredDevice device) => _device = device;

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _logger?.LogInformation("TGXL connecting to {Ip}:{Port}...", _device.IpAddress, _device.Port);
                _tcpClient = new TcpClient { NoDelay = true };
                _tcpClient.ReceiveTimeout = 10000;
                await _tcpClient.ConnectAsync(_device.IpAddress, _device.Port, ct);
                _stream = _tcpClient.GetStream();

                // Read version banner: "V1.2.17 TG" (or "V1.2.17 TG AUTH" on WAN)
                using var bannerReader = new StreamReader(_stream, Encoding.ASCII, leaveOpen: true);
                var banner = await bannerReader.ReadLineAsync(ct);
                if (banner != null && banner.StartsWith("V", StringComparison.Ordinal))
                {
                    _version = banner.Split(' ')[0][1..];
                    _logger?.LogInformation("TGXL connected: {Name} fw={Version}", _device.Name, _version);
                }

                await PollLoopAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "TGXL connection error ({Name})", _device.Name);
            }

            Disconnect();

            if (!ct.IsCancellationRequested)
            {
                _logger?.LogInformation("TGXL reconnecting to {Name} in 5s...", _device.Name);
                try { await Task.Delay(5000, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>
    /// Poll status every 100ms — TGXL does not push unsolicited status.
    /// </summary>
    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && IsConnected)
        {
            try
            {
                var response = await SendRawAsync("status", ct);
                if (response != null)
                    ParseStatus(response);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "TGXL poll error");
                break;
            }

            try { await Task.Delay(100, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Send one C{seq}|cmd\n and read a single-line response.
    /// </summary>
    private async Task<string?> SendRawAsync(string command, CancellationToken ct = default)
    {
        if (_stream == null) return null;

        await _sendLock.WaitAsync(ct);
        try
        {
            var n = (Interlocked.Increment(ref _seq) % 255) + 1;
            var line = $"C{n}|{command}\n";
            var bytes = Encoding.ASCII.GetBytes(line);

            await _stream.WriteAsync(bytes, ct);
            await _stream.FlushAsync(ct);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(2000);

            var buffer = new byte[4096];
            var sb = new StringBuilder();

            while (!cts.IsCancellationRequested)
            {
                var bytesRead = await _stream.ReadAsync(buffer, cts.Token);
                if (bytesRead == 0) break;
                sb.Append(Encoding.ASCII.GetString(buffer, 0, bytesRead));

                var text = sb.ToString();
                var newline = text.IndexOfAny(new[] { '\n', '\r' });
                if (newline >= 0)
                    return text[..newline].Trim();
            }

            return sb.Length > 0 ? sb.ToString().Trim() : null;
        }
        catch (OperationCanceledException)
        {
            _logger?.LogDebug("TGXL command '{Command}' timed out", command);
            return null;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    // ── Status parsing ────────────────────────────────────────────────────

    private void ParseStatus(string response)
    {
        var statusPart = response;
        var pipeIdx = response.IndexOf('|');
        if (pipeIdx >= 0)
            statusPart = response[(pipeIdx + 1)..];

        if (!statusPart.StartsWith("status", StringComparison.OrdinalIgnoreCase))
        {
            _logger?.LogDebug("TGXL ignoring non-status response: {Line}", response);
            return;
        }

        var kvStr = statusPart.Length > 7 ? statusPart[7..] : "";
        var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in kvStr.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = token.IndexOf('=');
            if (eq > 0)
                kv[token[..eq]] = eq < token.Length - 1 ? token[(eq + 1)..] : "";
        }

        try
        {
            if (kv.TryGetValue("fwd", out var fwdStr) &&
                double.TryParse(fwdStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var fwdDbm))
            {
                var watts = Math.Pow(10, fwdDbm / 10.0) / 1000.0;
                _forwardPowerWatts = watts >= 1.0 ? Math.Round(watts, 1) : 0;
            }

            if (kv.TryGetValue("swr", out var swrStr) &&
                double.TryParse(swrStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var rawRl))
            {
                var absRl = Math.Abs(rawRl);
                var gamma = Math.Pow(10, -absRl / 20.0);
                var swrRaw = (1 + gamma) / (1 - gamma);
                _swr = double.IsInfinity(swrRaw) || double.IsNaN(swrRaw) ? 99.9 : Math.Round(swrRaw, 2);
            }

            if (kv.TryGetValue("freqA", out var freqAStr) &&
                double.TryParse(freqAStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var freqAKhz))
                _freqA = Math.Round(freqAKhz / 1000.0, 3);

            if (kv.TryGetValue("freqB", out var freqBStr) &&
                double.TryParse(freqBStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var freqBKhz))
                _freqB = Math.Round(freqBKhz / 1000.0, 3);

            if (kv.TryGetValue("bandA", out var bandAStr) && int.TryParse(bandAStr, out var bandAVal))
                _bandA = bandAVal;
            if (kv.TryGetValue("bandB", out var bandBStr) && int.TryParse(bandBStr, out var bandBVal))
                _bandB = bandBVal;

            if (kv.TryGetValue("pttA", out var pttAStr)) _pttA = pttAStr == "1";
            if (kv.TryGetValue("pttB", out var pttBStr)) _pttB = pttBStr == "1";

            if (kv.TryGetValue("state", out var stateStr)) _isOperating = stateStr == "1";
            if (kv.TryGetValue("active", out var activeStr) && int.TryParse(activeStr, out var ar)) _activeRadio = ar;
            if (kv.TryGetValue("tuning", out var tuningStr)) _isTuning = tuningStr == "1";
            if (kv.TryGetValue("bypass", out var bypassStr)) _isBypassed = bypassStr == "1";

            if (kv.TryGetValue("relayL", out var lStr) && int.TryParse(lStr, out var lVal)) _L = lVal;
            if (kv.TryGetValue("relayC1", out var c1Str) && int.TryParse(c1Str, out var c1Val)) _C1 = c1Val;
            if (kv.TryGetValue("relayC2", out var c2Str) && int.TryParse(c2Str, out var c2Val)) _C2 = c2Val;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "TGXL status parse error on: {Line}", response);
        }
    }

    // ── Commands ──────────────────────────────────────────────────────────

    public async Task TuneAsync()
    {
        var r = await SendRawAsync("autotune");
        _logger?.LogInformation("TGXL autotune triggered, response: {R}", r);
    }

    public async Task SetBypassAsync(bool bypass)
    {
        var cmd = bypass ? "bypass set=1" : "bypass set=0";
        var r = await SendRawAsync(cmd);
        _logger?.LogInformation("TGXL bypass set={Val}, response: {R}", bypass ? 1 : 0, r);
    }

    public async Task SetOperateAsync(bool operate)
    {
        var cmd = operate ? "operate set=1" : "operate set=0";
        var r = await SendRawAsync(cmd);
        _logger?.LogInformation("TGXL operate set={Val}, response: {R}", operate ? 1 : 0, r);
    }

    public async Task ActivateChannelAsync(int channel)
    {
        if (channel != 1 && channel != 2)
        {
            _logger?.LogWarning("TGXL ActivateChannel: invalid channel {Ch}", channel);
            return;
        }
        var r = await SendRawAsync($"activate ch={channel}");
        _logger?.LogInformation("TGXL activate ch={Ch}, response: {R}", channel, r);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    public TgxlStatusDto GetStatus()
    {
        var swrX10 = (int)Math.Round(_swr * 10);
        var tuneResult = _swr <= 2.0 ? "OK" : "HighSWR";

        var portA = new TgxlPortStatusDto
        {
            PortId = 1,
            Auto = !_isBypassed,
            Band = GetBandForPort(_freqA, _bandA),
            FrequencyMhz = _freqA,
            SwrX10 = swrX10,
            IsTuning = _isTuning,
            IsTransmitting = _pttA,
            TuneResult = tuneResult,
        };

        TgxlPortStatusDto? portB = (_freqB > 0 || _bandB > 0)
            ? new TgxlPortStatusDto
            {
                PortId = 2,
                Auto = !_isBypassed,
                Band = GetBandForPort(_freqB, _bandB),
                FrequencyMhz = _freqB,
                SwrX10 = swrX10,
                IsTuning = _isTuning,
                IsTransmitting = _pttB,
                TuneResult = tuneResult,
            }
            : null;

        return new TgxlStatusDto
        {
            DeviceSerial = _device.Serial,
            DeviceName = _device.Name,
            IpAddress = _device.IpAddress,
            Version = _version,
            Model = _device.Model,
            IsConnected = IsConnected,
            IsOperating = _isOperating,
            IsBypassed = _isBypassed,
            IsTuning = _isTuning,
            ActiveRadio = _activeRadio,
            ForwardPowerWatts = _forwardPowerWatts,
            Swr = _swr,
            L = _L,
            C1 = _C1,
            C2 = _C2,
            FreqAMhz = _freqA,
            FreqBMhz = _freqB,
            PortA = portA,
            PortB = portB,
        };
    }

    private void Disconnect()
    {
        try { _stream?.Close(); } catch { }
        try { _tcpClient?.Close(); } catch { }
        _stream = null;
        _tcpClient = null;
    }

    public void Dispose()
    {
        Disconnect();
        _sendLock.Dispose();
    }

    private static string GetBandForPort(double freqMhz, int bandNum)
        => freqMhz > 0 ? FreqToBand(freqMhz) : BandNumToString(bandNum);

    private static string FreqToBand(double mhz) => mhz switch
    {
        >= 1.8   and < 2.0    => "160m",
        >= 3.5   and < 4.0    => "80m",
        >= 5.3   and < 5.4    => "60m",
        >= 7.0   and < 7.3    => "40m",
        >= 10.1  and < 10.15  => "30m",
        >= 14.0  and < 14.35  => "20m",
        >= 18.068 and < 18.168 => "17m",
        >= 21.0  and < 21.45  => "15m",
        >= 24.89 and < 24.99  => "12m",
        >= 28.0  and < 29.7   => "10m",
        >= 50.0  and < 54.0   => "6m",
        _ => mhz > 0 ? mhz.ToString("F3", CultureInfo.InvariantCulture) : "None"
    };

    private static string BandNumToString(int band) => band switch
    {
        160 => "160m",
        80  => "80m",
        60  => "60m",
        40  => "40m",
        30  => "30m",
        20  => "20m",
        17  => "17m",
        15  => "15m",
        12  => "12m",
        10  => "10m",
        6   => "6m",
        2   => "2m",
        0   => "None",
        _   => $"{band}m"
    };
}

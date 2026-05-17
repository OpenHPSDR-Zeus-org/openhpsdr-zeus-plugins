// SPDX-License-Identifier: GPL-2.0-or-later
//
// Power Genius XL plugin for Openhpsdr-Zeus.
//
// Discovers 4O3A Power Genius XL amplifiers on UDP :9008 and opens a TCP
// control connection on :9008. The wire protocol is ASCII line-based:
//   send:    "C<seq>|<command>\r\n"
//   reply:   "R<seq>|<hex>|<message>"
//   async:   "S0|<message>"  or  "S|<message>"
//
// Ported from Log4YM's PgxlService + PgxlConnection. SignalR push has been
// replaced with REST poll endpoints under /api/plugins/com.openhpsdr.zeus.plugins.pgxl/.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Extensions;

namespace Openhpsdr.Zeus.Plugins.PowerGeniusXL;

public sealed class PgxlPlugin : IZeusPlugin, IBackendPlugin
{
    private const int DiscoveryPort = 9008;
    private const int ControlPort = 9008;
    private const int KeepAliveIntervalMs = 30_000;

    private readonly ConcurrentDictionary<string, PgxlConnection> _connections = new();
    private CancellationTokenSource? _cts;
    private Task? _discoveryTask;
    private Task? _keepAliveTask;
    private IPluginContext? _ctx;

    private ILogger Log =>
        _ctx?.Logger ?? throw new InvalidOperationException("PgxlPlugin used before InitializeAsync.");

    // ------------------------------------------------------------------
    // IZeusPlugin
    // ------------------------------------------------------------------

    public Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        _ctx = context;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        Log.LogInformation("Power Genius XL plugin starting — UDP discovery on :{Port}", DiscoveryPort);

        _discoveryTask = Task.Run(() => RunDiscoveryListenerAsync(_cts.Token));
        _keepAliveTask = Task.Run(() => RunKeepAliveAsync(_cts.Token));

        return Task.CompletedTask;
    }

    public async Task ShutdownAsync(CancellationToken ct)
    {
        if (_cts != null)
        {
            try { _cts.Cancel(); } catch { /* ignore */ }
        }

        foreach (var c in _connections.Values)
        {
            try { c.Disconnect(); } catch { /* ignore */ }
        }

        var tasks = new List<Task>(2);
        if (_discoveryTask != null) tasks.Add(_discoveryTask);
        if (_keepAliveTask != null) tasks.Add(_keepAliveTask);

        try
        {
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(3), ct);
        }
        catch
        {
            // Best-effort; host will tear down the AssemblyLoadContext anyway.
        }
    }

    // ------------------------------------------------------------------
    // IBackendPlugin — REST surface
    // ------------------------------------------------------------------

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("status", GetAllStatus);
        endpoints.MapGet("devices/{serial}", GetDeviceStatus);
        endpoints.MapPost("devices/{serial}/operate", SetOperate);
        endpoints.MapPost("devices/{serial}/flexradio/disable", DisableFlexRadioPairing);
    }

    private IResult GetAllStatus()
    {
        var devices = _connections.Values
            .Select(c => c.GetStatus())
            .Where(s => s != null)
            .ToArray();
        return Results.Ok(devices);
    }

    private IResult GetDeviceStatus(string serial)
    {
        if (_connections.TryGetValue(serial, out var conn))
        {
            var status = conn.GetStatus();
            return status != null ? Results.Ok(status) : Results.NotFound();
        }
        return Results.NotFound();
    }

    private async Task<IResult> SetOperate(string serial, OperateRequest req)
    {
        if (!_connections.TryGetValue(serial, out var conn))
            return Results.NotFound();

        if (req.Operate)
            await conn.SetOperateAsync();
        else
            await conn.SetStandbyAsync();

        return Results.Ok(conn.GetStatus());
    }

    private async Task<IResult> DisableFlexRadioPairing(string serial, FlexRadioDisableRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Slice))
            return Results.BadRequest(new { error = "slice is required (A or B)" });

        var slice = req.Slice.Trim().ToUpperInvariant();
        if (slice != "A" && slice != "B")
            return Results.BadRequest(new { error = "slice must be 'A' or 'B'" });

        if (!_connections.TryGetValue(serial, out var conn))
            return Results.NotFound();

        await conn.DisableFlexRadioPairingAsync(slice);
        return Results.Ok(new { serial, slice, action = "flexradio.disabled" });
    }

    // ------------------------------------------------------------------
    // Discovery + keepalive
    // ------------------------------------------------------------------

    private async Task RunDiscoveryListenerAsync(CancellationToken ct)
    {
        UdpClient? udpClient = null;
        try
        {
            udpClient = new UdpClient();
            udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
            Log.LogInformation("Listening for PGXL discovery on UDP :{Port}", DiscoveryPort);
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Failed to bind PGXL discovery socket on UDP :{Port}", DiscoveryPort);
            udpClient?.Dispose();
            return;
        }

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var result = await udpClient.ReceiveAsync(ct);
                    var message = Encoding.ASCII.GetString(result.Buffer);

                    if (message.StartsWith("PGXL ", StringComparison.Ordinal)
                        || message.Contains("PowerGenius", StringComparison.Ordinal)
                        || message.Contains("model=PowerGeniusXL", StringComparison.Ordinal))
                    {
                        var device = ParseDiscoveryMessage(message, result.RemoteEndPoint);
                        if (device != null)
                            await HandleDeviceDiscoveredAsync(device, ct);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Log.LogError(ex, "PGXL discovery listener error");
                    try { await Task.Delay(1000, ct); } catch { break; }
                }
            }
        }
        finally
        {
            udpClient.Dispose();
        }
    }

    private async Task RunKeepAliveAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(KeepAliveIntervalMs, ct);
                foreach (var conn in _connections.Values)
                {
                    if (conn.IsConnected)
                        _ = conn.SendPingAsync();
                }
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private PgxlDiscoveredEvent? ParseDiscoveryMessage(string message, IPEndPoint remoteEndPoint)
    {
        try
        {
            Log.LogInformation("PGXL discovery packet: {Message}", message);

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in Regex.Matches(message, @"(\w+)=([^\s]+)"))
            {
                values[match.Groups[1].Value] = match.Groups[2].Value;
            }

            var ip = values.GetValueOrDefault("ip", remoteEndPoint.Address.ToString());
            var serial = values.GetValueOrDefault("serial_num")
                         ?? values.GetValueOrDefault("serial", "")
                         ?? "";
            var model = values.GetValueOrDefault("model", "PowerGeniusXL");

            if (string.IsNullOrEmpty(serial))
                serial = $"pgxl-{ip.Replace('.', '-')}";

            return new PgxlDiscoveredEvent(ip, ControlPort, serial, model);
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Failed to parse PGXL discovery message: {Message}", message);
            return null;
        }
    }

    private Task HandleDeviceDiscoveredAsync(PgxlDiscoveredEvent device, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(device.Serial))
            return Task.CompletedTask;

        if (!_connections.TryGetValue(device.Serial, out var connection))
        {
            Log.LogInformation("Discovered PGXL: {Model} ({Serial}) at {Ip}:{Port}",
                device.Model, device.Serial, device.IpAddress, device.Port);

            connection = new PgxlConnection(device, Log);
            if (_connections.TryAdd(device.Serial, connection))
                _ = connection.ConnectAsync(ct);
        }
        else
        {
            connection.UpdateDiscovery(device);
        }
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------
    // DTOs (wire-format records exposed through the REST endpoints)
    // ------------------------------------------------------------------

    public sealed record OperateRequest([property: JsonPropertyName("operate")] bool Operate);

    public sealed record FlexRadioDisableRequest([property: JsonPropertyName("slice")] string Slice);

    public sealed record PgxlDiscoveredEvent(string IpAddress, int Port, string Serial, string Model);

    public sealed record PgxlMeters(
        [property: JsonPropertyName("forwardPowerDbm")]   double ForwardPowerDbm,
        [property: JsonPropertyName("forwardPowerWatts")] double ForwardPowerWatts,
        [property: JsonPropertyName("returnLossDb")]      double ReturnLossDb,
        [property: JsonPropertyName("swrRatio")]          double SwrRatio,
        [property: JsonPropertyName("drivePowerDbm")]     double DrivePowerDbm,
        [property: JsonPropertyName("paCurrent")]         double PaCurrent,
        [property: JsonPropertyName("temperatureC")]      double TemperatureC);

    public sealed record PgxlSetup(
        [property: JsonPropertyName("bandSource")]        string BandSource,
        [property: JsonPropertyName("selectedAntenna")]   int SelectedAntenna,
        [property: JsonPropertyName("attenuatorEnabled")] bool AttenuatorEnabled,
        [property: JsonPropertyName("biasOffset")]        int BiasOffset,
        [property: JsonPropertyName("pttDelay")]          int PttDelay,
        [property: JsonPropertyName("keyDelay")]          int KeyDelay,
        [property: JsonPropertyName("highSwr")]           bool HighSwr,
        [property: JsonPropertyName("overTemp")]          bool OverTemp,
        [property: JsonPropertyName("overCurrent")]       bool OverCurrent);

    public sealed record PgxlStatusDto(
        [property: JsonPropertyName("serial")]          string Serial,
        [property: JsonPropertyName("ipAddress")]       string IpAddress,
        [property: JsonPropertyName("isConnected")]     bool IsConnected,
        [property: JsonPropertyName("isOperating")]     bool IsOperating,
        [property: JsonPropertyName("isTransmitting")]  bool IsTransmitting,
        [property: JsonPropertyName("band")]            string Band,
        [property: JsonPropertyName("biasA")]           string BiasA,
        [property: JsonPropertyName("biasB")]           string BiasB,
        [property: JsonPropertyName("firmwareVersion")] string FirmwareVersion,
        [property: JsonPropertyName("meters")]          PgxlMeters Meters,
        [property: JsonPropertyName("setup")]           PgxlSetup Setup);
}

// ----------------------------------------------------------------------
// PgxlConnection — single-device TCP control + status poll
// ----------------------------------------------------------------------

internal sealed class PgxlConnection
{
    private const string Terminator = "\r\n";

    private readonly ILogger _log;
    private PgxlPlugin.PgxlDiscoveredEvent _device;

    private TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private StreamReader? _reader;
    private int _sequenceNumber;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<string>> _pendingCommands = new();

    public string Serial => _device.Serial;
    public string IpAddress => _device.IpAddress;
    public bool IsConnected => _tcpClient?.Connected ?? false;
    public bool IsOperating { get; private set; }
    public bool IsTransmitting { get; private set; }
    public string Band { get; private set; } = "Unknown";
    public string BiasA { get; private set; } = "";
    public string BiasB { get; private set; } = "";
    public string FirmwareVersion { get; private set; } = "";

    private double _forwardPowerDbm;
    private double _returnLossDb;
    private double _drivePowerDbm;
    private double _paCurrent;
    private double _temperatureC;

    private string _bandSource = "ACC";
    private int _selectedAntenna = 1;
    // Setup fields below are placeholders surfaced through the REST DTO; the
    // PGXL 'setup read' parse path doesn't fill them yet (matches Log4YM).
#pragma warning disable CS0649
    private bool _attenuatorEnabled;
    private int _biasOffset;
    private int _pttDelay;
    private int _keyDelay;
#pragma warning restore CS0649
    private bool _highSwr;
    private bool _overTemp;
    private bool _overCurrent;
    private string _nickname = "";

    public PgxlConnection(PgxlPlugin.PgxlDiscoveredEvent device, ILogger log)
    {
        _device = device;
        _log = log;
    }

    public void UpdateDiscovery(PgxlPlugin.PgxlDiscoveredEvent device) => _device = device;

    public async Task ConnectAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _log.LogInformation("Connecting to PGXL {Serial} at {Ip}:{Port}...",
                    _device.Serial, _device.IpAddress, _device.Port);

                _tcpClient = new TcpClient();
                await _tcpClient.ConnectAsync(_device.IpAddress, _device.Port, ct);
                _stream = _tcpClient.GetStream();
                _reader = new StreamReader(_stream, Encoding.ASCII);

                _log.LogInformation("Connected to PGXL at {Ip}:{Port}", _device.IpAddress, _device.Port);

                await ReadPrologueAsync(ct);

                var receiveTask = Task.Run(() => ReceiveLoopAsync(ct), ct);
                await Task.Delay(500, ct);

                await InitializeAsync(ct);
                await StatusPollingLoopAsync(ct);

                await receiveTask;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "PGXL connection error to {Serial}", _device.Serial);
            }

            Disconnect();

            if (!ct.IsCancellationRequested)
            {
                _log.LogInformation("Reconnecting to PGXL {Serial} in 5 seconds...", _device.Serial);
                try { await Task.Delay(5000, ct); } catch { break; }
            }
        }
    }

    private async Task ReadPrologueAsync(CancellationToken ct)
    {
        var prologue = await _reader!.ReadLineAsync(ct);
        _log.LogInformation("PGXL prologue: '{Prologue}'", prologue);

        if (prologue != null && prologue.StartsWith('V'))
        {
            var parts = prologue.Split(' ');
            FirmwareVersion = parts[0][1..];
            _log.LogInformation("PGXL {Serial} firmware version: {Version}", _device.Serial, FirmwareVersion);
        }
    }

    private async Task InitializeAsync(CancellationToken ct)
    {
        var pingResponse = await SendCommandAsync("ping", ct);
        _log.LogInformation("PGXL ping response: '{Response}'", pingResponse);

        var setupResponse = await SendCommandAsync("setup read", ct);
        _log.LogInformation("PGXL setup response: '{Response}'", setupResponse);
        ParseSetupResponse(setupResponse);

        var statusResponse = await SendCommandAsync("status", ct);
        _log.LogInformation("PGXL status response: '{Response}'", statusResponse);
        ParseStatusResponse(statusResponse);

        var flexConfigA = await SendCommandAsync("flexradio read=A", ct);
        _log.LogInformation("PGXL FlexRadio config A: '{Response}'", flexConfigA);
        var flexConfigB = await SendCommandAsync("flexradio read=B", ct);
        _log.LogInformation("PGXL FlexRadio config B: '{Response}'", flexConfigB);

        _log.LogInformation("PGXL {Serial} initialized: Operating={Operating}, Band={Band}, Nickname={Nickname}",
            _device.Serial, IsOperating, Band, _nickname);
    }

    private async Task StatusPollingLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && IsConnected)
        {
            try
            {
                await Task.Delay(500, ct);
                var statusResponse = await SendCommandAsync("status", ct);
                ParseStatusResponse(statusResponse);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Error polling PGXL status");
                break;
            }
        }
    }

    private async Task<string> SendCommandAsync(string command, CancellationToken ct = default)
    {
        await _sendLock.WaitAsync(ct);
        try
        {
            var seq = Interlocked.Increment(ref _sequenceNumber) % 256;
            if (seq == 0) seq = 1;

            var tcs = new TaskCompletionSource<string>();
            _pendingCommands[seq] = tcs;

            var commandLine = $"C{seq}|{command}{Terminator}";
            var bytes = Encoding.ASCII.GetBytes(commandLine);

            await _stream!.WriteAsync(bytes, ct);
            await _stream.FlushAsync(ct);

            _log.LogInformation("PGXL sent: {Command}", commandLine.TrimEnd());

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(5000);

            try
            {
                return await tcs.Task.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                _log.LogWarning("PGXL command timeout: {Command}", command);
                return "";
            }
            finally
            {
                _pendingCommands.TryRemove(seq, out _);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        _log.LogInformation("PGXL receive loop starting for {Serial}", _device.Serial);

        while (!ct.IsCancellationRequested && _tcpClient?.Connected == true)
        {
            try
            {
                var line = await _reader!.ReadLineAsync(ct);
                if (line == null)
                {
                    _log.LogWarning("PGXL connection closed");
                    break;
                }
                if (!string.IsNullOrWhiteSpace(line))
                    ProcessLine(line);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "PGXL receive error");
                break;
            }
        }
        _log.LogInformation("PGXL receive loop ended for {Serial}", _device.Serial);
    }

    private void ProcessLine(string line)
    {
        _log.LogDebug("PGXL recv: {Line}", line);

        if (line.StartsWith('R'))
        {
            var parts = line.Split('|', 3);
            if (parts.Length >= 2)
            {
                var seqStr = parts[0][1..];
                if (int.TryParse(seqStr, out var seq))
                {
                    var message = parts.Length > 2 ? parts[2] : "";
                    if (_pendingCommands.TryGetValue(seq, out var tcs))
                        tcs.TrySetResult(message);
                }
            }
        }
        else if (line.StartsWith("S0|", StringComparison.Ordinal)
                 || line.StartsWith("S|", StringComparison.Ordinal))
        {
            var message = line.Contains('|') ? line[(line.IndexOf('|') + 1)..] : line;
            ProcessAsyncStatus(message);
        }
    }

    private void ProcessAsyncStatus(string message)
    {
        _log.LogDebug("PGXL async status: {Message}", message);

        var stateMatch = Regex.Match(message, @"state=(\w+)");
        if (stateMatch.Success)
        {
            var state = stateMatch.Groups[1].Value;
            var wasOperating = IsOperating;

            IsOperating = !state.Equals("STANDBY", StringComparison.OrdinalIgnoreCase)
                       && !state.Equals("FAULT", StringComparison.OrdinalIgnoreCase);
            IsTransmitting = state.StartsWith("TRANSMIT", StringComparison.OrdinalIgnoreCase);

            if (wasOperating != IsOperating)
                _log.LogInformation("PGXL state={State}, IsOperating changed to {IsOperating}", state, IsOperating);

            if (state.Equals("FAULT", StringComparison.OrdinalIgnoreCase))
                _log.LogWarning("PGXL entered FAULT state");
        }
    }

    private void ParseStatusResponse(string response)
    {
        if (string.IsNullOrEmpty(response)) return;

        var values = ParseKeyValuePairs(response);

        // state=STANDBY → not operating; IDLE → operate, not TX; TRANSMIT_* → TX; FAULT → fault.
        // Note: vdd is only non-zero during active TX so it can't be used as operate detector.
        if (values.TryGetValue("state", out var state))
        {
            IsOperating = !state.Equals("STANDBY", StringComparison.OrdinalIgnoreCase)
                       && !state.Equals("FAULT", StringComparison.OrdinalIgnoreCase);
            IsTransmitting = state.StartsWith("TRANSMIT", StringComparison.OrdinalIgnoreCase);
        }

        if (values.TryGetValue("bandA", out var bandA))
            Band = FormatBand(bandA);

        if (values.TryGetValue("biasA", out var biasA))
            BiasA = FormatBiasMode(biasA);
        if (values.TryGetValue("biasB", out var biasB))
            BiasB = FormatBiasMode(biasB);

        if (values.TryGetValue("fwd", out var fwd) && double.TryParse(fwd, out var fwdVal))
            _forwardPowerDbm = fwdVal;
        // PGXL sends return loss in a field named "swr" (negative dB; -60 = idle)
        if (values.TryGetValue("swr", out var swrRl) && double.TryParse(swrRl, out var swrRlVal))
            _returnLossDb = Math.Abs(swrRlVal);
        if (values.TryGetValue("drv", out var drv) && double.TryParse(drv, out var drvVal))
            _drivePowerDbm = drvVal;
        if (values.TryGetValue("id", out var id) && double.TryParse(id, out var idVal))
            _paCurrent = idVal;
        if (values.TryGetValue("temp", out var temp) && double.TryParse(temp, out var tempVal))
            _temperatureC = tempVal;

        if (values.TryGetValue("meffa", out var meffaFaults))
        {
            _highSwr = meffaFaults.Contains("SWR", StringComparison.OrdinalIgnoreCase);
            _overTemp = meffaFaults.Contains("TEMP", StringComparison.OrdinalIgnoreCase);
            _overCurrent = meffaFaults.Contains("CURRENT", StringComparison.OrdinalIgnoreCase);
        }
    }

    private void ParseSetupResponse(string response)
    {
        if (string.IsNullOrEmpty(response)) return;

        var values = ParseKeyValuePairs(response);
        if (values.TryGetValue("nickname", out var nickname))
            _nickname = nickname.Replace('_', ' ');
    }

    private static Dictionary<string, string> ParseKeyValuePairs(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(text, @"(\w+)=([^\s]+)"))
        {
            values[match.Groups[1].Value] = match.Groups[2].Value;
        }
        return values;
    }

    private static string FormatBand(string bandCode) => bandCode switch
    {
        "0"   => "N/A",
        "6"   => "6m",
        "10"  => "10m",
        "12"  => "12m",
        "15"  => "15m",
        "17"  => "17m",
        "20"  => "20m",
        "30"  => "30m",
        "40"  => "40m",
        "60"  => "60m",
        "80"  => "80m",
        "160" => "160m",
        _     => $"{bandCode}m",
    };

    /// <summary>
    /// PGXL bias values look like "RADIO_AB", "AUTO_AAB", "MANUAL_A"; the
    /// part after the last underscore is the visible mode label.
    /// </summary>
    private static string FormatBiasMode(string biasValue)
    {
        if (string.IsNullOrEmpty(biasValue)) return "";
        var underscoreIndex = biasValue.LastIndexOf('_');
        if (underscoreIndex >= 0 && underscoreIndex < biasValue.Length - 1)
            return biasValue[(underscoreIndex + 1)..];
        return biasValue;
    }

    public async Task SetOperateAsync()
    {
        _log.LogInformation("Setting PGXL {Serial} to OPERATE", _device.Serial);
        var response = await SendCommandAsync("operate=1");
        _log.LogInformation("PGXL operate response: '{Response}'", response);
    }

    public async Task SetStandbyAsync()
    {
        _log.LogInformation("Setting PGXL {Serial} to STANDBY", _device.Serial);
        var response = await SendCommandAsync("operate=0");
        _log.LogInformation("PGXL standby response: '{Response}'", response);
    }

    public async Task SendPingAsync()
    {
        try { await SendCommandAsync("ping"); }
        catch (Exception ex) { _log.LogDebug(ex, "PGXL ping failed"); }
    }

    public async Task DisableFlexRadioPairingAsync(string slice)
    {
        _log.LogInformation("Disabling FlexRadio pairing for PGXL slice {Slice}", slice);
        var response = await SendCommandAsync($"flexradio ampslice={slice.ToUpperInvariant()} active=0");
        _log.LogInformation("PGXL disable FlexRadio response: {Response}", response);
    }

    public PgxlPlugin.PgxlStatusDto? GetStatus()
    {
        // Return loss is meaningless when not transmitting (PGXL idles at -60 dB).
        var swrRatio = IsTransmitting ? ReturnLossToSwr(_returnLossDb) : 0;

        var meters = new PgxlPlugin.PgxlMeters(
            ForwardPowerDbm:   _forwardPowerDbm,
            ForwardPowerWatts: DbmToWatts(_forwardPowerDbm),
            ReturnLossDb:      _returnLossDb,
            SwrRatio:          swrRatio,
            DrivePowerDbm:     _drivePowerDbm,
            PaCurrent:         _paCurrent,
            TemperatureC:      _temperatureC);

        var setup = new PgxlPlugin.PgxlSetup(
            BandSource:        _bandSource,
            SelectedAntenna:   _selectedAntenna,
            AttenuatorEnabled: _attenuatorEnabled,
            BiasOffset:        _biasOffset,
            PttDelay:          _pttDelay,
            KeyDelay:          _keyDelay,
            HighSwr:           _highSwr,
            OverTemp:          _overTemp,
            OverCurrent:       _overCurrent);

        return new PgxlPlugin.PgxlStatusDto(
            Serial:          _device.Serial,
            IpAddress:       _device.IpAddress,
            IsConnected:     IsConnected,
            IsOperating:     IsOperating,
            IsTransmitting:  IsTransmitting,
            Band:            Band,
            BiasA:           BiasA,
            BiasB:           BiasB,
            FirmwareVersion: FirmwareVersion,
            Meters:          meters,
            Setup:           setup);
    }

    // P(W) = 10^((dBm - 30) / 10)
    private static double DbmToWatts(double dbm)
    {
        if (dbm <= 0) return 0;
        return Math.Pow(10, (dbm - 30) / 10);
    }

    // Return loss (dB) → SWR
    private static double ReturnLossToSwr(double rl)
    {
        if (rl <= 0) return 99.9;
        var gamma = Math.Pow(10, -rl / 20);
        if (gamma >= 1) return 99.9;
        var swr = (1 + gamma) / (1 - gamma);
        return Math.Min(swr, 99.9);
    }

    public void Disconnect()
    {
        try { _reader?.Close(); } catch { /* ignore */ }
        try { _stream?.Close(); } catch { /* ignore */ }
        try { _tcpClient?.Close(); } catch { /* ignore */ }
        _reader = null;
        _stream = null;
        _tcpClient = null;
    }
}

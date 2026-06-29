using System.Collections.Concurrent;
using System.Globalization;
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

namespace Openhpsdr.Zeus.Plugins.AntennaGenius;

/// <summary>
/// 4O3A Antenna Genius plugin.
///
/// Ported from Log4YM's <c>AntennaGeniusService</c>. The original was a
/// hosted <c>BackgroundService</c> that broadcast device events over SignalR;
/// here we run the same UDP-discovery + TCP-command loops from
/// <see cref="InitializeAsync"/>, hold device state in memory, and let the
/// frontend poll <c>GET status</c>.
/// </summary>
public sealed class AntennaGeniusPlugin : IZeusPlugin, IBackendPlugin
{
    private const int DiscoveryPort = 9007;
    private const int KeepAliveIntervalMs = 30000;
    private const int ReconnectDelayMs = 5000;
    private const string SettingsKey = "settings";

    private readonly ConcurrentDictionary<string, AntennaGeniusConnection> _connections = new();
    private IPluginContext? _ctx;
    private CancellationTokenSource? _cts;
    private Task? _discoveryTask;
    private Task? _keepAliveTask;

    // Manual-override connection state. Guarded by _manualLock because the
    // settings endpoint and the discovery path can both mutate it.
    private readonly object _manualLock = new();
    private CancellationTokenSource? _manualCts;
    private string? _manualKey;
    private CancellationToken _runToken;
    private AntennaGeniusSettings _settings = new();

    public async Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        _ctx = context;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _runToken = token;

        _settings = await SafeGetSettingsAsync(context, ct).ConfigureAwait(false) ?? new AntennaGeniusSettings();

        context.Logger.LogInformation(
            "Antenna Genius plugin starting; UDP discovery on port {Port}; manualIp={ManualIp} manualPort={ManualPort}",
            DiscoveryPort,
            string.IsNullOrWhiteSpace(_settings.ManualIpAddress) ? "(none)" : _settings.ManualIpAddress,
            _settings.ManualPort);

        _discoveryTask = Task.Run(() => RunDiscoveryListenerAsync(token), token);
        _keepAliveTask = Task.Run(() => RunKeepAliveAsync(token), token);

        // If a manual address is configured, dial it directly — this is the
        // mDNS-fallback path for networks whose switch filters the discovery
        // broadcast. Auto-discovery keeps running in parallel above for
        // operators whose switch passes it.
        ApplyManualConnection(_settings, token);
    }

    public async Task ShutdownAsync(CancellationToken ct)
    {
        _ctx?.Logger.LogInformation("Antenna Genius plugin stopping");

        if (_cts is not null)
        {
            try { _cts.Cancel(); } catch { /* ignore */ }
        }

        lock (_manualLock)
        {
            try { _manualCts?.Cancel(); } catch { /* ignore */ }
            _manualCts?.Dispose();
            _manualCts = null;
            _manualKey = null;
        }

        foreach (var conn in _connections.Values)
            conn.Disconnect();
        _connections.Clear();

        var tasks = new List<Task>(2);
        if (_discoveryTask is not null) tasks.Add(_discoveryTask);
        if (_keepAliveTask is not null) tasks.Add(_keepAliveTask);

        if (tasks.Count > 0)
        {
            try { await Task.WhenAll(tasks).WaitAsync(ct); }
            catch (OperationCanceledException) { /* expected on shutdown */ }
            catch (Exception ex)
            {
                _ctx?.Logger.LogDebug(ex, "Antenna Genius background tasks completed with exception");
            }
        }

        _cts?.Dispose();
        _cts = null;
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("status", GetStatus);
        endpoints.MapPost("select-antenna", SelectAntenna);
        endpoints.MapGet("settings", GetSettings);
        endpoints.MapPost("settings", PutSettings);
        endpoints.MapPost("test", TestConnection);
    }

    private IResult GetStatus()
    {
        var statuses = _connections.Values
            .Select(c => c.GetStatus())
            .ToArray();
        return Results.Ok(statuses);
    }

    private async Task<IResult> SelectAntenna(SelectAntennaRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Serial))
            return Results.BadRequest(new { error = "serial is required" });
        if (req.PortId is < 1 or > 2)
            return Results.BadRequest(new { error = "portId must be 1 or 2" });
        if (req.AntennaId < 0)
            return Results.BadRequest(new { error = "antennaId must be >= 0" });

        if (!_connections.TryGetValue(req.Serial, out var conn))
        {
            _ctx?.Logger.LogWarning("Cannot select antenna: device {Serial} not found", req.Serial);
            return Results.NotFound(new { error = $"device {req.Serial} not found" });
        }

        await conn.SelectAntennaAsync(req.PortId, req.AntennaId);
        return Results.Ok(conn.GetStatus());
    }

    // ----------------------------------------------------------------------
    // Manual-connection settings (issue #818). On networks whose switch
    // filters the UDP discovery broadcast (mDNS/Bonjour-style multicast),
    // auto-discovery never sees the device. These endpoints let the operator
    // pin a direct IP/port so the plugin dials the TCP command channel
    // straight away, no broadcast required.
    // ----------------------------------------------------------------------

    private IResult GetSettings() => Results.Ok(_settings);

    private async Task<IResult> PutSettings(AntennaGeniusSettings req)
    {
        if (req.ManualPort is < 1 or > 65535)
            return Results.BadRequest(new { error = "manualPort must be between 1 and 65535" });

        var next = new AntennaGeniusSettings(
            ManualIpAddress: (req.ManualIpAddress ?? string.Empty).Trim(),
            ManualPort: req.ManualPort);

        _settings = next;

        if (_ctx is not null)
            await PersistSettingsAsync(_ctx, next).ConfigureAwait(false);

        ApplyManualConnection(next, _runToken);
        return Results.Ok(next);
    }

    private async Task<IResult> TestConnection(AntennaGeniusTestRequest req)
    {
        var ip = (req.IpAddress ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(ip))
            return Results.BadRequest(new { error = "ipAddress is required" });
        if (req.Port is < 1 or > 65535)
            return Results.BadRequest(new { error = "port must be between 1 and 65535" });

        var result = await AntennaGeniusConnection.TestAsync(ip, req.Port, _ctx?.Logger).ConfigureAwait(false);
        return Results.Ok(result);
    }

    private static async Task<AntennaGeniusSettings?> SafeGetSettingsAsync(IPluginContext context, CancellationToken ct)
    {
        try
        {
            return await context.Settings.GetAsync<AntennaGeniusSettings>(SettingsKey, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(ex, "antennagenius.settings.get failed; using defaults");
            return null;
        }
    }

    private static async Task PersistSettingsAsync(IPluginContext context, AntennaGeniusSettings settings)
    {
        try
        {
            await context.Settings.SetAsync(SettingsKey, settings).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(ex, "antennagenius.settings.set failed; in-memory settings kept");
        }
    }

    private static string ManualKey(string ip, int port) =>
        string.Create(CultureInfo.InvariantCulture, $"manual:{ip}:{port}");

    /// <summary>
    /// Reconcile the manual connection to match <paramref name="settings"/>.
    /// Idempotent: a no-op when already pointed at the same endpoint. Tears
    /// down the previous manual connection (cancelling its reconnect loop) when
    /// the address changes or is cleared, and refuses to open a second
    /// connection to an IP that auto-discovery has already reached.
    /// </summary>
    private void ApplyManualConnection(AntennaGeniusSettings settings, CancellationToken ct)
    {
        lock (_manualLock)
        {
            var ip = (settings.ManualIpAddress ?? string.Empty).Trim();
            var newKey = string.IsNullOrEmpty(ip) ? null : ManualKey(ip, settings.ManualPort);

            if (newKey == _manualKey)
                return; // already serving this endpoint

            // Tear down any previous manual connection. Cancelling its own CTS
            // stops the reconnect loop; Disconnect closes the live socket.
            if (_manualCts is not null)
            {
                try { _manualCts.Cancel(); } catch { /* ignore */ }
                _manualCts.Dispose();
                _manualCts = null;
            }
            if (_manualKey is not null && _connections.TryRemove(_manualKey, out var old))
                old.Disconnect();
            _manualKey = null;

            if (newKey is null)
                return; // manual override cleared

            // Don't double-connect if discovery already reached this IP.
            if (_connections.Values.Any(c => string.Equals(c.IpAddress, ip, StringComparison.OrdinalIgnoreCase)))
            {
                _ctx?.Logger.LogInformation(
                    "Manual Antenna Genius {Ip} already connected via discovery; not adding a second connection", ip);
                return;
            }

            var device = new AntennaGeniusDiscoveredEvent(
                IpAddress: ip,
                Port: settings.ManualPort,
                Version: "",
                Serial: newKey,
                Name: "Antenna Genius (manual)",
                RadioPorts: 2,
                AntennaPorts: 8,
                Mode: "master",
                Uptime: 0);

            var connection = new AntennaGeniusConnection(device, _ctx!.Logger);
            if (_connections.TryAdd(newKey, connection))
            {
                _manualKey = newKey;
                _manualCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _ctx?.Logger.LogInformation(
                    "Connecting to manually-configured Antenna Genius at {Ip}:{Port}", ip, settings.ManualPort);
                _ = connection.ConnectAsync(_manualCts.Token);
            }
        }
    }

    private async Task RunDiscoveryListenerAsync(CancellationToken ct)
    {
        using var udpClient = new UdpClient();
        udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        try
        {
            udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
        }
        catch (Exception ex)
        {
            _ctx?.Logger.LogError(ex, "Failed to bind UDP discovery port {Port}", DiscoveryPort);
            return;
        }

        _ctx?.Logger.LogInformation("Listening for Antenna Genius discovery on UDP port {Port}", DiscoveryPort);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await udpClient.ReceiveAsync(ct);
                var message = Encoding.ASCII.GetString(result.Buffer);

                if (message.StartsWith("AG ", StringComparison.Ordinal))
                {
                    var device = ParseDiscoveryMessage(message);
                    if (device != null)
                        HandleDeviceDiscovered(device, ct);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _ctx?.Logger.LogError(ex, "Error in discovery listener");
                try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private AntennaGeniusDiscoveredEvent? ParseDiscoveryMessage(string message)
    {
        // AG ip=192.168.1.39 port=9007 v=4.0.22 serial=9A-3A-DC name=Ranko_4O3A ports=2 antennas=8 mode=master uptime=3034
        try
        {
            var parts = message.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var values = new Dictionary<string, string>();

            foreach (var part in parts.Skip(1)) // Skip "AG"
            {
                var kv = part.Split('=', 2);
                if (kv.Length == 2)
                    values[kv[0]] = kv[1];
            }

            return new AntennaGeniusDiscoveredEvent(
                IpAddress: values.GetValueOrDefault("ip", ""),
                Port: int.Parse(values.GetValueOrDefault("port", "9007"), CultureInfo.InvariantCulture),
                Version: values.GetValueOrDefault("v", ""),
                Serial: values.GetValueOrDefault("serial", ""),
                Name: values.GetValueOrDefault("name", "Unknown").Replace('_', ' '),
                RadioPorts: int.Parse(values.GetValueOrDefault("ports", "2"), CultureInfo.InvariantCulture),
                AntennaPorts: int.Parse(values.GetValueOrDefault("antennas", "8"), CultureInfo.InvariantCulture),
                Mode: values.GetValueOrDefault("mode", "master"),
                Uptime: int.Parse(values.GetValueOrDefault("uptime", "0"), CultureInfo.InvariantCulture)
            );
        }
        catch (Exception ex)
        {
            _ctx?.Logger.LogWarning(ex, "Failed to parse discovery message: {Message}", message);
            return null;
        }
    }

    private void HandleDeviceDiscovered(AntennaGeniusDiscoveredEvent device, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(device.Serial))
            return;

        if (!_connections.TryGetValue(device.Serial, out var connection))
        {
            // Dedup by IP: if we already have a connection to this address
            // (e.g. a manual override pinned the same unit), don't open a
            // second socket to the same device.
            if (_connections.Values.Any(c => string.Equals(c.IpAddress, device.IpAddress, StringComparison.OrdinalIgnoreCase)))
                return;

            _ctx?.Logger.LogInformation("Discovered Antenna Genius: {Name} ({Serial}) at {Ip}:{Port}",
                device.Name, device.Serial, device.IpAddress, device.Port);

            connection = new AntennaGeniusConnection(device, _ctx!.Logger);
            if (_connections.TryAdd(device.Serial, connection))
            {
                _ = connection.ConnectAsync(ct);
            }
        }
        else
        {
            connection.UpdateDiscovery(device);
        }
    }

    private async Task RunKeepAliveAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(KeepAliveIntervalMs, ct);

                foreach (var connection in _connections.Values)
                {
                    if (connection.IsConnected)
                        _ = connection.SendPingAsync();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    // ----------------------------------------------------------------------
    // Per-device TCP connection. Carries the parsing logic from Log4YM
    // essentially unchanged; the IHubContext broadcasts have been removed
    // in favour of in-memory state mutation.
    // ----------------------------------------------------------------------
    internal sealed class AntennaGeniusConnection
    {
        private readonly ILogger _logger;
        private AntennaGeniusDiscoveredEvent _device;

        private TcpClient? _tcpClient;
        private NetworkStream? _stream;
        private StreamReader? _reader;
        private int _sequenceNumber = 0;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly ConcurrentDictionary<int, TaskCompletionSource<List<string>>> _pendingCommands = new();
        private readonly ConcurrentDictionary<int, List<string>> _pendingResponses = new();

        private List<AntennaGeniusAntennaInfo> _antennas = new();
        private List<AntennaGeniusBandInfo> _bands = new();
        private AntennaGeniusPortStatus _portA = new(1, true, "AUTO", 0, 0, 0, false, false);
        private AntennaGeniusPortStatus _portB = new(2, true, "AUTO", 0, 0, 0, false, false);
        private string _version = "";

        public bool IsConnected => _tcpClient?.Connected ?? false;

        public string IpAddress => _device.IpAddress;

        /// <summary>
        /// Probe an Antenna Genius at <paramref name="ip"/>:<paramref name="port"/>
        /// without joining the live connection pool. Opens a short-lived TCP
        /// connection, reads the firmware prologue if the device offers one, and
        /// disposes everything. Used by the <c>POST test</c> endpoint so the
        /// operator can confirm a manual address before saving it.
        /// </summary>
        public static async Task<AntennaGeniusTestResult> TestAsync(string ip, int port, ILogger? logger, CancellationToken ct = default)
        {
            try
            {
                using var client = new TcpClient();
                using (var dialCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    dialCts.CancelAfter(TimeSpan.FromSeconds(3));
                    await client.ConnectAsync(ip, port, dialCts.Token).ConfigureAwait(false);
                }

                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII);

                string? prologue = null;
                try
                {
                    using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    readCts.CancelAfter(TimeSpan.FromSeconds(2));
                    prologue = await reader.ReadLineAsync(readCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Device connected but sent no prologue within the window —
                    // still a successful reach.
                }

                var version = prologue is not null && prologue.StartsWith("V", StringComparison.Ordinal)
                    ? prologue.Split(' ')[0].Substring(1)
                    : string.Empty;

                return new AntennaGeniusTestResult(true, version, null);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new AntennaGeniusTestResult(false, string.Empty, "Connection timed out");
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "Antenna Genius test connection to {Ip}:{Port} failed", ip, port);
                return new AntennaGeniusTestResult(false, string.Empty, ex.Message);
            }
        }

        public AntennaGeniusConnection(AntennaGeniusDiscoveredEvent device, ILogger logger)
        {
            _device = device;
            _logger = logger;
        }

        public void UpdateDiscovery(AntennaGeniusDiscoveredEvent device)
        {
            _device = device;
        }

        public async Task ConnectAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    _logger.LogInformation("Connecting to Antenna Genius at {Ip}:{Port}...",
                        _device.IpAddress, _device.Port);

                    _tcpClient = new TcpClient();
                    await _tcpClient.ConnectAsync(_device.IpAddress, _device.Port, ct);
                    _stream = _tcpClient.GetStream();
                    _reader = new StreamReader(_stream, Encoding.ASCII);

                    // Read prologue (V4.0.22 AG)
                    var prologue = await _reader.ReadLineAsync(ct);
                    if (prologue != null && prologue.StartsWith("V", StringComparison.Ordinal))
                    {
                        _version = prologue.Split(' ')[0].Substring(1);
                        _logger.LogInformation("Connected to Antenna Genius {Name}, firmware {Version}",
                            _device.Name, _version);
                    }

                    // Start receive loop in background FIRST so it can process responses
                    var receiveTask = Task.Run(() => ReceiveLoopAsync(ct), ct);

                    // Small delay to let receive loop start
                    await Task.Delay(100, ct);

                    // Initialize: get antenna list, bands, and port status
                    await InitializeAsync(ct);

                    // Subscribe to updates
                    await SubscribeToUpdatesAsync(ct);

                    // Wait for receive loop to complete (usually when connection drops)
                    await receiveTask;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Connection error to {Name}", _device.Name);
                }

                // Cleanup and retry
                Disconnect();

                if (!ct.IsCancellationRequested)
                {
                    _logger.LogInformation("Reconnecting to {Name} in 5 seconds...", _device.Name);
                    try { await Task.Delay(ReconnectDelayMs, ct); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }

        private async Task InitializeAsync(CancellationToken ct)
        {
            var antennaResponse = await SendCommandAsync("antenna list", ct);
            _antennas = ParseAntennaList(antennaResponse);
            _logger.LogDebug("Loaded {Count} antennas", _antennas.Count);

            var bandResponse = await SendCommandAsync("band list", ct);
            _bands = ParseBandList(bandResponse);
            _logger.LogDebug("Loaded {Count} bands", _bands.Count);

            var port1Response = await SendCommandAsync("port get 1", ct);
            _portA = ParsePortStatus(port1Response, 1);

            var port2Response = await SendCommandAsync("port get 2", ct);
            _portB = ParsePortStatus(port2Response, 2);

            _logger.LogInformation("Port A: Band={Band}, Antenna={Ant}, Port B: Band={BandB}, Antenna={AntB}",
                _bands.FirstOrDefault(b => b.Id == _portA.Band)?.Name ?? "None",
                _antennas.FirstOrDefault(a => a.Id == _portA.RxAntenna)?.Name ?? "None",
                _bands.FirstOrDefault(b => b.Id == _portB.Band)?.Name ?? "None",
                _antennas.FirstOrDefault(a => a.Id == _portB.RxAntenna)?.Name ?? "None");
        }

        private async Task SubscribeToUpdatesAsync(CancellationToken ct)
        {
            await SendCommandAsync("sub port all", ct);
            await SendCommandAsync("sub relay", ct);
            _logger.LogDebug("Subscribed to port and relay updates");
        }

        private async Task<List<string>> SendCommandAsync(string command, CancellationToken ct = default)
        {
            await _sendLock.WaitAsync(ct);
            try
            {
                var seq = Interlocked.Increment(ref _sequenceNumber) % 256;
                if (seq == 0) seq = 1;

                var tcs = new TaskCompletionSource<List<string>>();
                _pendingCommands[seq] = tcs;

                var commandLine = $"C{seq}|{command}\r\n";
                var bytes = Encoding.ASCII.GetBytes(commandLine);

                await _stream!.WriteAsync(bytes, ct);
                await _stream.FlushAsync(ct);

                _logger.LogInformation("Sent: {Command}", commandLine.TrimEnd());

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(5000);

                try
                {
                    return await tcs.Task.WaitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("Command timeout: {Command}", command);
                    return new List<string>();
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
            _logger.LogInformation("Receive loop starting...");

            while (!ct.IsCancellationRequested && _tcpClient?.Connected == true)
            {
                try
                {
                    var line = await _reader!.ReadLineAsync(ct);
                    if (line == null)
                    {
                        _logger.LogWarning("Connection closed by server");
                        break;
                    }

                    _logger.LogInformation("Received line: {Line}", line);
                    await ProcessLineAsync(line);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in receive loop");
                    break;
                }
            }
            _logger.LogInformation("Receive loop ended");
        }

        private async Task ProcessLineAsync(string line)
        {
            _logger.LogInformation("Received: {Line}", line);

            if (line.StartsWith("R", StringComparison.Ordinal))
            {
                // Response: R<seq>|<hex_code>|<message>
                var parts = line.Split('|', 3);
                if (parts.Length >= 2)
                {
                    var seqStr = parts[0].Substring(1);
                    if (int.TryParse(seqStr, out var seq))
                    {
                        var message = parts.Length > 2 ? parts[2] : "";

                        if (string.IsNullOrEmpty(message))
                        {
                            // Empty message means end of response - complete the TCS
                            if (_pendingCommands.TryGetValue(seq, out var tcs))
                            {
                                var responses = _pendingResponses.GetValueOrDefault(seq, new List<string>());
                                _pendingResponses.TryRemove(seq, out _);
                                tcs.TrySetResult(responses);
                                _logger.LogDebug("Command {Seq} completed with {Count} responses (terminator)", seq, responses.Count);
                            }
                        }
                        else
                        {
                            // Non-empty message - accumulate it
                            if (!_pendingResponses.ContainsKey(seq))
                                _pendingResponses[seq] = new List<string>();
                            _pendingResponses[seq].Add(message);

                            // Single-line responses like "port get" complete immediately
                            // since they don't send an empty terminator
                            if (message.StartsWith("port ", StringComparison.Ordinal) &&
                                _pendingCommands.TryGetValue(seq, out var tcs))
                            {
                                var responses = _pendingResponses.GetValueOrDefault(seq, new List<string>());
                                _pendingResponses.TryRemove(seq, out _);
                                tcs.TrySetResult(responses);
                                _logger.LogDebug("Command {Seq} completed with {Count} responses (single-line)", seq, responses.Count);
                            }
                        }
                    }
                }
            }
            else if (line.StartsWith("S0|", StringComparison.Ordinal))
            {
                // Status message
                var message = line.Substring(3);
                await ProcessStatusMessageAsync(message);
            }
        }

        private async Task ProcessStatusMessageAsync(string message)
        {
            // S0|port 1 auto=1 source=AUTO band=0 rxant=0 txant=0 inband=0 tx=0 inhibit=0
            // S0|relay tx=00 rx=04 state=04

            if (message.StartsWith("port ", StringComparison.Ordinal))
            {
                var status = ParsePortStatusFromMessage(message);
                if (status != null)
                {
                    if (status.PortId == 1)
                        _portA = status;
                    else if (status.PortId == 2)
                        _portB = status;

                    _logger.LogInformation("Port {Port} changed: Band={Band}, RxAnt={RxAnt}, TxAnt={TxAnt}, TX={Tx}",
                        status.PortId,
                        _bands.FirstOrDefault(b => b.Id == status.Band)?.Name ?? "None",
                        _antennas.FirstOrDefault(a => a.Id == status.RxAntenna)?.Name ?? "None",
                        _antennas.FirstOrDefault(a => a.Id == status.TxAntenna)?.Name ?? "None",
                        status.IsTransmitting);
                }
            }
            else if (message.StartsWith("relay ", StringComparison.Ordinal))
            {
                _logger.LogDebug("Relay status: {Message}", message);
            }
            else if (message.StartsWith("antenna reload", StringComparison.Ordinal))
            {
                _logger.LogInformation("Antenna configuration changed, reloading...");
                var antennaResponse = await SendCommandAsync("antenna list");
                _antennas = ParseAntennaList(antennaResponse);
            }
        }

        private static List<AntennaGeniusAntennaInfo> ParseAntennaList(List<string> responses)
        {
            var antennas = new List<AntennaGeniusAntennaInfo>();
            foreach (var line in responses)
            {
                // antenna 1 name=Antenna_1 tx=0000 rx=0001 inband=0000
                var match = Regex.Match(line, @"antenna (\d+) name=(\S+) tx=([0-9A-Fa-f]+) rx=([0-9A-Fa-f]+) inband=([0-9A-Fa-f]+)");
                if (match.Success)
                {
                    antennas.Add(new AntennaGeniusAntennaInfo(
                        Id: int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                        Name: match.Groups[2].Value.Replace('_', ' '),
                        TxBandMask: Convert.ToUInt16(match.Groups[3].Value, 16),
                        RxBandMask: Convert.ToUInt16(match.Groups[4].Value, 16),
                        InbandMask: Convert.ToUInt16(match.Groups[5].Value, 16)
                    ));
                }
            }
            return antennas;
        }

        private static List<AntennaGeniusBandInfo> ParseBandList(List<string> responses)
        {
            var bands = new List<AntennaGeniusBandInfo>();
            foreach (var line in responses)
            {
                // band 0 name=None freq_start=0.000000 freq_stop=0.000000
                var match = Regex.Match(line, @"band (\d+) name=(\S+) freq_start=(\S+) freq_stop=(\S+)");
                if (match.Success)
                {
                    bands.Add(new AntennaGeniusBandInfo(
                        Id: int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                        Name: match.Groups[2].Value.Replace('_', ' '),
                        FreqStart: double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture),
                        FreqStop: double.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture)
                    ));
                }
            }
            return bands;
        }

        private static AntennaGeniusPortStatus ParsePortStatus(List<string> responses, int portId)
        {
            foreach (var line in responses)
            {
                var status = ParsePortStatusFromMessage($"port {portId} " + line.Replace($"port {portId} ", ""));
                if (status != null && status.PortId == portId)
                    return status;

                status = ParsePortStatusFromMessage(line);
                if (status != null)
                    return status;
            }

            return new AntennaGeniusPortStatus(portId, true, "AUTO", 0, 0, 0, false, false);
        }

        private static AntennaGeniusPortStatus? ParsePortStatusFromMessage(string message)
        {
            // port 1 auto=1 source=AUTO band=0 rxant=0 txant=0 tx=0 inhibit=0
            var match = Regex.Match(message,
                @"port (\d+) auto=(\d+) source=(\S+) band=(\d+) rxant=(\d+) txant=(\d+).*?tx=(\d+) inhibit=(\d+)");

            if (match.Success)
            {
                return new AntennaGeniusPortStatus(
                    PortId: int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                    Auto: match.Groups[2].Value == "1",
                    Source: match.Groups[3].Value,
                    Band: int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture),
                    RxAntenna: int.Parse(match.Groups[5].Value, CultureInfo.InvariantCulture),
                    TxAntenna: int.Parse(match.Groups[6].Value, CultureInfo.InvariantCulture),
                    IsTransmitting: match.Groups[7].Value == "1",
                    IsInhibited: match.Groups[8].Value == "1"
                );
            }

            return null;
        }

        public async Task SelectAntennaAsync(int portId, int antennaId)
        {
            if (!IsConnected)
            {
                _logger.LogWarning("Cannot select antenna: not connected");
                return;
            }

            // Set both TX and RX antenna to the same value
            var command = $"port set {portId} rxant={antennaId} txant={antennaId}";
            await SendCommandAsync(command);

            _logger.LogInformation("Selected antenna {AntennaId} for port {PortId}", antennaId, portId);
        }

        public async Task SendPingAsync()
        {
            try
            {
                await SendCommandAsync("ping");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Ping failed");
            }
        }

        public AntennaGeniusStatusEvent GetStatus()
        {
            return new AntennaGeniusStatusEvent(
                DeviceSerial: _device.Serial,
                DeviceName: _device.Name,
                IpAddress: _device.IpAddress,
                Version: _version,
                IsConnected: IsConnected,
                Antennas: _antennas,
                Bands: _bands,
                PortA: _portA,
                PortB: _portB
            );
        }

        public void Disconnect()
        {
            try
            {
                _stream?.Close();
                _tcpClient?.Close();
            }
            catch { /* ignore */ }

            _stream = null;
            _tcpClient = null;
        }
    }
}

// ---------------------------------------------------------------------------
// DTOs — record shapes ported verbatim from Log4YM (namespaces only changed).
// JsonPropertyName attributes ensure camelCase JSON for the frontend.
// ---------------------------------------------------------------------------

public sealed record AntennaGeniusDiscoveredEvent(
    [property: JsonPropertyName("ipAddress")]    string IpAddress,
    [property: JsonPropertyName("port")]         int Port,
    [property: JsonPropertyName("version")]      string Version,
    [property: JsonPropertyName("serial")]       string Serial,
    [property: JsonPropertyName("name")]         string Name,
    [property: JsonPropertyName("radioPorts")]   int RadioPorts,
    [property: JsonPropertyName("antennaPorts")] int AntennaPorts,
    [property: JsonPropertyName("mode")]         string Mode,
    [property: JsonPropertyName("uptime")]       int Uptime
);

public sealed record AntennaGeniusAntennaInfo(
    [property: JsonPropertyName("id")]          int Id,
    [property: JsonPropertyName("name")]        string Name,
    [property: JsonPropertyName("txBandMask")]  ushort TxBandMask,
    [property: JsonPropertyName("rxBandMask")]  ushort RxBandMask,
    [property: JsonPropertyName("inbandMask")]  ushort InbandMask
);

public sealed record AntennaGeniusBandInfo(
    [property: JsonPropertyName("id")]        int Id,
    [property: JsonPropertyName("name")]      string Name,
    [property: JsonPropertyName("freqStart")] double FreqStart,
    [property: JsonPropertyName("freqStop")]  double FreqStop
);

public sealed record AntennaGeniusPortStatus(
    [property: JsonPropertyName("portId")]         int PortId,
    [property: JsonPropertyName("auto")]           bool Auto,
    [property: JsonPropertyName("source")]         string Source,
    [property: JsonPropertyName("band")]           int Band,
    [property: JsonPropertyName("rxAntenna")]      int RxAntenna,
    [property: JsonPropertyName("txAntenna")]      int TxAntenna,
    [property: JsonPropertyName("isTransmitting")] bool IsTransmitting,
    [property: JsonPropertyName("isInhibited")]    bool IsInhibited
);

public sealed record AntennaGeniusStatusEvent(
    [property: JsonPropertyName("deviceSerial")] string DeviceSerial,
    [property: JsonPropertyName("deviceName")]   string DeviceName,
    [property: JsonPropertyName("ipAddress")]    string IpAddress,
    [property: JsonPropertyName("version")]      string Version,
    [property: JsonPropertyName("isConnected")]  bool IsConnected,
    [property: JsonPropertyName("antennas")]     List<AntennaGeniusAntennaInfo> Antennas,
    [property: JsonPropertyName("bands")]        List<AntennaGeniusBandInfo> Bands,
    [property: JsonPropertyName("portA")]        AntennaGeniusPortStatus PortA,
    [property: JsonPropertyName("portB")]        AntennaGeniusPortStatus PortB
);

public sealed record SelectAntennaRequest(
    [property: JsonPropertyName("serial")]    string Serial,
    [property: JsonPropertyName("portId")]    int PortId,
    [property: JsonPropertyName("antennaId")] int AntennaId
);

// ---------------------------------------------------------------------------
// Manual-connection settings (issue #818). Persisted via IPluginContext.Settings
// (LiteDB-backed, isolated per plugin). An empty ManualIpAddress means "use
// auto-discovery only" — the default. Port defaults to 9007, the Antenna Genius
// firmware v4.0+ command/discovery port.
// ---------------------------------------------------------------------------

public sealed record AntennaGeniusSettings(
    [property: JsonPropertyName("manualIpAddress")] string ManualIpAddress = "",
    [property: JsonPropertyName("manualPort")]      int ManualPort = 9007
);

public sealed record AntennaGeniusTestRequest(
    [property: JsonPropertyName("ipAddress")] string IpAddress,
    [property: JsonPropertyName("port")]      int Port
);

public sealed record AntennaGeniusTestResult(
    [property: JsonPropertyName("ok")]      bool Ok,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("error")]   string? Error
);

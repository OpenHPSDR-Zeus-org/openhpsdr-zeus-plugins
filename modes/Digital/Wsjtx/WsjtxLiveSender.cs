// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus Digital plugin — extracted from Zeus core
// (the live-stream half of Zeus.Server.Hosting/Wsjtx/WsjtxUdpBroadcaster.cs).
// GPL-2.0-or-later.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// The type-12 LoggedADIF / type-5 QSOLogged path STAYS IN CORE (it is
// log-driven and mode-agnostic); this sender carries only the live
// Heartbeat/Status/Decode/WSPRDecode stream the plugin's WsjtxLiveEmitter
// produces. Config comes from the plugin-side WsjtxLiveConfig (pushed by the
// core UI) instead of WsjtxManagementService.

using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Openhpsdr.Zeus.Plugins.Digital.Wsjtx;

/// <summary>
/// Sends WSJT-X NetworkMessage datagrams so third-party loggers (JTAlert /
/// GridTracker / JTDX) pick up the plugin's live FT8/FT4/WSPR activity.
///
/// SEND-ONLY: there is no inbound socket anywhere in this plugin. The plugin
/// never honours Reply(4)/HaltTx(8)/FreeText(9) — those are network TX-triggers
/// into a real PA. No-ops when disabled (the default) — network egress is
/// opt-in. Transport is unicast by default, or multicast (operator-configured
/// TTL, default 1) when the config's Multicast flag treats Host as the group
/// address. Cross-platform: pure <see cref="UdpClient"/>, no native deps.
///
/// One long-lived send socket is cached for the sender's lifetime.
/// <see cref="UdpClient.SendAsync(byte[],int,string,int)"/> takes the
/// destination per call, so the SAME socket serves unicast and multicast and
/// every host/port change without re-allocation. Sends are serialised through a
/// semaphore so the fire-and-forget live stream (tens of Decode datagrams per
/// FT8 slot, plus Status/Heartbeat) never issues concurrent SendAsync on a
/// single socket.
/// </summary>
public sealed class WsjtxLiveSender : IDisposable
{
    private readonly ILogger _log;
    private readonly Func<WsjtxLiveConfig> _config;

    // One cached send socket for the sender's lifetime. Created lazily on the
    // first enabled send so the disabled-default path allocates no socket at all.
    // _sendGate serialises sends; _appliedMulticastTtl tracks the TTL currently
    // set on the socket so an operator TTL change re-applies (mirrors the
    // in-core broadcaster's behavior; -1 = never applied).
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private UdpClient? _udp;
    private int _appliedMulticastTtl = -1;
    private bool _disposed;

    public WsjtxLiveSender(ILogger log, Func<WsjtxLiveConfig> config)
    {
        _log = log;
        _config = config;
    }

    /// <summary>Send a pre-encoded datagram on the live stream (Heartbeat / Status
    /// / Decode / WSPRDecode). No-op when disabled; never throws.</summary>
    public async Task SendDatagramAsync(byte[] datagram, CancellationToken ct = default)
    {
        var cfg = _config();
        if (!cfg.Enabled) return;
        try
        {
            await SendInternalAsync(cfg, datagram, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "wsjtx.live send failed bytes={Bytes}", datagram.Length);
        }
    }

    private async Task SendInternalAsync(WsjtxLiveConfig cfg, byte[] datagram, CancellationToken ct)
    {
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var udp = _udp ??= new UdpClient();
            var ttl = Math.Clamp(cfg.MulticastTtl, 1, 255);
            if (cfg.Multicast && ttl != _appliedMulticastTtl)
            {
                // Operator-configured multicast TTL (hop limit); default 1 keeps
                // datagrams on the local segment. Send-only multicast needs no
                // JoinMulticastGroup — the group address is just the destination.
                udp.Client.SetSocketOption(
                    SocketOptionLevel.IP,
                    SocketOptionName.MulticastTimeToLive,
                    ttl);
                _appliedMulticastTtl = ttl;
            }
            await udp.SendAsync(datagram, datagram.Length, cfg.Host, cfg.Port)
                .WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _udp?.Dispose();
        _sendGate.Dispose();
    }
}

// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus Digital · FT8/FT4 — wire DTOs for the plugin's HTTP + SSE surface.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Inlined copies of the records that lived in Zeus.Contracts
// (Ft8DecodeFrame.cs / WsprSpotFrame.cs / Ft8TxStatusFrame.cs / SpottingDtos.cs)
// and the request records from Zeus.Server.Hosting/Ft8TxEndpoints.cs — the
// Rf2k/Recorder plugin precedent. Field names and order are preserved so the
// camelCase JSON the core frontend ingests is byte-shape-identical to today's
// 0x38 / 0x39 / 0x3A frame payloads and the old /api/ft8|wspr request bodies.
// The MsgType byte itself does not travel here: SSE carries the event name
// (ft8decode / wsprspot / txstatus) instead.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Openhpsdr.Zeus.Plugins.Digital;

/// <summary>One decoded FT8/FT4 message line for the wire/UI.</summary>
/// <remarks>
/// <paramref name="Country"/> is additive enrichment attached at decode
/// broadcast (the resolver lives in Dsp/). <paramref name="WorkedBefore"/> is
/// always emitted false by the plugin — the core frontend decorates rows from
/// its own logbook (GET /api/log/digital-worked) at render time. The field is
/// kept so the JSON shape matches today's 0x38 payload exactly.
/// </remarks>
public sealed record Ft8DecodeDto(
    float SnrDb,
    float DtSec,
    float FreqHz,
    int Score,
    string Text,
    bool WorkedBefore = false,
    string? Country = null);

/// <summary>All decodes from one completed slot on one receiver.</summary>
public sealed record Ft8DecodeBatchDto(
    int Receiver,
    long SlotStartUnixMs,
    string Protocol,                 // "FT8" | "FT4"
    IReadOnlyList<Ft8DecodeDto> Decodes);

/// <summary>Request body for POST ft8/enable.</summary>
public sealed record Ft8EnableRequest(
    int? Receiver,
    string? Protocol,                // "FT8" | "FT4" (default FT8)
    int? Passes);                    // 1 = NORMAL, &gt;1 = DEEP/MULTI

/// <summary>Request body for POST wspr/enable.</summary>
public sealed record WsprEnableRequest(
    int? Receiver,
    double? DialFreqMhz);            // transceiver dial freq, e.g. 14.0956 (20 m)

/// <summary>One decoded WSPR spot for the wire/UI. Message text is
/// "&lt;callsign&gt; &lt;grid4&gt; &lt;dBm&gt;".</summary>
public sealed record WsprSpotDto(
    float SnrDb,
    float DtSec,
    float FreqMhz,
    int DriftHz,
    string Message);

/// <summary>All spots from one completed 120 s slot on one receiver.</summary>
public sealed record WsprSpotBatchDto(
    int Receiver,
    long SlotStartUnixMs,
    double DialFreqMhz,
    IReadOnlyList<WsprSpotDto> Spots);

/// <summary>The live state of the digital-mode TX keyer for the wire/UI.</summary>
/// <remarks>
/// Identical to today's 0x3A payload plus ONE additive trailing field:
/// <paramref name="SkippedSlots"/> counts TX slots the keyer refused because
/// MOX was already keyed by another source when the slot fired (the pre-key
/// gate — see Ft8TxService). Additive-at-the-end so existing ingest is
/// untouched.
/// </remarks>
public sealed record Ft8TxStatusDto(
    bool Armed,                   // ENABLE-TX master; defaults false, never auto-set
    bool Transmitting,            // true while a slot is being keyed on the air
    string Mode,                  // "FT8" | "FT4" | "WSPR"
    string? Message,              // currently staged / transmitting message, if any
    int AudioHz,                  // TX audio offset of tone 0
    string Slot,                  // "even" | "odd" (FT8/FT4); "" for WSPR
    int WatchdogSecsRemaining,    // seconds until the unattended watchdog disarms (0 = disarmed)
    long? LastTxSlotMs,           // unix-ms of the last slot the keyer transmitted, if any
    bool NativeAvailable,         // encode/synth path present on this platform
    int SkippedSlots = 0);        // slots skipped because MOX was already keyed elsewhere

/// <summary>POST ft8/tx/arm — the FT8/FT4 ENABLE-TX master.</summary>
public sealed record Ft8TxArmRequest(bool Enabled);

/// <summary>POST ft8/tx — stage the next FT8/FT4 transmission.</summary>
public sealed record Ft8TxStageRequest(string Message, int? AudioHz, string? Slot, string? Mode);

/// <summary>POST wspr/tx/arm — the WSPR ENABLE-TX master.</summary>
public sealed record WsprTxArmRequest(bool Enabled);

/// <summary>POST wspr/tx/settings — beacon content + cadence.</summary>
public sealed record WsprTxSettingsRequest(
    string Call, string Grid4, int? DBm, int? AudioHz, double? TxPercent);

/// <summary>
/// Persisted config for the digital-mode spotting uploaders (today's
/// Zeus.Contracts.SpottingRuntimeConfig shape). Both DISABLED by default —
/// they only run when the operator explicitly opts in AND a callsign +
/// Maidenhead grid are available (from this override, or the identity the
/// core UI pushes to config/identity). RX-spot push only; never transmits.
/// </summary>
public sealed record SpottingRuntimeConfig(
    bool PskReporterEnabled = false,
    bool WsprnetEnabled = false,
    string Callsign = "",
    string Grid = "");

/// <summary>
/// Status/config view for the spotting uploaders (today's SpottingStatus
/// shape). <paramref name="IdentityResolved"/> is true when a callsign + grid
/// are available (pushed identity or the config override), so the panel can
/// warn when they are missing.
/// </summary>
public sealed record SpottingStatus(
    bool PskReporterEnabled,
    bool WsprnetEnabled,
    string Callsign,
    string Grid,
    bool IdentityResolved);

/// <summary>GET/POST config/identity — operator callsign + Maidenhead grid,
/// pushed by the core UI (which resolves override → QRZ home before pushing).</summary>
public sealed record OperatorIdentityConfig(string? Call, string? Grid);

/// <summary>
/// GET/POST config/wsjtx-live — the live WSJT-X UDP stream config, pushed by
/// the core UI. When <paramref name="Multicast"/> is true, <paramref name="Host"/>
/// is treated as the multicast group address and <paramref name="MulticastTtl"/>
/// is the hop limit (clamped 1..255, matching the in-core broadcaster).
/// <paramref name="InstanceId"/> is optional and defaults to the WSJT-X default
/// so third-party tools that key on the instance id keep working when the core
/// UI omits it.
/// </summary>
public sealed record WsjtxLiveConfig(
    bool Enabled = false,
    string Host = "127.0.0.1",
    int Port = 2237,
    bool Multicast = false,
    string InstanceId = "WSJT-X",
    int MulticastTtl = 1);

/// <summary>
/// Shared JSON options for the SSE event payloads — the exact options
/// Ft8DecodeFrame / WsprSpotFrame / Ft8TxStatusFrame used, so the serialized
/// bytes match today's frame payloads.
/// </summary>
public static class DigitalJson
{
    public static readonly JsonSerializerOptions SseOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}

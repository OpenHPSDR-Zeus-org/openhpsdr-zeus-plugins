// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see ATTRIBUTIONS.md for provenance.

namespace Zeus.Server.Voyeur.Alerts;

/// <summary>
/// Watchword Alerts (Voyeur Mode, zeus-la5) — "ping me when my call/name is
/// heard". The operator registers watchwords (callsigns or plain keywords); when
/// one is heard in a transcribed over, Voyeur fires a notification through the
/// operator's own FREE channels (their SMTP, an ntfy topic, or a carrier
/// email-to-SMS gateway). This file holds the pure data shapes; the matching,
/// gating, clip-making and delivery live in their own files.
///
/// SECRETS: <see cref="EmailChannelConfig.Password"/> and
/// <see cref="NtfyChannelConfig.Token"/> are persisted in the plugin's isolated
/// settings store but MUST NEVER be logged or returned over the API. The GET
/// surface uses <see cref="AlertConfigDto"/> (booleans only); the PUT surface
/// uses <see cref="AlertConfigUpdate"/> where a null/blank secret means "keep
/// the stored one".
/// </summary>
public enum WatchwordType
{
    /// <summary>Amateur callsign — matched against CallsignExtractor output and a
    /// word-boundary literal scan (case- and punctuation-insensitive).</summary>
    Callsign,
    /// <summary>Plain keyword/name — matched word-boundary in the transcript so
    /// "Doug" hits "Doug" but never "doughnut".</summary>
    Keyword,
}

/// <summary>One thing the operator wants to be pinged about.</summary>
public sealed record Watchword(string Text, WatchwordType Type, bool Enabled = true);

/// <summary>Operator's own SMTP relay. Password is a secret (never logged/returned).</summary>
public sealed record EmailChannelConfig(
    bool Enabled = false,
    string Host = "",
    int Port = 587,
    bool UseSsl = true,
    string Username = "",
    string Password = "",
    string From = "",
    string To = "");

/// <summary>ntfy.sh (or self-hosted) push. Optional bearer token is a secret.</summary>
public sealed record NtfyChannelConfig(
    bool Enabled = false,
    string ServerUrl = "https://ntfy.sh",
    string Topic = "",
    string? Token = null);

/// <summary>SMS via a carrier email-to-SMS gateway (text-only). Reuses the email
/// channel's SMTP relay for transport, so it requires a configured
/// <see cref="EmailChannelConfig"/>.</summary>
public sealed record SmsChannelConfig(
    bool Enabled = false,
    string GatewayAddress = ""); // e.g. 5551234567@vtext.com

/// <summary>Full, persisted Alert configuration (INCLUDES secrets). Round-trips
/// through IPluginSettings as one JSON document. Immutable — updates replace the
/// whole record so the worker can read a stable snapshot without locking.</summary>
public sealed record AlertSettings(
    bool Enabled = false,
    IReadOnlyList<Watchword>? Watchwords = null,
    EmailChannelConfig? Email = null,
    NtfyChannelConfig? Ntfy = null,
    SmsChannelConfig? Sms = null,
    int CooldownSeconds = 300,
    int GlobalRateCapPer10Min = 20,
    bool AttachClip = false,
    int ClipMaxSeconds = 20,
    string? ZeusBaseUrl = null)
{
    /// <summary>Safe defaults: everything off, no watchwords. Used when nothing
    /// has been persisted yet so the worker never NREs on a null sub-record.</summary>
    public static AlertSettings Default { get; } = new();

    /// <summary>Null-coalesced accessors so callers never see a null sub-record.</summary>
    public IReadOnlyList<Watchword> EffectiveWatchwords => Watchwords ?? Array.Empty<Watchword>();
    public EmailChannelConfig EffectiveEmail => Email ?? new EmailChannelConfig();
    public NtfyChannelConfig EffectiveNtfy => Ntfy ?? new NtfyChannelConfig();
    public SmsChannelConfig EffectiveSms => Sms ?? new SmsChannelConfig();
}

/// <summary>What a channel is handed to deliver one alert. Carries the matched
/// watchword, the over's context, an optional deep link back into Zeus, and an
/// optional local clip path (email/SMS attach; null when clips are off or the
/// over had no audio). Contains NO secrets.</summary>
public sealed record AlertPayload(
    string WatchwordText,
    WatchwordType WatchwordType,
    string MatchedText,
    string SessionId,
    string SegmentId,
    DateTime HeardUtc,
    long FreqHz,
    string Band,
    string Mode,
    string? Transcript,
    string? Callsign,
    string? DeepLink,
    string? ClipPath)
{
    /// <summary>One-line subject ("Voyeur: KB2UKA heard on 20m").</summary>
    public string Subject =>
        $"Voyeur alert: {MatchedText} heard" + (string.IsNullOrEmpty(Band) ? "" : $" on {Band}");

    /// <summary>Plain-text body shared by every channel. Never includes secrets.</summary>
    public string BodyText()
    {
        var lines = new List<string>
        {
            $"Watchword \"{WatchwordText}\" matched: {MatchedText}",
            $"Heard: {HeardUtc:yyyy-MM-dd HH:mm:ss} UTC",
            $"Frequency: {FreqHz / 1_000_000.0:F3} MHz" + (string.IsNullOrEmpty(Band) ? "" : $" ({Band})") +
                (string.IsNullOrEmpty(Mode) ? "" : $" {Mode}"),
        };
        if (!string.IsNullOrWhiteSpace(Callsign)) lines.Add($"Callsign: {Callsign}");
        if (!string.IsNullOrWhiteSpace(Transcript)) lines.Add($"Over: {Transcript!.Trim()}");
        if (!string.IsNullOrWhiteSpace(DeepLink)) lines.Add($"Open in Zeus: {DeepLink}");
        return string.Join("\n", lines);
    }
}

// ---- API wire shapes (redacted for GET, secret-optional for PUT) -------------

/// <summary>GET response: the config WITHOUT secrets. Presence of a stored secret
/// is surfaced as a boolean so the UI can show "password set" without ever
/// receiving it.</summary>
public sealed record AlertConfigDto(
    bool Enabled,
    IReadOnlyList<Watchword> Watchwords,
    AlertEmailDto Email,
    AlertNtfyDto Ntfy,
    SmsChannelConfig Sms,
    int CooldownSeconds,
    int GlobalRateCapPer10Min,
    bool AttachClip,
    int ClipMaxSeconds,
    string? ZeusBaseUrl)
{
    public static AlertConfigDto From(AlertSettings s)
    {
        var e = s.EffectiveEmail;
        var n = s.EffectiveNtfy;
        return new AlertConfigDto(
            s.Enabled,
            s.EffectiveWatchwords,
            new AlertEmailDto(e.Enabled, e.Host, e.Port, e.UseSsl, e.Username, e.From, e.To,
                HasPassword: !string.IsNullOrEmpty(e.Password)),
            new AlertNtfyDto(n.Enabled, n.ServerUrl, n.Topic, HasToken: !string.IsNullOrEmpty(n.Token)),
            s.EffectiveSms,
            s.CooldownSeconds,
            s.GlobalRateCapPer10Min,
            s.AttachClip,
            s.ClipMaxSeconds,
            s.ZeusBaseUrl);
    }
}

public sealed record AlertEmailDto(
    bool Enabled, string Host, int Port, bool UseSsl, string Username,
    string From, string To, bool HasPassword);

public sealed record AlertNtfyDto(
    bool Enabled, string ServerUrl, string Topic, bool HasToken);

/// <summary>PUT request. Secret fields (<see cref="EmailPassword"/>,
/// <see cref="NtfyToken"/>) are nullable: null keeps the stored secret, a
/// non-null value (incl. empty string) replaces it. Non-secret fields are
/// nullable too so a partial update only changes what it sends.</summary>
public sealed record AlertConfigUpdate(
    bool? Enabled = null,
    IReadOnlyList<Watchword>? Watchwords = null,
    AlertEmailUpdate? Email = null,
    AlertNtfyUpdate? Ntfy = null,
    SmsChannelConfig? Sms = null,
    int? CooldownSeconds = null,
    int? GlobalRateCapPer10Min = null,
    bool? AttachClip = null,
    int? ClipMaxSeconds = null,
    string? ZeusBaseUrl = null);

public sealed record AlertEmailUpdate(
    bool? Enabled = null, string? Host = null, int? Port = null, bool? UseSsl = null,
    string? Username = null, string? Password = null, string? From = null, string? To = null);

public sealed record AlertNtfyUpdate(
    bool? Enabled = null, string? ServerUrl = null, string? Topic = null, string? Token = null);

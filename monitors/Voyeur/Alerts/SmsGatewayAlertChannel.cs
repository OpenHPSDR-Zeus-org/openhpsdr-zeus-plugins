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

using System.Net.Mail;

namespace Zeus.Server.Voyeur.Alerts;

/// <summary>
/// SMS delivery via a carrier email-to-SMS gateway (free, text-only) — e.g.
/// <c>5551234567@vtext.com</c> (Verizon), <c>@txt.att.net</c> (AT&amp;T),
/// <c>@tmomail.net</c> (T-Mobile). It rides the operator's EMAIL SMTP relay, so
/// it requires a configured <see cref="EmailChannelConfig"/>; the only extra
/// setting is the gateway address. No carrier API, no Twilio, no cost.
///
/// Gateways drop attachments and truncate long bodies, so this sends a SHORT,
/// text-only message and never attaches the clip. Reuses
/// <see cref="EmailAlertChannel"/>'s message builder + transport seam, so it is
/// equally socket-free in tests.
/// </summary>
public sealed class SmsGatewayAlertChannel : IAlertChannel
{
    public string Name => "sms";

    private readonly EmailAlertChannel.SmtpTransport _transport;

    public SmsGatewayAlertChannel() : this(EmailAlertChannel.DefaultTransportAsync) { }

    // Test/seam ctor.
    internal SmsGatewayAlertChannel(EmailAlertChannel.SmtpTransport transport) => _transport = transport;

    public bool IsConfigured(AlertSettings settings)
    {
        var sms = settings.EffectiveSms;
        var email = settings.EffectiveEmail;
        // Needs the gateway address AND a usable SMTP relay (From + Host) to send
        // through. The email channel itself need not be enabled — SMS can ride a
        // relay the operator only uses for texts.
        return sms.Enabled
            && !string.IsNullOrWhiteSpace(sms.GatewayAddress)
            && !string.IsNullOrWhiteSpace(email.Host)
            && email.Port > 0
            && !string.IsNullOrWhiteSpace(email.From);
    }

    public Task SendAsync(AlertPayload payload, AlertSettings settings, CancellationToken ct)
    {
        var cfg = settings.EffectiveEmail;
        var sms = settings.EffectiveSms;
        // Text-only, short, no attachment, no link clutter (gateways mangle URLs).
        var body = ShortBody(payload);
        var msg = EmailAlertChannel.BuildMessage(cfg, sms.GatewayAddress, payload.Subject, body, attachmentPath: null);
        return _transport(msg, cfg, ct);
    }

    // A compact one/two-liner suited to a 160-char SMS segment.
    internal static string ShortBody(AlertPayload p)
    {
        var freq = $"{p.FreqHz / 1_000_000.0:F3}MHz";
        var who = string.IsNullOrWhiteSpace(p.Callsign) ? p.MatchedText : p.Callsign!;
        return $"Voyeur: {who} heard on {freq}" +
               (string.IsNullOrEmpty(p.Band) ? "" : $" ({p.Band})") +
               $" at {p.HeardUtc:HH:mm}Z";
    }
}

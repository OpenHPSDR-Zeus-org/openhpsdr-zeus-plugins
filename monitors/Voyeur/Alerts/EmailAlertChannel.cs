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

using System.Net;
using System.Net.Mail;

namespace Zeus.Server.Voyeur.Alerts;

/// <summary>
/// Email delivery via the operator's OWN SMTP relay, using the BCL
/// <see cref="SmtpClient"/> — no new NuGet (MailKit was rejected for a live
/// advisory). Attaches the capped clip when present. The SMTP password lives
/// only in <see cref="EmailChannelConfig"/> and is never logged.
///
/// Testability: the actual transmit is behind <see cref="_transport"/>, an
/// internal seam that defaults to a real <see cref="SmtpClient"/> send. Tests
/// inject a fake to capture the composed <see cref="MailMessage"/> WITHOUT
/// opening a socket, so message construction + secret handling are verified with
/// no network.
/// </summary>
public sealed class EmailAlertChannel : IAlertChannel
{
    public string Name => "email";

    internal delegate Task SmtpTransport(MailMessage message, EmailChannelConfig cfg, CancellationToken ct);

    private readonly SmtpTransport _transport;

    public EmailAlertChannel() : this(DefaultTransportAsync) { }

    // Test/seam ctor.
    internal EmailAlertChannel(SmtpTransport transport) => _transport = transport;

    public bool IsConfigured(AlertSettings settings)
    {
        var e = settings.EffectiveEmail;
        return e.Enabled
            && !string.IsNullOrWhiteSpace(e.Host)
            && e.Port > 0
            && !string.IsNullOrWhiteSpace(e.From)
            && !string.IsNullOrWhiteSpace(e.To);
    }

    public Task SendAsync(AlertPayload payload, AlertSettings settings, CancellationToken ct)
    {
        var cfg = settings.EffectiveEmail;
        var msg = BuildMessage(cfg, cfg.To, payload.Subject, payload.BodyText(), payload.ClipPath);
        return _transport(msg, cfg, ct);
    }

    // Shared message builder (also used by the SMS gateway channel).
    internal static MailMessage BuildMessage(
        EmailChannelConfig cfg, string to, string subject, string body, string? attachmentPath)
    {
        var msg = new MailMessage
        {
            From = new MailAddress(cfg.From),
            Subject = subject,
            Body = body,
            IsBodyHtml = false,
        };
        foreach (var addr in to.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            msg.To.Add(addr);

        if (!string.IsNullOrEmpty(attachmentPath) && File.Exists(attachmentPath))
            msg.Attachments.Add(new Attachment(attachmentPath));

        return msg;
    }

    // Shared real SMTP send — reused by the SMS-gateway channel (same relay).
    internal static async Task DefaultTransportAsync(MailMessage message, EmailChannelConfig cfg, CancellationToken ct)
    {
        using var client = new SmtpClient(cfg.Host, cfg.Port)
        {
            EnableSsl = cfg.UseSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
        };
        // Unauthenticated relays are valid (empty username) — only set creds when
        // a username is provided. The password never leaves this object.
        client.Credentials = string.IsNullOrEmpty(cfg.Username)
            ? CredentialCache.DefaultNetworkCredentials
            : new NetworkCredential(cfg.Username, cfg.Password);

        try
        {
            // SmtpClient.SendMailAsync predates CancellationToken; honour ct by
            // racing it so a wedged relay can't pin the alert worker forever.
            // The delay is linked to a CTS we cancel in finally so the Task.Delay
            // registration on the long-lived plugin token never leaks per send.
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var send = client.SendMailAsync(message);
            var done = await Task.WhenAny(send, Task.Delay(Timeout.Infinite, linked.Token)).ConfigureAwait(false);
            if (done != send) { client.SendAsyncCancel(); ct.ThrowIfCancellationRequested(); }
            await send.ConfigureAwait(false); // surface any send exception
        }
        finally
        {
            // Always dispose so any attachment's file handle is released and the
            // caller can delete the temp clip (Windows holds the lock otherwise).
            message.Dispose();
        }
    }
}

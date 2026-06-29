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
/// One delivery transport for a Watchword Alert (email / ntfy push / SMS
/// gateway). Channels are pure transports: they receive the immutable
/// <see cref="AlertPayload"/> plus the current <see cref="AlertSettings"/>
/// (which holds their config + secrets) and send. They open NO sockets in unit
/// tests — the service is exercised against a fake channel, and each real channel
/// has an injectable transport seam so its message construction is testable
/// without a network. Channels MUST NOT log secrets and SHOULD let exceptions
/// propagate; the service isolates and retries per channel.
/// </summary>
public interface IAlertChannel
{
    /// <summary>Stable channel id for logging / the test endpoint ("email" |
    /// "ntfy" | "sms").</summary>
    string Name { get; }

    /// <summary>True when this channel is enabled AND has the fields it needs to
    /// send. The service skips channels that aren't configured.</summary>
    bool IsConfigured(AlertSettings settings);

    /// <summary>Deliver one alert. Throws on failure (the service catches +
    /// retries with isolation). Honours <paramref name="ct"/>.</summary>
    Task SendAsync(AlertPayload payload, AlertSettings settings, CancellationToken ct);
}

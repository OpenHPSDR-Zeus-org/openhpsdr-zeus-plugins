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

using System.Text.Json;
using Xunit;
using Zeus.Server.Voyeur.Alerts;

namespace Openhpsdr.Zeus.Plugins.Voyeur.Tests.Alerts;

/// <summary>
/// The full <see cref="AlertSettings"/> document (INCLUDING secrets) round-trips
/// through System.Text.Json unchanged — this is the contract the host's
/// IPluginSettings store relies on. Covers watchwords (with the enum type),
/// the SMTP password + ntfy token secrets, and the scalar knobs.
/// </summary>
public class AlertSettingsRoundTripTests
{
    [Fact]
    public void Full_settings_round_trip_preserves_watchwords_secrets_and_enum()
    {
        var original = new AlertSettings(
            Enabled: true,
            Watchwords: new List<Watchword>
            {
                new("KB2UKA", WatchwordType.Callsign, true),
                new("Doug", WatchwordType.Keyword, false),
            },
            Email: new EmailChannelConfig(
                Enabled: true, Host: "smtp.example.com", Port: 465, UseSsl: true,
                Username: "user", Password: "super-secret-pw", From: "a@b.com", To: "c@d.com"),
            Ntfy: new NtfyChannelConfig(
                Enabled: true, ServerUrl: "https://ntfy.sh", Topic: "voyeur", Token: "tok-abc-123"),
            Sms: new SmsChannelConfig(Enabled: true, GatewayAddress: "5551234567@vtext.com"),
            CooldownSeconds: 120,
            GlobalRateCapPer10Min: 7,
            AttachClip: true,
            ClipMaxSeconds: 15,
            ZeusBaseUrl: "http://host:6060");

        var json = JsonSerializer.Serialize(original);
        var rt = JsonSerializer.Deserialize<AlertSettings>(json);
        Assert.NotNull(rt);

        Assert.Equal(original.Enabled, rt!.Enabled);
        Assert.Equal(original.CooldownSeconds, rt.CooldownSeconds);
        Assert.Equal(original.GlobalRateCapPer10Min, rt.GlobalRateCapPer10Min);
        Assert.Equal(original.AttachClip, rt.AttachClip);
        Assert.Equal(original.ClipMaxSeconds, rt.ClipMaxSeconds);
        Assert.Equal(original.ZeusBaseUrl, rt.ZeusBaseUrl);

        // Channel sub-records (record value-equality covers every field incl. the
        // secrets) — and assert the secrets survived explicitly.
        Assert.Equal(original.EffectiveEmail, rt.EffectiveEmail);
        Assert.Equal("super-secret-pw", rt.EffectiveEmail.Password);
        Assert.Equal(original.EffectiveNtfy, rt.EffectiveNtfy);
        Assert.Equal("tok-abc-123", rt.EffectiveNtfy.Token);
        Assert.Equal(original.EffectiveSms, rt.EffectiveSms);

        // Watchwords element-wise (Watchword is a record → equality includes the
        // WatchwordType enum and the Enabled flag).
        Assert.Equal(original.EffectiveWatchwords.Count, rt.EffectiveWatchwords.Count);
        for (int i = 0; i < original.EffectiveWatchwords.Count; i++)
            Assert.Equal(original.EffectiveWatchwords[i], rt.EffectiveWatchwords[i]);

        Assert.Equal(WatchwordType.Callsign, rt.EffectiveWatchwords[0].Type);
        Assert.Equal(WatchwordType.Keyword, rt.EffectiveWatchwords[1].Type);
    }
}

// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;
using Zeus.Server.Voyeur.Alerts;
using Xunit;

namespace Openhpsdr.Zeus.Plugins.Voyeur.Tests.Alerts;

public class AlertModelsTests
{
    private static AlertPayload Sample(string? deepLink = null) => new(
        WatchwordText: "Doug",
        WatchwordType: WatchwordType.Keyword,
        MatchedText: "Doug",
        SessionId: "sess",
        SegmentId: "seg",
        HeardUtc: new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
        FreqHz: 14_250_000,
        Band: "20m",
        Mode: "USB",
        Transcript: "Good morning Doug",
        Callsign: "KB2UKA",
        DeepLink: deepLink,
        ClipPath: null);

    [Fact]
    public void Subject_includes_match_and_band()
    {
        Assert.Contains("Doug", Sample().Subject);
        Assert.Contains("20m", Sample().Subject);
    }

    [Fact]
    public void BodyText_includes_context_and_deeplink()
    {
        var body = Sample("https://zeus.local/#/x").BodyText();
        Assert.Contains("Doug", body);
        Assert.Contains("14.250 MHz", body);
        Assert.Contains("KB2UKA", body);
        Assert.Contains("Good morning Doug", body);
        Assert.Contains("https://zeus.local/#/x", body);
    }

    [Fact]
    public void BuildDeepLink_null_base_returns_null()
        => Assert.Null(VoyeurAlertService.BuildDeepLink(null, "s", "g"));

    [Fact]
    public void BuildDeepLink_trims_slash_and_encodes()
    {
        var link = VoyeurAlertService.BuildDeepLink("https://host/", "s 1", "g/2");
        Assert.NotNull(link);
        Assert.StartsWith("https://host/#/plugins/com.kb2uka.voyeur", link);
        Assert.Contains("session=s%201", link);
        Assert.Contains("segment=g%2F2", link);
        Assert.DoesNotContain("host//", link);
    }

    // ---- secret handling / redaction ----

    private static AlertSettings WithSecrets() => AlertSettings.Default with
    {
        Email = new EmailChannelConfig(Enabled: true, Host: "smtp.example.com", Port: 587,
            Username: "user", Password: "SUPERSECRET", From: "a@b.c", To: "d@e.f"),
        Ntfy = new NtfyChannelConfig(Enabled: true, Topic: "mytopic", Token: "TOKENSECRET"),
    };

    [Fact]
    public void ConfigDto_reports_secret_presence_without_exposing_it()
    {
        var dto = AlertConfigDto.From(WithSecrets());
        Assert.True(dto.Email.HasPassword);
        Assert.True(dto.Ntfy.HasToken);
        Assert.False(AlertConfigDto.From(AlertSettings.Default).Email.HasPassword);
    }

    [Fact]
    public void ConfigDto_json_never_contains_secrets()
    {
        var json = JsonSerializer.Serialize(AlertConfigDto.From(WithSecrets()));
        Assert.DoesNotContain("SUPERSECRET", json);
        Assert.DoesNotContain("TOKENSECRET", json);
        // non-secret fields still present
        Assert.Contains("smtp.example.com", json);
        Assert.Contains("mytopic", json);
    }

    [Fact]
    public void ConfigDto_has_no_password_or_token_property()
    {
        Assert.Null(typeof(AlertEmailDto).GetProperty("Password"));
        Assert.Null(typeof(AlertNtfyDto).GetProperty("Token"));
    }

    [Fact]
    public void Merge_null_secret_keeps_stored_value()
    {
        var cur = WithSecrets();
        var merged = VoyeurAlertService.Merge(cur,
            new AlertConfigUpdate(Email: new AlertEmailUpdate(Host: "new.host", Password: null)));
        Assert.Equal("new.host", merged.EffectiveEmail.Host);
        Assert.Equal("SUPERSECRET", merged.EffectiveEmail.Password); // preserved
    }

    [Fact]
    public void Merge_nonnull_secret_replaces_value()
    {
        var merged = VoyeurAlertService.Merge(WithSecrets(),
            new AlertConfigUpdate(Email: new AlertEmailUpdate(Password: "NEWPASS")));
        Assert.Equal("NEWPASS", merged.EffectiveEmail.Password);
    }

    [Fact]
    public void Merge_empty_string_secret_clears_value()
    {
        var merged = VoyeurAlertService.Merge(WithSecrets(),
            new AlertConfigUpdate(Ntfy: new AlertNtfyUpdate(Token: "")));
        Assert.Equal("", merged.EffectiveNtfy.Token);
    }

    [Fact]
    public void Merge_leaves_unspecified_fields_unchanged()
    {
        var cur = WithSecrets() with { Enabled = true, CooldownSeconds = 123 };
        var merged = VoyeurAlertService.Merge(cur, new AlertConfigUpdate(AttachClip: true));
        Assert.True(merged.Enabled);
        Assert.Equal(123, merged.CooldownSeconds);
        Assert.True(merged.AttachClip);
        Assert.Equal("smtp.example.com", merged.EffectiveEmail.Host);
    }
}

// SPDX-License-Identifier: GPL-2.0-or-later

using Zeus.Server.Voyeur.Alerts;
using Xunit;

namespace Openhpsdr.Zeus.Plugins.Voyeur.Tests.Alerts;

public class VoyeurAlertServiceTests
{
    private static Watchword Kw(string t) => new(t, WatchwordType.Keyword);

    private sealed class Harness
    {
        public FakeOverContextSource Overs { get; } = new();
        public FakePluginSettings Settings { get; } = new();
        public CapturingLogger Logger { get; } = new();
        public List<FakeAlertChannel> Channels { get; }
        public VoyeurAlertService Service { get; }
        public DateTime Now { get; set; } = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public Harness(params FakeAlertChannel[] channels)
        {
            Channels = channels.ToList();
            Service = new VoyeurAlertService(Overs, Settings, Channels, Logger, () => Now);
        }
    }

    private static AlertConfigUpdate Enable(params Watchword[] ww) =>
        new(Enabled: true, Watchwords: ww, CooldownSeconds: 0, GlobalRateCapPer10Min: 0);

    [Fact]
    public async Task Fans_out_a_match_to_all_configured_channels()
    {
        var a = new FakeAlertChannel("a");
        var b = new FakeAlertChannel("b");
        var h = new Harness(a, b);
        await h.Service.UpdateConfigAsync(Enable(Kw("Doug")));

        await h.Service.ProcessForTestAsync("s", "g", "good morning Doug", null, default);

        Assert.Single(a.Sent);
        Assert.Single(b.Sent);
        Assert.Equal("Doug", a.Sent[0].MatchedText);
    }

    [Fact]
    public async Task Skips_unconfigured_channels()
    {
        var on = new FakeAlertChannel("on", configured: true);
        var off = new FakeAlertChannel("off", configured: false);
        var h = new Harness(on, off);
        await h.Service.UpdateConfigAsync(Enable(Kw("Doug")));

        await h.Service.ProcessForTestAsync("s", "g", "Doug here", null, default);

        Assert.Single(on.Sent);
        Assert.Empty(off.Sent);
    }

    [Fact]
    public async Task No_match_sends_nothing()
    {
        var a = new FakeAlertChannel();
        var h = new Harness(a);
        await h.Service.UpdateConfigAsync(Enable(Kw("Doug")));

        await h.Service.ProcessForTestAsync("s", "g", "completely unrelated chatter", null, default);

        Assert.Empty(a.Sent);
    }

    [Fact]
    public async Task Disabled_sends_nothing()
    {
        var a = new FakeAlertChannel();
        var h = new Harness(a);
        await h.Service.UpdateConfigAsync(new AlertConfigUpdate(Enabled: false, Watchwords: new[] { Kw("Doug") }));

        await h.Service.ProcessForTestAsync("s", "g", "Doug Doug Doug", null, default);

        Assert.Empty(a.Sent);
    }

    [Fact]
    public async Task Failing_channel_is_isolated_from_the_rest()
    {
        var bad = new FakeAlertChannel("bad", throwFirst: 5); // never succeeds
        var good = new FakeAlertChannel("good");
        var h = new Harness(bad, good);
        await h.Service.UpdateConfigAsync(Enable(Kw("Doug")));

        // Must not throw despite the bad channel failing.
        await h.Service.ProcessForTestAsync("s", "g", "Doug", null, default);

        Assert.Empty(bad.Sent);
        Assert.Single(good.Sent);
    }

    [Fact]
    public async Task Channel_send_is_retried_then_succeeds()
    {
        var flaky = new FakeAlertChannel("flaky", throwFirst: 1); // first attempt throws
        var h = new Harness(flaky);
        await h.Service.UpdateConfigAsync(Enable(Kw("Doug")));

        await h.Service.ProcessForTestAsync("s", "g", "Doug", null, default);

        Assert.Equal(2, flaky.Attempts);
        Assert.Single(flaky.Sent);
    }

    [Fact]
    public async Task Cooldown_suppresses_a_rapid_repeat()
    {
        var a = new FakeAlertChannel();
        var h = new Harness(a);
        await h.Service.UpdateConfigAsync(new AlertConfigUpdate(
            Enabled: true, Watchwords: new[] { Kw("Doug") },
            CooldownSeconds: 300, GlobalRateCapPer10Min: 0));

        await h.Service.ProcessForTestAsync("s", "g1", "Doug", null, default);
        h.Now = h.Now.AddSeconds(10); // within cooldown
        await h.Service.ProcessForTestAsync("s", "g2", "Doug", null, default);

        Assert.Single(a.Sent); // second suppressed
    }

    [Fact]
    public async Task Attaches_clip_when_enabled_and_audio_present()
    {
        var a = new FakeAlertChannel();
        var h = new Harness(a);
        h.Overs.AudioPath = TestWav.WriteSilence(
            Path.Combine(Path.GetTempPath(), "voyeur-svc-" + Guid.NewGuid().ToString("N") + ".wav"), 4);
        try
        {
            await h.Service.UpdateConfigAsync(new AlertConfigUpdate(
                Enabled: true, Watchwords: new[] { Kw("Doug") },
                CooldownSeconds: 0, GlobalRateCapPer10Min: 0, AttachClip: true, ClipMaxSeconds: 2));

            await h.Service.ProcessForTestAsync("s", "g", "Doug", null, default);

            Assert.Single(a.Sent);
            Assert.False(string.IsNullOrEmpty(a.Sent[0].ClipPath));
        }
        finally
        {
            if (h.Overs.AudioPath is not null) File.Delete(h.Overs.AudioPath);
        }
    }

    [Fact]
    public async Task No_clip_when_attach_disabled()
    {
        var a = new FakeAlertChannel();
        var h = new Harness(a);
        await h.Service.UpdateConfigAsync(Enable(Kw("Doug")));

        await h.Service.ProcessForTestAsync("s", "g", "Doug", null, default);

        Assert.Single(a.Sent);
        Assert.Null(a.Sent[0].ClipPath);
    }

    // ---- config persistence + redaction end-to-end ----

    [Fact]
    public async Task Update_persists_secret_and_get_redacts_it()
    {
        var h = new Harness(new FakeAlertChannel());
        await h.Service.UpdateConfigAsync(new AlertConfigUpdate(
            Email: new AlertEmailUpdate(Enabled: true, Host: "smtp.x", Port: 587,
                From: "a@b", To: "c@d", Password: "SECRET")));

        var dto = await h.Service.GetConfigAsync();
        Assert.True(dto.Email.HasPassword);

        // Persisted document keeps the secret (so the relay still works)...
        var stored = h.Settings.Peek<AlertSettings>(VoyeurAlertService.SettingsKey);
        Assert.NotNull(stored);
        Assert.Equal("SECRET", stored!.EffectiveEmail.Password);
    }

    [Fact]
    public async Task Update_with_null_password_keeps_stored_secret()
    {
        var h = new Harness(new FakeAlertChannel());
        await h.Service.UpdateConfigAsync(new AlertConfigUpdate(
            Email: new AlertEmailUpdate(Host: "smtp.x", From: "a@b", To: "c@d", Password: "SECRET")));

        // A later update that changes only the host must NOT wipe the password.
        await h.Service.UpdateConfigAsync(new AlertConfigUpdate(
            Email: new AlertEmailUpdate(Host: "smtp.y", Password: null)));

        var stored = h.Settings.Peek<AlertSettings>(VoyeurAlertService.SettingsKey);
        Assert.Equal("smtp.y", stored!.EffectiveEmail.Host);
        Assert.Equal("SECRET", stored.EffectiveEmail.Password);
    }

    [Fact]
    public async Task Secret_is_never_written_to_the_log()
    {
        var a = new FakeAlertChannel();
        var h = new Harness(a);
        await h.Service.UpdateConfigAsync(new AlertConfigUpdate(
            Enabled: true, Watchwords: new[] { Kw("Doug") }, CooldownSeconds: 0, GlobalRateCapPer10Min: 0,
            Email: new AlertEmailUpdate(Enabled: true, Host: "smtp.x", From: "a@b", To: "c@d", Password: "LOGLEAK")));

        await h.Service.ProcessForTestAsync("s", "g", "Doug", null, default);

        Assert.All(h.Logger.Messages, msg => Assert.DoesNotContain("LOGLEAK", msg));
    }

    [Fact]
    public async Task Test_reports_per_channel_results()
    {
        var ok = new FakeAlertChannel("ok");
        var bad = new FakeAlertChannel("bad", throwFirst: 1);
        var notcfg = new FakeAlertChannel("notcfg", configured: false);
        var h = new Harness(ok, bad, notcfg);
        await h.Service.UpdateConfigAsync(Enable());

        var results = await h.Service.TestAsync(channelName: null);

        Assert.Equal(3, results.Count);
        Assert.True(results.Single(r => r.Channel == "ok").Ok);
        Assert.False(results.Single(r => r.Channel == "bad").Ok);   // test does not retry
        Assert.False(results.Single(r => r.Channel == "notcfg").Ok);
        Assert.Equal("not configured", results.Single(r => r.Channel == "notcfg").Detail);
    }

    [Fact]
    public async Task OnTranscribed_with_no_text_or_call_is_dropped_cheaply()
    {
        var a = new FakeAlertChannel();
        var h = new Harness(a);
        await h.Service.UpdateConfigAsync(Enable(Kw("Doug")));

        h.Service.OnTranscribed("s", "g", null, null); // nothing to match → no enqueue/work
        Assert.Empty(a.Sent);
    }
}

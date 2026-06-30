// SPDX-License-Identifier: GPL-2.0-or-later

using System.Net;
using System.Net.Http;
using System.Net.Mail;
using Zeus.Server.Voyeur.Alerts;
using Xunit;

namespace Openhpsdr.Zeus.Plugins.Voyeur.Tests.Alerts;

public class AlertChannelTests
{
    private static AlertPayload Payload() => new(
        WatchwordText: "Doug", WatchwordType: WatchwordType.Keyword, MatchedText: "Doug",
        SessionId: "sess", SegmentId: "seg", HeardUtc: new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
        FreqHz: 14_250_000, Band: "20m", Mode: "USB",
        Transcript: "hi Doug", Callsign: "KB2UKA",
        DeepLink: "https://zeus.local/#/x", ClipPath: null);

    // ---- email ----

    [Fact]
    public async Task Email_builds_message_without_leaking_password()
    {
        MailMessage? captured = null;
        var ch = new EmailAlertChannel((msg, cfg, ct) => { captured = msg; return Task.CompletedTask; });

        var settings = AlertSettings.Default with
        {
            Email = new EmailChannelConfig(Enabled: true, Host: "smtp.x", Port: 587,
                Username: "u", Password: "SUPERSECRET", From: "from@x", To: "to@y"),
        };
        Assert.True(ch.IsConfigured(settings));

        await ch.SendAsync(Payload(), settings, default);

        Assert.NotNull(captured);
        Assert.Equal("from@x", captured!.From!.Address);
        Assert.Contains(captured.To, a => a.Address == "to@y");
        Assert.DoesNotContain("SUPERSECRET", captured.Subject);
        Assert.DoesNotContain("SUPERSECRET", captured.Body);
    }

    [Fact]
    public void Email_not_configured_when_disabled_or_missing_fields()
    {
        var ch = new EmailAlertChannel((_, _, _) => Task.CompletedTask);
        Assert.False(ch.IsConfigured(AlertSettings.Default));
        Assert.False(ch.IsConfigured(AlertSettings.Default with
        {
            Email = new EmailChannelConfig(Enabled: true, Host: "", From: "a", To: "b"),
        }));
    }

    // ---- ntfy ----

    [Fact]
    public async Task Ntfy_posts_to_topic_with_title_and_bearer()
    {
        var handler = new CapturingHandler();
        var ch = new NtfyAlertChannel(new HttpClient(handler));
        var settings = AlertSettings.Default with
        {
            Ntfy = new NtfyChannelConfig(Enabled: true, ServerUrl: "https://ntfy.sh", Topic: "mytopic", Token: "TOK"),
        };
        Assert.True(ch.IsConfigured(settings));

        await ch.SendAsync(Payload(), settings, default);

        Assert.NotNull(handler.Request);
        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("https://ntfy.sh/mytopic", handler.Request.RequestUri!.ToString());
        Assert.True(handler.Request.Headers.TryGetValues("X-Title", out var title));
        Assert.Contains("Doug", string.Join("", title!));
        Assert.Equal("Bearer", handler.Request.Headers.Authorization!.Scheme);
        Assert.Equal("TOK", handler.Request.Headers.Authorization.Parameter);
        Assert.Contains("hi Doug", handler.Body);
    }

    [Fact]
    public async Task Ntfy_omits_authorization_without_token()
    {
        var handler = new CapturingHandler();
        var ch = new NtfyAlertChannel(new HttpClient(handler));
        var settings = AlertSettings.Default with
        {
            Ntfy = new NtfyChannelConfig(Enabled: true, Topic: "t", Token: null),
        };
        await ch.SendAsync(Payload(), settings, default);
        Assert.Null(handler.Request!.Headers.Authorization);
    }

    [Fact]
    public async Task Ntfy_throws_on_non_success()
    {
        var handler = new CapturingHandler { Status = HttpStatusCode.InternalServerError };
        var ch = new NtfyAlertChannel(new HttpClient(handler));
        var settings = AlertSettings.Default with { Ntfy = new NtfyChannelConfig(Enabled: true, Topic: "t") };
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => ch.SendAsync(Payload(), settings, default));
    }

    // ---- sms ----

    [Fact]
    public async Task Sms_sends_short_text_only_to_gateway_via_smtp()
    {
        MailMessage? captured = null;
        var ch = new SmsGatewayAlertChannel((msg, cfg, ct) => { captured = msg; return Task.CompletedTask; });
        var settings = AlertSettings.Default with
        {
            Email = new EmailChannelConfig(Enabled: false, Host: "smtp.x", Port: 587, From: "from@x", Password: "PW"),
            Sms = new SmsChannelConfig(Enabled: true, GatewayAddress: "5551234@vtext.com"),
        };
        Assert.True(ch.IsConfigured(settings)); // rides SMTP even though email channel is disabled

        await ch.SendAsync(Payload() with { ClipPath = "/tmp/should-be-ignored.wav" }, settings, default);

        Assert.NotNull(captured);
        Assert.Contains(captured!.To, a => a.Address == "5551234@vtext.com");
        Assert.Empty(captured.Attachments);                       // gateways drop attachments
        Assert.DoesNotContain("PW", captured.Body);
    }

    [Fact]
    public void Sms_not_configured_without_relay()
    {
        var ch = new SmsGatewayAlertChannel((_, _, _) => Task.CompletedTask);
        var settings = AlertSettings.Default with { Sms = new SmsChannelConfig(Enabled: true, GatewayAddress: "x@y") };
        Assert.False(ch.IsConfigured(settings)); // no SMTP host/from
    }

    [Fact]
    public void Sms_short_body_is_compact_and_text_only()
    {
        var body = SmsGatewayAlertChannel.ShortBody(Payload());
        Assert.Contains("KB2UKA", body);
        Assert.Contains("14.250MHz", body);
        Assert.DoesNotContain("\n", body);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string Body { get; private set; } = "";
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            if (request.Content is not null) Body = await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(Status);
        }
    }
}

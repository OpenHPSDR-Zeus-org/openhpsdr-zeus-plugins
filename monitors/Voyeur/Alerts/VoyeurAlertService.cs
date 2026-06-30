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

using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Zeus.Plugins.Contracts;

namespace Zeus.Server.Voyeur.Alerts;

/// <summary>Per-channel outcome of a manual test send.</summary>
public sealed record AlertTestResult(string Channel, bool Ok, string Detail);

/// <summary>
/// Watchword Alerts engine (Voyeur Mode, zeus-la5). Sits OFF the audio/DSP path
/// and OFF the transcription worker: <see cref="OnTranscribed"/> is the only
/// hot-path entry, and it just drops a job on a bounded single-reader queue and
/// returns instantly — exactly the channel/single-worker pattern
/// <see cref="VoyeurTranscriptionService"/> uses. A single background worker then
/// matches watchwords, applies per-watchword cooldown + a global rate cap, builds
/// the payload (optionally with a capped audio clip), and fans out to the
/// operator's FREE channels (email / ntfy / SMS gateway) with per-channel
/// isolation and bounded retry — one bad relay can never stall the queue or break
/// the other channels.
///
/// Secrets (SMTP password, ntfy token) live only in the persisted
/// <see cref="AlertSettings"/> document and are NEVER logged or returned; the
/// GET surface is the redacted <see cref="AlertConfigDto"/>.
/// </summary>
public sealed class VoyeurAlertService
{
    internal const string SettingsKey = "alerts.config";

    private readonly record struct AlertJob(
        string SessionId, string SegmentId, string? Transcript, string? Callsign);

    private readonly IOverContextSource _overs;
    private readonly IPluginSettings _pluginSettings;
    private readonly IReadOnlyList<IAlertChannel> _channels;
    private readonly ILogger _log;
    private readonly Func<DateTime> _now;

    // Cooldown + global rate-cap state. Touched only by the single worker (and by
    // the gate's own internal structures), so it needs no locking.
    private readonly AlertGate _gate = new();

    private readonly Channel<AlertJob> _queue = Channel.CreateBounded<AlertJob>(
        new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

    // Settings: a volatile immutable snapshot the worker reads lock-free; updates
    // (REST thread) serialise on _settingsLock and replace the whole reference.
    private volatile AlertSettings _settings = AlertSettings.Default;
    private readonly SemaphoreSlim _settingsLock = new(1, 1);
    private bool _loaded;

    public VoyeurAlertService(
        VoyeurStore store,
        IPluginSettings pluginSettings,
        IEnumerable<IAlertChannel> channels,
        ILogger log,
        Func<DateTime>? clock = null)
        : this(new VoyeurStoreContextSource(store), pluginSettings, channels, log, clock)
    {
    }

    // Test/seam ctor: the over-context source is abstracted so unit tests need no
    // real LiteDB store.
    internal VoyeurAlertService(
        IOverContextSource overs,
        IPluginSettings pluginSettings,
        IEnumerable<IAlertChannel> channels,
        ILogger log,
        Func<DateTime>? clock = null)
    {
        _overs = overs;
        _pluginSettings = pluginSettings;
        _channels = channels.ToArray();
        _log = log;
        _now = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>HOT-PATH ENTRY. Called from the transcription worker right after
    /// attribution. Enqueues and returns instantly; all real work happens on the
    /// alert worker. Drops cheaply when there is nothing to match on.</summary>
    public void OnTranscribed(string sessionId, string segmentId, string? transcript, string? callsign)
    {
        if (string.IsNullOrWhiteSpace(transcript) && string.IsNullOrWhiteSpace(callsign)) return;
        _queue.Writer.TryWrite(new AlertJob(sessionId, segmentId, transcript, callsign));
    }

    /// <summary>Run the single-worker alert loop until cancelled. The plugin entry
    /// starts this on a Task in InitializeAsync and cancels it in ShutdownAsync,
    /// mirroring the transcription worker.</summary>
    public async Task RunAsync(CancellationToken stoppingToken)
    {
        await EnsureLoadedAsync(stoppingToken).ConfigureAwait(false);
        await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await ProcessAsync(job, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A failed alert must never take the worker down.
                _log.LogWarning(ex, "voyeur.alerts job failed seg={Seg}", job.SegmentId);
            }
        }
    }

    // Test hook: drive one job through the full pipeline synchronously, bypassing
    // the queue/worker timing. Production goes through OnTranscribed → RunAsync.
    internal Task ProcessForTestAsync(
        string sessionId, string segmentId, string? transcript, string? callsign, CancellationToken ct)
        => ProcessAsync(new AlertJob(sessionId, segmentId, transcript, callsign), ct);

    private async Task ProcessAsync(AlertJob job, CancellationToken ct)
    {
        var settings = _settings; // stable snapshot
        if (!settings.Enabled) return;
        var watchwords = settings.EffectiveWatchwords;
        if (watchwords.Count == 0) return;
        if (!_channels.Any(c => c.IsConfigured(settings))) return; // nothing to send through

        // Build the candidate-callsign set: the attributed call + everything the
        // extractor finds in the transcript (CallsignExtractor-style compare).
        var calls = new List<string>();
        if (!string.IsNullOrWhiteSpace(job.Callsign)) calls.Add(job.Callsign!);
        calls.AddRange(CallsignExtractor.Extract(job.Transcript));

        var matches = WatchwordMatcher.Match(job.Transcript, calls, watchwords);
        if (matches.Count == 0) return;

        // Resolve the over's context once (off the hot path — a DB read here is
        // fine). Null-safe: a session deleted mid-flight just yields blanks.
        var ctx = _overs.GetContext(job.SessionId, job.SegmentId);
        var heard = ctx?.HeardUtc ?? _now();

        string? clipPath = null;
        bool clipAttempted = false;
        try
        {
            foreach (var match in matches)
            {
                ct.ThrowIfCancellationRequested();
                // Cooldown key is the canonical WATCHWORD (upper-invariant), not
                // the raw matched span — so every transcript phrasing that hits
                // the same watchword shares one cooldown bucket.
                var key = match.Watchword.Type + ":" + match.Watchword.Text.ToUpperInvariant();
                if (!_gate.TryFire(key, _now(), settings.CooldownSeconds, settings.GlobalRateCapPer10Min))
                {
                    _log.LogDebug("voyeur.alerts suppressed (cooldown/rate-cap) ww={Ww}", match.Watchword.Text);
                    continue;
                }

                // Make the clip once, lazily, only if we're actually firing.
                if (settings.AttachClip && !clipAttempted)
                {
                    clipAttempted = true;
                    clipPath = AlertClipMaker.TryMake(
                        _overs.GetSegmentAudioPath(job.SegmentId), settings.ClipMaxSeconds);
                }

                var payload = BuildPayload(match, job, ctx, heard, clipPath, settings);
                await FanOutAsync(payload, settings, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            if (clipPath is not null) TryDelete(clipPath);
        }
    }

    private async Task FanOutAsync(AlertPayload payload, AlertSettings settings, CancellationToken ct)
    {
        foreach (var ch in _channels)
        {
            if (!ch.IsConfigured(settings)) continue;
            await SendWithRetryAsync(ch, payload, settings, ct).ConfigureAwait(false);
        }
    }

    private async Task SendWithRetryAsync(
        IAlertChannel ch, AlertPayload payload, AlertSettings settings, CancellationToken ct)
    {
        const int maxAttempts = 2;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await ch.SendAsync(payload, settings, ct).ConfigureAwait(false);
                _log.LogInformation("voyeur.alerts sent ch={Ch} ww={Ww}", ch.Name, payload.WatchwordText);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // shutdown — stop, don't retry
            }
            catch (Exception ex)
            {
                if (attempt >= maxAttempts)
                {
                    // Isolation: log and move on so one channel can't break the rest.
                    _log.LogWarning(ex, "voyeur.alerts channel {Ch} failed after {N} attempts", ch.Name, maxAttempts);
                    return;
                }
                try { await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    // ---- manual test + config API ------------------------------------------

    /// <summary>Send a synthetic alert to the configured channels (or one named
    /// channel) to validate setup. Bypasses cooldown/rate-cap (operator-initiated)
    /// and never throws; per-channel outcomes are returned. Detail strings carry
    /// no secrets.</summary>
    public async Task<IReadOnlyList<AlertTestResult>> TestAsync(string? channelName, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        var settings = _settings;
        var payload = SamplePayload(settings);
        var results = new List<AlertTestResult>();
        foreach (var ch in _channels)
        {
            if (channelName is not null &&
                !string.Equals(ch.Name, channelName, StringComparison.OrdinalIgnoreCase)) continue;
            if (!ch.IsConfigured(settings)) { results.Add(new AlertTestResult(ch.Name, false, "not configured")); continue; }
            try
            {
                await ch.SendAsync(payload, settings, ct).ConfigureAwait(false);
                results.Add(new AlertTestResult(ch.Name, true, "sent"));
            }
            catch (Exception ex)
            {
                // Generic client-facing detail — the raw exception can carry the
                // relay host / topic URL / local clip path; keep it in the log only.
                _log.LogWarning(ex, "voyeur.alerts test send failed ch={Ch}", ch.Name);
                results.Add(new AlertTestResult(ch.Name, false, "send failed (check channel settings)"));
            }
        }
        return results;
    }

    /// <summary>Redacted config for GET (no secrets — booleans only).</summary>
    public async Task<AlertConfigDto> GetConfigAsync(CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        return AlertConfigDto.From(_settings);
    }

    /// <summary>Apply a partial update (secret fields left null keep the stored
    /// secret), persist it, and return the redacted view.</summary>
    public async Task<AlertConfigDto> UpdateConfigAsync(AlertConfigUpdate update, CancellationToken ct = default)
    {
        await _settingsLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_loaded) { _settings = await LoadOrDefaultAsync(ct).ConfigureAwait(false); _loaded = true; }
            var merged = Merge(_settings, update);
            await _pluginSettings.SetAsync(SettingsKey, merged, ct).ConfigureAwait(false);
            _settings = merged;
            return AlertConfigDto.From(merged);
        }
        finally
        {
            _settingsLock.Release();
        }
    }

    // ---- internals (testable) ----------------------------------------------

    internal static AlertSettings Merge(AlertSettings cur, AlertConfigUpdate u)
    {
        var e = cur.EffectiveEmail;
        var email = u.Email is null ? e : new EmailChannelConfig(
            Enabled: u.Email.Enabled ?? e.Enabled,
            Host: u.Email.Host ?? e.Host,
            Port: u.Email.Port ?? e.Port,
            UseSsl: u.Email.UseSsl ?? e.UseSsl,
            Username: u.Email.Username ?? e.Username,
            Password: u.Email.Password ?? e.Password,   // null KEEPS the stored secret
            From: u.Email.From ?? e.From,
            To: u.Email.To ?? e.To);

        var n = cur.EffectiveNtfy;
        var ntfy = u.Ntfy is null ? n : new NtfyChannelConfig(
            Enabled: u.Ntfy.Enabled ?? n.Enabled,
            ServerUrl: u.Ntfy.ServerUrl ?? n.ServerUrl,
            Topic: u.Ntfy.Topic ?? n.Topic,
            Token: u.Ntfy.Token ?? n.Token);            // null KEEPS the stored secret

        return cur with
        {
            Enabled = u.Enabled ?? cur.Enabled,
            Watchwords = u.Watchwords ?? cur.EffectiveWatchwords,
            Email = email,
            Ntfy = ntfy,
            Sms = u.Sms ?? cur.EffectiveSms,
            CooldownSeconds = u.CooldownSeconds ?? cur.CooldownSeconds,
            GlobalRateCapPer10Min = u.GlobalRateCapPer10Min ?? cur.GlobalRateCapPer10Min,
            AttachClip = u.AttachClip ?? cur.AttachClip,
            ClipMaxSeconds = u.ClipMaxSeconds ?? cur.ClipMaxSeconds,
            ZeusBaseUrl = u.ZeusBaseUrl ?? cur.ZeusBaseUrl,
        };
    }

    internal static string? BuildDeepLink(string? baseUrl, string sessionId, string segmentId)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return null;
        var b = baseUrl.TrimEnd('/');
        return $"{b}/#/plugins/com.kb2uka.voyeur?session={Uri.EscapeDataString(sessionId)}" +
               $"&segment={Uri.EscapeDataString(segmentId)}";
    }

    private static AlertPayload BuildPayload(
        WatchwordMatch m, AlertJob job, OverContext? ctx, DateTime heard,
        string? clipPath, AlertSettings settings) => new(
            WatchwordText: m.Watchword.Text,
            WatchwordType: m.Watchword.Type,
            MatchedText: m.MatchedText,
            SessionId: job.SessionId,
            SegmentId: job.SegmentId,
            HeardUtc: heard,
            FreqHz: ctx?.FreqHz ?? 0,
            Band: ctx?.Band ?? "",
            Mode: ctx?.Mode ?? "",
            Transcript: job.Transcript,
            Callsign: job.Callsign,
            DeepLink: BuildDeepLink(settings.ZeusBaseUrl, job.SessionId, job.SegmentId),
            ClipPath: clipPath);

    private static AlertPayload SamplePayload(AlertSettings settings) => new(
        WatchwordText: "TEST",
        WatchwordType: WatchwordType.Keyword,
        MatchedText: "TEST",
        SessionId: "test-session",
        SegmentId: "test-segment",
        HeardUtc: DateTime.UtcNow,
        FreqHz: 14_250_000,
        Band: "20m",
        Mode: "USB",
        Transcript: "This is a Voyeur watchword alert test.",
        Callsign: null,
        DeepLink: BuildDeepLink(settings.ZeusBaseUrl, "test-session", "test-segment"),
        ClipPath: null);

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_loaded) return;
        await _settingsLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_loaded) return;
            _settings = await LoadOrDefaultAsync(ct).ConfigureAwait(false);
            _loaded = true;
        }
        finally
        {
            _settingsLock.Release();
        }
    }

    private async Task<AlertSettings> LoadOrDefaultAsync(CancellationToken ct)
    {
        try
        {
            return await _pluginSettings.GetAsync<AlertSettings>(SettingsKey, ct).ConfigureAwait(false)
                   ?? AlertSettings.Default;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "voyeur.alerts settings load failed; using defaults");
            return AlertSettings.Default;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}

/// <summary>
/// Cooldown + global rate-cap gate. Single-threaded by construction (only the
/// alert worker calls it), so it needs no locking. Pure-ish: every decision is a
/// function of the supplied <c>now</c>, which makes it deterministically testable.
/// </summary>
internal sealed class AlertGate
{
    private readonly Dictionary<string, DateTime> _lastFired = new(StringComparer.Ordinal);
    private readonly Queue<DateTime> _recent = new(); // global window of recent fires

    /// <summary>True (and records the fire) when both the per-key cooldown and the
    /// global 10-minute cap allow it; false (records nothing) when suppressed.</summary>
    public bool TryFire(string key, DateTime now, int cooldownSeconds, int globalCapPer10Min)
    {
        // Prune the global window first.
        var windowStart = now - TimeSpan.FromMinutes(10);
        while (_recent.Count > 0 && _recent.Peek() < windowStart) _recent.Dequeue();

        if (globalCapPer10Min > 0 && _recent.Count >= globalCapPer10Min) return false;

        if (cooldownSeconds > 0 &&
            _lastFired.TryGetValue(key, out var last) &&
            (now - last) < TimeSpan.FromSeconds(cooldownSeconds))
            return false;

        _lastFired[key] = now;
        _recent.Enqueue(now);
        return true;
    }
}

/// <summary>The over context an alert needs: where/when it was heard.</summary>
internal readonly record struct OverContext(long FreqHz, string Band, string Mode, DateTime HeardUtc);

/// <summary>Abstracts the <see cref="VoyeurStore"/> reads the alert worker makes,
/// so unit tests can supply a fake instead of standing up a real LiteDB.</summary>
internal interface IOverContextSource
{
    OverContext? GetContext(string sessionId, string segmentId);
    string? GetSegmentAudioPath(string segmentId);
}

/// <summary>Production adapter over <see cref="VoyeurStore"/>.</summary>
internal sealed class VoyeurStoreContextSource : IOverContextSource
{
    private readonly VoyeurStore _store;
    public VoyeurStoreContextSource(VoyeurStore store) => _store = store;

    public OverContext? GetContext(string sessionId, string segmentId)
    {
        var detail = _store.GetSession(sessionId);
        if (detail is null) return null;
        var heard = detail.Segments.FirstOrDefault(s => s.Id == segmentId)?.StartedUtc
                    ?? detail.Session.StartedUtc;
        return new OverContext(detail.Session.FreqHz, detail.Session.Band, detail.Session.Mode, heard);
    }

    public string? GetSegmentAudioPath(string segmentId) => _store.GetSegmentAudioPath(segmentId);
}

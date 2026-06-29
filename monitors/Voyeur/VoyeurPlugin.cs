// SPDX-License-Identifier: GPL-2.0-or-later
//
// Voyeur Mode · Net Monitor — plugin entry for Openhpsdr-Zeus (zeus-la5).
//
// Extracted from the in-tree Zeus.Server.Hosting/Voyeur/* services. Behaviour
// and wire format are preserved verbatim so the existing panel calls and the
// operator's saved sessions/engines/models continue to work unchanged:
//
//   * RX audio arrives via the host RX tap (IRxAudioTapPlugin.OnRxAudio) and is
//     fed to VoyeurMonitorService.Feed — replacing the in-core direct
//     subscription to DspPipelineService.RxAudioAvailable. Receiver 0 only
//     (filtered on AudioBlockContext.Receiver) + idle-guard inside Feed.
//   * Per-session metadata (freq/mode/band) comes from IPluginContext.Radio.
//   * QRZ enrichment goes through IPluginContext.Qrz (host-mediated, reuses the
//     operator's stored credentials + rate limit).
//   * Sessions/segments persist to <appdata>/Zeus/voyeur.db and over audio to
//     ~/Downloads/zeus-voyeur — the SAME paths the in-core feature used, so an
//     upgrading operator's logs, engines, and models are adopted automatically.
//   * The whisper/llama engine + model downloads are unchanged (button-download
//     from the voyeur-engines-v1 release tag via VoyeurInstallService).
//
// The 16 endpoints are re-mapped under /api/plugins/com.kb2uka.voyeur/... ; the
// UI module repoints to that prefix.

using System.Net.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Audio;
using Zeus.Plugins.Contracts.Extensions;
using Zeus.Server;              // VoyeurMonitorService, BandUtils
using Zeus.Server.Voyeur;       // store / whisper / llama / install / transcription / DTOs
using Zeus.Server.Voyeur.Alerts; // watchword alerts service + channels + DTOs

namespace Openhpsdr.Zeus.Plugins.Voyeur;

// Request DTOs — inlined from the in-core ZeusEndpoints.cs (they were internal
// records there, not part of the moved VoyeurModels). Same field names so the
// UI's POST bodies are unchanged.
public sealed record VoyeurStartRequest(bool? KeepAudio);
public sealed record VoyeurInstallRequest(string? Model);
public sealed record VoyeurEngineConfigRequest(string? Engine, string? Provider, bool? GpuEnabled);
public sealed record VoyeurAlertTestRequest(string? Channel);

public sealed class VoyeurPlugin : IZeusPlugin, IBackendPlugin, IRxAudioTapPlugin
{
    private IPluginContext? _ctx;
    private VoyeurStore? _store;
    private WhisperTranscriber? _whisper;            // concrete: install/status + STT adapter source
    private SherpaParakeetTranscriber? _parakeet;    // concrete: install/status (opt-in Parakeet)
    private LlamaSummarizer? _llama;
    private SileroVad? _vad;                          // optional VAD (transcription refine + install)
    private ISttEngine? _whisperEngine;
    private ISttEngine? _parakeetEngine;
    private VoyeurInstallService? _install;
    private VoyeurTranscriptionService? _transcription;
    private VoyeurMonitorService? _monitor;
    private VoyeurCorpusStore? _corpus;
    private VoyeurAlertService? _alerts;

    // Live operator settings (additive, default-safe). Volatile so the
    // transcription worker / endpoints see updates without locking.
    private volatile EngineSettings _engineSettings = EngineSettings.Default;
    private volatile SegSettings _seg = new();

    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private Task? _alertsTask;

    public async Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        _ctx = context;
        var log = context.Logger;

        // Hydrate persisted settings (all default-safe: Whisper engine, energy
        // gate, alerts/corpus OFF). PsEnabled / radio path are untouched.
        var callsigns = await context.Settings.GetAsync<CallsignSettings>("callsigns", ct) ?? new CallsignSettings();
        _engineSettings = await context.Settings.GetAsync<EngineSettings>(EngineSettings.SettingsKey, ct) ?? EngineSettings.Default;
        _seg = await context.Settings.GetAsync<SegSettings>("voyeur.seg", ct) ?? new SegSettings();
        var corpusSettings = await context.Settings.GetAsync<CorpusSettings>("corpus", ct);

        _whisper = new WhisperTranscriber(log, callsigns);
        _parakeet = new SherpaParakeetTranscriber(log);
        _llama = new LlamaSummarizer(log);
        _vad = new SileroVad(log);
        _whisperEngine = new WhisperSttEngine(_whisper, log);
        _parakeetEngine = _parakeet;

        _install = new VoyeurInstallService(log, new SimpleHttpClientFactory(), _whisper, _llama, _parakeet, _vad);
        _store = new VoyeurStore(log);

        // Training-corpus retention (default OFF — hydrate the saved opt-in/cap).
        _corpus = new VoyeurCorpusStore(Path.Combine(_store.AudioRoot, "corpus"), log);
        if (corpusSettings is not null) _corpus.Settings = corpusSettings;

        // Watchword alerts — created BEFORE transcription so it can be injected;
        // fan-out order = ctor order.
        _alerts = new VoyeurAlertService(
            _store,
            context.Settings,
            new IAlertChannel[]
            {
                new EmailAlertChannel(),
                new NtfyAlertChannel(),
                new SmsGatewayAlertChannel(),
            },
            log);

        _transcription = new VoyeurTranscriptionService(
            _whisperEngine, _parakeetEngine, () => _engineSettings,
            _store, context.Qrz, log, callsigns, _corpus, _alerts, _vad);
        _transcription.Configure(_seg);

        _monitor = new VoyeurMonitorService(context.Radio, _store, _transcription, log);

        // Drive the worker loops (the BackgroundService host machinery is gone —
        // the plugin owns the lifetime). Both cancel on _runCts.
        _runCts = new CancellationTokenSource();
        _runTask = _transcription.RunAsync(_runCts.Token);
        _alertsTask = _alerts.RunAsync(_runCts.Token);

        log.LogInformation(
            "voyeur.plugin.init whisper={Asr} parakeet={Pk} digest={Digest} radio={Radio} qrz={Qrz}",
            _whisperEngine.Available, _parakeetEngine.Available, _llama.Available,
            context.Radio is not null, context.Qrz is not null);
    }

    public async Task ShutdownAsync(CancellationToken ct)
    {
        try { _monitor?.Stop(); } catch { /* finalize best-effort */ }
        try
        {
            _runCts?.Cancel();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            foreach (var t in new[] { _runTask, _alertsTask })
            {
                if (t is null) continue;
                try { await t.WaitAsync(timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { /* expected on shutdown */ }
            }
        }
        finally
        {
            _monitor?.Dispose();
            _store?.Dispose();
            _runCts?.Dispose();
            _ctx = null; _store = null; _whisper = null; _parakeet = null; _llama = null;
            _vad = null; _whisperEngine = null; _parakeetEngine = null;
            _install = null; _transcription = null; _monitor = null;
            _corpus = null; _alerts = null;
            _runCts = null; _runTask = null; _alertsTask = null;
        }
    }

    // ---- RX audio tap -------------------------------------------------------

    // Advisory: the host RX band audio is mono 48 kHz; the ring tolerates any
    // block length, and Feed() reads the actual rate from AudioBlockContext.
    public AudioPluginRequirements Requirements { get; } = new(48_000, 1, 2048);

    public Task InitializeTapAsync(IAudioHost host, CancellationToken ct) => Task.CompletedTask;

    public void OnRxAudio(ReadOnlySpan<float> samples, AudioBlockContext ctx)
    {
        // Main receiver only — exactly the `receiver != 0` filter the in-core
        // service applied. Feed() idle-guards when no session is active.
        if (ctx.Receiver != 0) return;
        _monitor?.Feed(samples, ctx.SampleRate);
    }

    public Task ShutdownTapAsync(CancellationToken ct) => Task.CompletedTask;

    // ---- HTTP endpoints (re-mapped under /api/plugins/{id}/...) -------------

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("status", () => Results.Ok(Monitor.Status()));

        endpoints.MapGet("transcription", () => Results.Ok(new
        {
            available = Whisper.Available,
            modelDir = WhisperTranscriber.ModelDir,
            digestAvailable = Llama.Available,
        }));

        endpoints.MapGet("install/models", () => Results.Ok(VoyeurInstallService.AvailableModels));
        endpoints.MapGet("install/status", () => Results.Ok(Install.Status()));
        endpoints.MapPost("install/model", (VoyeurInstallRequest body) =>
            Results.Ok(Install.InstallModel(body?.Model ?? "small.en")));
        endpoints.MapPost("install/cancel", () => { Install.Cancel(); return Results.Ok(Install.Status()); });

        endpoints.MapPost("start", (VoyeurStartRequest? body) =>
            Results.Ok(Monitor.Start(keepAudio: body?.KeepAudio ?? true, settings: _seg)));
        endpoints.MapPost("stop", () => Results.Ok(Monitor.Stop()));

        // --- engine selection (Whisper locked default | Parakeet opt-in) -------
        endpoints.MapGet("config/engine", () => Results.Ok(EngineConfigDto()));
        endpoints.MapPut("config/engine", async (VoyeurEngineConfigRequest body, CancellationToken ct) =>
        {
            var cur = _engineSettings;
            var kind = cur.Engine;
            if (!string.IsNullOrWhiteSpace(body?.Engine) &&
                Enum.TryParse<SttEngineKind>(body!.Engine, ignoreCase: true, out var k))
                kind = k;
            var next = new EngineSettings(
                Engine: kind,
                Provider: string.IsNullOrWhiteSpace(body?.Provider) ? cur.Provider : body!.Provider!.Trim().ToLowerInvariant(),
                GpuEnabled: body?.GpuEnabled ?? cur.GpuEnabled);
            _engineSettings = next;
            await (_ctx?.Settings.SetAsync(EngineSettings.SettingsKey, next, ct) ?? Task.CompletedTask);
            return Results.Ok(EngineConfigDto());
        });

        // --- segmentation tunables (pre-roll / hang / floor / max-over / VAD) ---
        endpoints.MapGet("config/seg", () => Results.Ok(_seg));
        endpoints.MapPut("config/seg", async (SegSettings body, CancellationToken ct) =>
        {
            var s = body ?? new SegSettings();
            _seg = s;
            _transcription?.Configure(s); // VAD refine picks it up now; gate on next Start
            await (_ctx?.Settings.SetAsync("voyeur.seg", s, ct) ?? Task.CompletedTask);
            return Results.Ok(_seg);
        });

        // --- watchword alerts (config redacted; secrets write-only) ------------
        endpoints.MapGet("alerts/config", async () => Results.Ok(await Alerts.GetConfigAsync()));
        endpoints.MapPut("alerts/config", async (AlertConfigUpdate body) =>
            Results.Ok(await Alerts.UpdateConfigAsync(body)));
        endpoints.MapPost("alerts/test", async (VoyeurAlertTestRequest? body) =>
            Results.Ok(await Alerts.TestAsync(body?.Channel)));

        // --- training-corpus retention (default OFF) ---------------------------
        endpoints.MapGet("corpus", () => Results.Ok(Corpus.GetStats()));
        endpoints.MapPatch("corpus", async (CorpusSettings body, CancellationToken ct) =>
        {
            var s = new CorpusSettings(body?.RetainCorpus ?? false, body?.MaxClips ?? 5000);
            Corpus.Settings = s;
            await (_ctx?.Settings.SetAsync("corpus", s, ct) ?? Task.CompletedTask);
            return Results.Ok(Corpus.GetStats());
        });

        endpoints.MapGet("sessions", () => Results.Ok(Store.ListSessions()));
        endpoints.MapGet("sessions/{id}", (string id) =>
        {
            var d = Store.GetSession(id);
            return d is null ? Results.NotFound(new { error = "session not found" }) : Results.Ok(d);
        });
        endpoints.MapPatch("sessions/{id}", (string id, VoyeurUpdateRequest body) =>
        {
            var d = Store.Update(id, body?.Label, body?.Pinned);
            return d is null ? Results.NotFound(new { error = "session not found" }) : Results.Ok(d);
        });
        endpoints.MapDelete("sessions/{id}", (string id) =>
            Store.Delete(id)
                ? Results.Ok(new { deleted = id })
                : Results.NotFound(new { error = "session not found" }));

        endpoints.MapGet("sessions/{id}/report", (string id) =>
        {
            var r = Store.GetReport(id);
            return r is null ? Results.NotFound(new { error = "session not found" }) : Results.Ok(r);
        });
        endpoints.MapGet("search", (string? q) => Results.Ok(Store.Search(q ?? "")));

        endpoints.MapPost("sessions/{id}/digest", async (string id, CancellationToken ct) =>
        {
            if (!Llama.Available)
                return Results.BadRequest(new { error = "digest model not installed" });
            var transcript = Store.SessionTranscript(id);
            if (string.IsNullOrWhiteSpace(transcript))
                return Results.BadRequest(new { error = "no transcript to summarize yet" });
            var digest = await Llama.SummarizeAsync(transcript, TimeSpan.FromSeconds(120), ct);
            if (digest is null) return Results.StatusCode(503);
            Store.SetDigest(id, digest);
            return Results.Ok(Store.GetReport(id));
        });

        endpoints.MapGet("segments/{segId}/audio", (string segId) =>
        {
            var path = Store.GetSegmentAudioPath(segId);
            return path is null
                ? Results.NotFound(new { error = "audio not found" })
                : Results.File(path, "audio/wav", enableRangeProcessing: true);
        });
    }

    private VoyeurMonitorService Monitor => _monitor ?? throw NotInit();
    private VoyeurStore Store => _store ?? throw NotInit();
    private WhisperTranscriber Whisper => _whisper ?? throw NotInit();
    private LlamaSummarizer Llama => _llama ?? throw NotInit();
    private VoyeurInstallService Install => _install ?? throw NotInit();
    private VoyeurAlertService Alerts => _alerts ?? throw NotInit();
    private VoyeurCorpusStore Corpus => _corpus ?? throw NotInit();

    private object EngineConfigDto() => new
    {
        engine = _engineSettings.Engine.ToString(),       // "Whisper" | "Parakeet"
        provider = _engineSettings.Provider,
        gpuEnabled = _engineSettings.GpuEnabled,
        resolvedProvider = _engineSettings.ResolveProvider(),
        available = new
        {
            whisper = _whisperEngine?.Available ?? false,
            parakeet = _parakeetEngine?.Available ?? false,
        },
    };

    private static InvalidOperationException NotInit() => new("VoyeurPlugin not initialised");
}

/// <summary>Minimal IHttpClientFactory for VoyeurInstallService's occasional
/// engine/model downloads — the plugin host doesn't inject one.</summary>
internal sealed class SimpleHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}

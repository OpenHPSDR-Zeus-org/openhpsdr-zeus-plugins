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
using Zeus.Server;          // VoyeurMonitorService, BandUtils
using Zeus.Server.Voyeur;   // store / whisper / llama / install / transcription / DTOs

namespace Openhpsdr.Zeus.Plugins.Voyeur;

// Request DTOs — inlined from the in-core ZeusEndpoints.cs (they were internal
// records there, not part of the moved VoyeurModels). Same field names so the
// UI's POST bodies are unchanged.
public sealed record VoyeurStartRequest(bool? KeepAudio);
public sealed record VoyeurInstallRequest(string? Model);

public sealed class VoyeurPlugin : IZeusPlugin, IBackendPlugin, IRxAudioTapPlugin
{
    private IPluginContext? _ctx;
    private VoyeurStore? _store;
    private WhisperTranscriber? _whisper;
    private LlamaSummarizer? _llama;
    private VoyeurInstallService? _install;
    private VoyeurTranscriptionService? _transcription;
    private VoyeurMonitorService? _monitor;

    private CancellationTokenSource? _runCts;
    private Task? _runTask;

    public Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        _ctx = context;
        var log = context.Logger;

        _whisper = new WhisperTranscriber(log);
        _llama = new LlamaSummarizer(log);
        _install = new VoyeurInstallService(log, new SimpleHttpClientFactory(), _whisper, _llama);
        _store = new VoyeurStore(log);
        _transcription = new VoyeurTranscriptionService(_whisper, _store, context.Qrz, log);
        _monitor = new VoyeurMonitorService(context.Radio, _store, _transcription, log);

        // Drive the transcription worker loop (the BackgroundService host machinery
        // is gone — the plugin owns the lifetime).
        _runCts = new CancellationTokenSource();
        _runTask = _transcription.RunAsync(_runCts.Token);

        log.LogInformation(
            "voyeur.plugin.init transcription={Asr} digest={Digest} radio={Radio} qrz={Qrz}",
            _whisper.Available, _llama.Available, context.Radio is not null, context.Qrz is not null);
        return Task.CompletedTask;
    }

    public async Task ShutdownAsync(CancellationToken ct)
    {
        try { _monitor?.Stop(); } catch { /* finalize best-effort */ }
        try
        {
            _runCts?.Cancel();
            if (_runTask is not null)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                try { await _runTask.WaitAsync(timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { /* expected on shutdown */ }
            }
        }
        finally
        {
            _monitor?.Dispose();
            _store?.Dispose();
            _runCts?.Dispose();
            _ctx = null; _store = null; _whisper = null; _llama = null;
            _install = null; _transcription = null; _monitor = null;
            _runCts = null; _runTask = null;
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
            Results.Ok(Monitor.Start(keepAudio: body?.KeepAudio ?? true)));
        endpoints.MapPost("stop", () => Results.Ok(Monitor.Stop()));

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
    private static InvalidOperationException NotInit() => new("VoyeurPlugin not initialised");
}

/// <summary>Minimal IHttpClientFactory for VoyeurInstallService's occasional
/// engine/model downloads — the plugin host doesn't inject one.</summary>
internal sealed class SimpleHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}

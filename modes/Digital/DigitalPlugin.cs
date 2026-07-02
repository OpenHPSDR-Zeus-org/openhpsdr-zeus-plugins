// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus Digital · FT8/FT4 — plugin entry for Openhpsdr-Zeus.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Backend-only plugin (no ui block in plugin.json): the pop-out UI shell stays
// in Zeus core and talks to this plugin's endpoints under
// /api/plugins/com.kb2uka.digital/. Extracted from the in-core FT8/FT4/WSPR
// suite; behaviour and JSON shapes are preserved so the core frontend works
// unchanged against the re-homed routes:
//
//   * RX audio arrives via the host RX tap (IRxAudioTapPlugin.OnRxAudio,
//     receiver 0 only) and is fed to Ft8Service/WsprService.FeedAudio —
//     replacing the in-core direct subscription to
//     DspPipelineService.RxAudioAvailable. Decode pipeline, slot timing, and
//     the #1100 self-heal watchdog are ported verbatim.
//   * TX keying goes through IPluginContext.RadioController (tagged
//     MoxSource.Plugin by the host) under the full keying discipline (pre-key
//     skip gate / weKeyed-only release / post-key IsMoxOn confirm — see
//     Ft8TxService); TX audio rides IPluginContext.Playback.PlayOnAir, which
//     NEVER keys. Arm state always starts false; the operator arms every
//     session.
//   * The 0x38/0x39/0x3A WebSocket pushes become the GET events SSE stream
//     (events ft8decode / wsprspot / txstatus, payloads byte-shape-identical
//     to the old frame JSON).
//   * Spotting (PSK Reporter / WSPRnet), the live WSJT-X UDP stream, and the
//     operator identity are configured via the config/* endpoints (pushed by
//     the core UI) and persist via IPluginContext.Settings (no LiteDB).
//   * The Dsp/ layer is the extracted Zeus.Dsp.Ft8 source; the zeus_ft8 /
//     zeus_wspr natives ship in the plugin zip under runtimes/<rid>/native and
//     are resolved by Ft8NativeLoader relative to this assembly.
//
// Composition is plain (no DI container): InitializeAsync builds the object
// graph, MapEndpoints closes over it. ShutdownAsync sets _shutdown so EVERY
// mapped handler — status first — answers 503 from then on: a restartless
// reinstall leaves the old boot's routes closed over THIS dead instance, and
// the 503 keeps the core mode gate grey instead of lighting FT8 against a
// zombie.

using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Openhpsdr.Zeus.Plugins.Digital.Dsp;
using Openhpsdr.Zeus.Plugins.Digital.Spotting;
using Openhpsdr.Zeus.Plugins.Digital.Wsjtx;
using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Audio;
using Zeus.Plugins.Contracts.Extensions;

namespace Openhpsdr.Zeus.Plugins.Digital;

public sealed class DigitalPlugin : IZeusPlugin, IBackendPlugin, IRxAudioTapPlugin
{
    private static readonly TimeSpan SseHeartbeatInterval = TimeSpan.FromSeconds(15);

    private IPluginContext? _ctx;
    private ILogger? _log;

    private Ft8Service? _ft8;
    private WsprService? _wspr;
    private DigitalTxArbiter? _arbiter;
    private Ft8TxService? _ft8Tx;
    private WsprTxService? _wsprTx;
    private DigitalConfigService? _config;
    private DigitalEventStream? _events;
    private PskReporterReporter? _pskReporter;
    private WsprnetReporter? _wsprnetReporter;
    private WsjtxLiveSender? _wsjtxSender;
    private WsjtxLiveEmitter? _wsjtxEmitter;

    // A6 zombie-route guard: once true every mapped handler answers 503. Routes
    // survive a restartless uninstall/reinstall (they were mapped at boot and
    // close over THIS instance), so liveness must be an instance flag.
    private volatile bool _shutdown;

    public async Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        _ctx = context;
        var log = _log = context.Logger;

        var sink = context.Playback
            ?? throw new InvalidOperationException(
                "Zeus Digital requires the host audio playback sink (AudioStream).");

        // Persisted plugin-side config (spotting / identity / wsjtx-live) — all
        // network egress defaults OFF until the operator opts in via the core UI.
        var config = _config = await DigitalConfigService.LoadAsync(context.Settings, log, ct)
            .ConfigureAwait(false);

        var events = _events = new DigitalEventStream();

        // RX decode pipelines (idle until the core UI POSTs ft8/enable | wspr/enable).
        var ft8 = _ft8 = new Ft8Service(log);
        var wspr = _wspr = new WsprService(log);

        // TX keyers — armed defaults false, only the explicit arm endpoints set it.
        // Both share one arbiter so FT8/FT4 and WSPR can never be armed together.
        var arbiter = _arbiter = new DigitalTxArbiter();
        _ft8Tx = new Ft8TxService(context.RadioController, sink, PublishTxStatus, arbiter, log);
        _wsprTx = new WsprTxService(context.RadioController, sink, PublishTxStatus, arbiter, log);
        _ft8Tx.Start();
        _wsprTx.Start();

        // SSE fan-out of decodes/spots (country-enriched; workedBefore stays
        // false — the core frontend decorates from its own logbook).
        ft8.DecodesReady += OnFt8DecodesForSse;
        wspr.SpotsReady += OnWsprSpotsForSse;

        // Spotting uploaders + the live WSJT-X stream — leaf subscribers, all
        // gated on their (default-off) enables inside their handlers.
        var radio = context.Radio;
        _pskReporter = new PskReporterReporter(log, ft8, () => radio?.FrequencyHz ?? 0, config);
        _pskReporter.Start();
        _wsprnetReporter = new WsprnetReporter(log, wspr, config);
        _wsjtxSender = new WsjtxLiveSender(log, config.GetWsjtxLiveConfig);
        _wsjtxEmitter = new WsjtxLiveEmitter(log, config, _wsjtxSender.SendDatagramAsync, ft8, wspr, radio, _ft8Tx);
        _wsjtxEmitter.Start();

        log.LogInformation(
            "digital.plugin.init ft8Native={Ft8} wsprNative={Wspr} control={Control} radio={Radio}",
            ft8.NativeAvailable, wspr.NativeAvailable,
            context.RadioController is not null, radio is not null);
    }

    public async Task ShutdownAsync(CancellationToken ct)
    {
        // Flag first: from here every mapped route answers 503 (the boot-time
        // routes cannot be unmapped) and the SSE writers stop.
        _shutdown = true;

        try
        {
            if (_ft8 is not null) _ft8.DecodesReady -= OnFt8DecodesForSse;
            if (_wspr is not null) _wspr.SpotsReady -= OnWsprSpotsForSse;

            // Keyers first — releases any MOX WE raised before anything else dies.
            if (_ft8Tx is not null) await _ft8Tx.StopAsync().ConfigureAwait(false);
            if (_wsprTx is not null) await _wsprTx.StopAsync().ConfigureAwait(false);
            _ft8Tx?.Dispose();
            _wsprTx?.Dispose();

            _wsjtxEmitter?.Dispose();
            _wsjtxSender?.Dispose();
            _pskReporter?.Dispose();
            _wsprnetReporter?.Dispose();

            _ft8?.Dispose();
            _wspr?.Dispose();

            // Complete every SSE subscriber so open event streams end promptly.
            _events?.CompleteAll();
        }
        finally
        {
            // Deliberately do NOT null the service fields: a request that passed
            // its _shutdown check a beat before the flag was set would NRE into
            // a 500 instead of the contracted 503. The flag alone gates every
            // handler; the disposed objects are unreachable behind it.
            _ctx = null;
        }
    }

    // ---- RX audio tap -------------------------------------------------------

    // Advisory: host RX band audio is mono 48 kHz; the services read the actual
    // rate from the block context and skip anything that isn't 48 kHz.
    public AudioPluginRequirements Requirements { get; } = new(48_000, 1, 2048);

    public Task InitializeTapAsync(IAudioHost host, CancellationToken ct) => Task.CompletedTask;

    public void OnRxAudio(ReadOnlySpan<float> samples, AudioBlockContext ctx)
    {
        // Main receiver only — the in-core pipeline tap only ever fed RX0 and
        // both decode sessions are RX0 today. FeedAudio idle-guards when the
        // mode is disabled.
        if (ctx.Receiver != 0) return;
        _ft8?.FeedAudio(ctx.Receiver, ctx.SampleRate, samples);
        _wspr?.FeedAudio(ctx.Receiver, ctx.SampleRate, samples);
    }

    public Task ShutdownTapAsync(CancellationToken ct) => Task.CompletedTask;

    // ---- SSE publication (decode worker / keyer threads — enqueue only) ------

    private void OnFt8DecodesForSse(Ft8DecodeBatch batch)
    {
        try
        {
            var decodes = new List<Ft8DecodeDto>(batch.Decodes.Count);
            foreach (var d in batch.Decodes)
            {
                // Country enrichment stays with decode (the resolver is in Dsp/);
                // workedBefore is emitted false — the core frontend decorates
                // rows from its own logbook at render time.
                string? country = null;
                if (Ft8MessageParse.TryParseSender(d.Text, out var sender, out _))
                    country = CallsignCountryResolver.Resolve(sender);
                decodes.Add(new Ft8DecodeDto(
                    d.SnrDb, d.DtSec, d.FreqHz, d.Score, d.Text, WorkedBefore: false, country));
            }

            var dto = new Ft8DecodeBatchDto(
                batch.Receiver,
                new DateTimeOffset(batch.SlotStartUtc, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                batch.Protocol == Ft8Protocol.Ft4 ? "FT4" : "FT8",
                decodes);

            _events?.Publish("ft8decode", JsonSerializer.Serialize(dto, DigitalJson.SseOptions));
        }
        catch (Exception ex)
        {
            _log?.LogDebug(ex, "digital.sse ft8decode publish failed");
        }
    }

    private void OnWsprSpotsForSse(WsprSpotBatch batch)
    {
        try
        {
            var spots = new List<WsprSpotDto>(batch.Spots.Count);
            foreach (var s in batch.Spots)
                spots.Add(new WsprSpotDto(s.SnrDb, s.DtSec, s.FreqMhz, s.DriftHz, s.Message));

            var dto = new WsprSpotBatchDto(
                batch.Receiver,
                new DateTimeOffset(batch.SlotStartUtc, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                batch.DialFreqMhz,
                spots);

            _events?.Publish("wsprspot", JsonSerializer.Serialize(dto, DigitalJson.SseOptions));
        }
        catch (Exception ex)
        {
            _log?.LogDebug(ex, "digital.sse wsprspot publish failed");
        }
    }

    private void PublishTxStatus(Ft8TxStatusDto status)
    {
        try
        {
            _events?.Publish("txstatus", JsonSerializer.Serialize(status, DigitalJson.SseOptions));
        }
        catch (Exception ex)
        {
            _log?.LogDebug(ex, "digital.sse txstatus publish failed");
        }
    }

    // ---- HTTP endpoints (mapped under /api/plugins/com.kb2uka.digital/...) ---
    // Response/request JSON shapes are byte-for-byte the old in-core
    // /api/ft8|wspr|spotting contract so the re-pointed core frontend works
    // unchanged. Every handler checks the A6 shutdown flag.

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Liveness probe: the core mode gate greys FT8/FT4 unless this answers
        // 2xx (installed-but-not-restarted → no route; zombie instance → 503).
        endpoints.MapGet("status", () =>
        {
            if (_shutdown) return Gone();
            var ft8 = Ft8;
            var wspr = Wspr;
            return Results.Ok(new
            {
                ok = true,
                version = _ctx?.Manifest.Version ?? "",
                ft8 = new
                {
                    nativeAvailable = ft8.NativeAvailable,
                    enabled = ft8.IsEnabled,
                    degraded = ft8.IsDegraded,
                    protocol = ft8.ActiveProtocol == Ft8Protocol.Ft4 ? "FT4" : "FT8",
                },
                wspr = new
                {
                    nativeAvailable = wspr.NativeAvailable,
                    enabled = wspr.IsEnabled,
                    degraded = wspr.IsDegraded,
                },
            });
        });

        // ---- FT8/FT4 decode control (old /api/ft8 block) ---------------------

        endpoints.MapGet("ft8", () =>
        {
            if (_shutdown) return Gone();
            var ft8 = Ft8;
            return Results.Ok(new
            {
                nativeAvailable = ft8.NativeAvailable,
                enabled = ft8.IsEnabled,
                receiver = ft8.ActiveReceiver,
                protocol = ft8.ActiveProtocol == Ft8Protocol.Ft4 ? "FT4" : "FT8",
                passes = ft8.DecodePasses,
            });
        });

        endpoints.MapPost("ft8/enable", (Ft8EnableRequest body) =>
        {
            if (_shutdown) return Gone();
            var ft8 = Ft8;
            var proto = string.Equals(body.Protocol, "FT4", StringComparison.OrdinalIgnoreCase)
                ? Ft8Protocol.Ft4 : Ft8Protocol.Ft8;
            if (body.Passes is int p) ft8.DecodePasses = Math.Clamp(p, 1, 4);
            bool ok = ft8.Enable(body.Receiver ?? 0, proto);
            _log?.LogInformation("api.ft8.enable rx={Rx} proto={Proto} ok={Ok}",
                body.Receiver ?? 0, proto, ok);
            return ok
                ? Results.Ok(new { enabled = true, nativeAvailable = true })
                : Results.Ok(new { enabled = false, nativeAvailable = ft8.NativeAvailable });
        });

        endpoints.MapPost("ft8/disable", () =>
        {
            if (_shutdown) return Gone();
            Ft8.Disable();
            _log?.LogInformation("api.ft8.disable");
            return Results.Ok(new { enabled = false });
        });

        // ---- FT8/FT4 keyer (old Ft8TxEndpoints block) -------------------------

        endpoints.MapGet("ft8/tx", () => _shutdown ? Gone() : Results.Ok(Ft8Tx.Status()));

        endpoints.MapPost("ft8/tx/arm", (Ft8TxArmRequest body) =>
        {
            if (_shutdown) return Gone();
            Ft8Tx.SetArmed(body.Enabled);
            _log?.LogInformation("api.ft8.tx.arm enabled={Enabled}", body.Enabled);
            return Results.Ok(Ft8Tx.Status());
        });

        endpoints.MapPost("ft8/tx", (Ft8TxStageRequest body) =>
        {
            if (_shutdown) return Gone();
            string? err = Ft8Tx.Stage(
                body.Message, body.AudioHz ?? 1500, body.Slot ?? "even", body.Mode ?? "FT8");
            if (err is not null) return Results.BadRequest(new { error = err });
            _log?.LogInformation("api.ft8.tx.stage msg='{Msg}' slot={Slot} mode={Mode}",
                body.Message, body.Slot, body.Mode);
            return Results.Ok(Ft8Tx.Status());
        });

        endpoints.MapPost("ft8/tx/halt", () =>
        {
            if (_shutdown) return Gone();
            Ft8Tx.Halt();
            _log?.LogInformation("api.ft8.tx.halt");
            return Results.Ok(Ft8Tx.Status());
        });

        // ---- WSPR decode control (old /api/wspr block) -------------------------

        endpoints.MapGet("wspr", () =>
        {
            if (_shutdown) return Gone();
            var wspr = Wspr;
            return Results.Ok(new
            {
                nativeAvailable = wspr.NativeAvailable,
                enabled = wspr.IsEnabled,
                receiver = wspr.ActiveReceiver,
                dialFreqMhz = wspr.DialFreqMhz,
            });
        });

        endpoints.MapPost("wspr/enable", (WsprEnableRequest body) =>
        {
            if (_shutdown) return Gone();
            var wspr = Wspr;
            double dial = body.DialFreqMhz ?? 14.0956; // 20 m default
            bool ok = wspr.Enable(body.Receiver ?? 0, dial);
            _log?.LogInformation("api.wspr.enable rx={Rx} dial={Dial:F4} ok={Ok}",
                body.Receiver ?? 0, dial, ok);
            return ok
                ? Results.Ok(new { enabled = true, nativeAvailable = true })
                : Results.Ok(new { enabled = false, nativeAvailable = wspr.NativeAvailable });
        });

        endpoints.MapPost("wspr/disable", () =>
        {
            if (_shutdown) return Gone();
            Wspr.Disable();
            _log?.LogInformation("api.wspr.disable");
            return Results.Ok(new { enabled = false });
        });

        // ---- WSPR beacon keyer -------------------------------------------------

        endpoints.MapGet("wspr/tx", () => _shutdown ? Gone() : Results.Ok(WsprTx.Status()));

        endpoints.MapPost("wspr/tx/arm", (WsprTxArmRequest body) =>
        {
            if (_shutdown) return Gone();
            WsprTx.SetArmed(body.Enabled);
            _log?.LogInformation("api.wspr.tx.arm enabled={Enabled}", body.Enabled);
            return Results.Ok(WsprTx.Status());
        });

        endpoints.MapPost("wspr/tx/settings", (WsprTxSettingsRequest body) =>
        {
            if (_shutdown) return Gone();
            string? err = WsprTx.SetSettings(
                body.Call, body.Grid4, body.DBm ?? 30, body.AudioHz ?? 1500, body.TxPercent ?? 0.2);
            if (err is not null) return Results.BadRequest(new { error = err });
            _log?.LogInformation("api.wspr.tx.settings call={Call} grid={Grid} pct={Pct}",
                body.Call, body.Grid4, body.TxPercent);
            return Results.Ok(WsprTx.Status());
        });

        endpoints.MapPost("wspr/tx/halt", () =>
        {
            if (_shutdown) return Gone();
            WsprTx.Halt();
            _log?.LogInformation("api.wspr.tx.halt");
            return Results.Ok(WsprTx.Status());
        });

        // ---- SSE event stream (replaces the 0x38/0x39/0x3A hub pushes) ---------

        endpoints.MapGet("events", HandleEventsAsync);

        // ---- config pushed by the core UI --------------------------------------

        endpoints.MapGet("config/identity", () =>
            _shutdown ? Gone() : Results.Ok(Config.GetIdentity()));

        endpoints.MapPost("config/identity", async (OperatorIdentityConfig body, CancellationToken ct) =>
        {
            if (_shutdown) return Gone();
            var saved = await Config.SetIdentityAsync(body, ct).ConfigureAwait(false);
            return Results.Ok(saved);
        });

        endpoints.MapGet("config/spotting", () =>
            _shutdown ? Gone() : Results.Ok(Config.GetSpottingConfig()));

        endpoints.MapPost("config/spotting", async (SpottingRuntimeConfig body, CancellationToken ct) =>
        {
            if (_shutdown) return Gone();
            var status = await Config.SetSpottingConfigAsync(body, ct).ConfigureAwait(false);
            return Results.Ok(status);
        });

        endpoints.MapGet("spotting/status", () =>
            _shutdown ? Gone() : Results.Ok(Config.GetSpottingStatus()));

        endpoints.MapGet("config/wsjtx-live", () =>
            _shutdown ? Gone() : Results.Ok(Config.GetWsjtxLiveConfig()));

        endpoints.MapPost("config/wsjtx-live", async (WsjtxLiveConfig body, CancellationToken ct) =>
        {
            if (_shutdown) return Gone();
            var saved = await Config.SetWsjtxLiveConfigAsync(body, ct).ConfigureAwait(false);
            return Results.Ok(saved);
        });
    }

    // SSE: hold the response open, relay events as they arrive, and write a
    // comment heartbeat every 15 s so intermediaries/clients can detect a dead
    // stream. Each subscriber has its own bounded queue (see DigitalEventStream)
    // so a slow client never blocks the decode thread. EventSource on the core
    // frontend auto-reconnects; per the reconnect contract it re-GETs /ft8,
    // /ft8/tx and /wspr on every open.
    private async Task HandleEventsAsync(HttpContext http)
    {
        if (_shutdown || _events is null)
        {
            http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        http.Response.Headers.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        // Some reverse proxies buffer unless told otherwise; harmless elsewhere.
        http.Response.Headers["X-Accel-Buffering"] = "no";

        var ct = http.RequestAborted;
        using var subscription = _events.Subscribe(out var reader);
        try
        {
            // Open the stream immediately so EventSource fires `open` without
            // waiting for the first decode.
            await http.Response.WriteAsync(": connected\n\n", ct).ConfigureAwait(false);
            await http.Response.Body.FlushAsync(ct).ConfigureAwait(false);

            while (!ct.IsCancellationRequested && !_shutdown)
            {
                bool hasMore;
                using (var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    heartbeat.CancelAfter(SseHeartbeatInterval);
                    try
                    {
                        hasMore = await reader.WaitToReadAsync(heartbeat.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // No event inside the heartbeat window — emit a comment
                        // line to keep the connection demonstrably alive.
                        await http.Response.WriteAsync(": keep-alive\n\n", ct).ConfigureAwait(false);
                        await http.Response.Body.FlushAsync(ct).ConfigureAwait(false);
                        continue;
                    }
                }
                if (!hasMore) break; // channel completed — plugin shutting down

                while (reader.TryRead(out var ev))
                {
                    await http.Response.WriteAsync($"event: {ev.Name}\ndata: {ev.Json}\n\n", ct)
                        .ConfigureAwait(false);
                }
                await http.Response.Body.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Client went away — normal for SSE.
        }
    }

    private static IResult Gone()
        => Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

    // Endpoint lambdas run only after InitializeAsync (the host maps them during
    // activation), so these are effectively non-null there; the throw is a
    // programmer-error guard, mirroring RecorderPlugin.Svc.
    private Ft8Service Ft8 => _ft8 ?? throw new InvalidOperationException("DigitalPlugin not initialised");
    private WsprService Wspr => _wspr ?? throw new InvalidOperationException("DigitalPlugin not initialised");
    private Ft8TxService Ft8Tx => _ft8Tx ?? throw new InvalidOperationException("DigitalPlugin not initialised");
    private WsprTxService WsprTx => _wsprTx ?? throw new InvalidOperationException("DigitalPlugin not initialised");
    private DigitalConfigService Config => _config ?? throw new InvalidOperationException("DigitalPlugin not initialised");
}

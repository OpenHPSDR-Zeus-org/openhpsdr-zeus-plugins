// SPDX-License-Identifier: GPL-2.0-or-later
//
// WAV Recorder · Tape Deck — plugin entry for Openhpsdr-Zeus.
//
// Extracted from the in-tree Zeus.Server.Hosting/Wav/* services. Behaviour and
// wire format are preserved verbatim so the existing panel calls and the
// operator's saved recordings continue to work unchanged:
//
//   * RX audio arrives via the host RX tap (IRxAudioTapPlugin.OnRxAudio) and is
//     fed to WavRecorderService.FeedRxAudio — replacing the in-core direct
//     subscription to DspPipelineService.RxAudioAvailable. Receiver 0 only.
//   * Processed/raw TX mic audio arrives via the host TX tap
//     (ITxAudioTapPlugin.OnTxMicAudio) — replacing TxAudioIngest.MicPcmTapped.
//   * Local + over-air playback ride IPluginContext.Playback
//     (IAudioPlaybackSink), which NEVER keys; the over-air self-key goes through
//     IPluginContext.RadioController (IRadioController.SetMoxAsync), tagged
//     MoxSource.Plugin by the host, with the post-key IsMoxOn confirm-gate.
//   * The single recordings-root preference persists via IPluginContext.Settings
//     (no LiteDB) — sidestepping the Windows shared-lock crash (#682).
//   * Recordings live under <Downloads>/Zeus Recordings — the SAME path the
//     in-core feature used, so an upgrading operator's clips are adopted
//     automatically.
//
// The 15 endpoints are re-mapped under /api/plugins/com.kb2uka.recorder/... ;
// the UI module repoints to that prefix.

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Audio;
using Zeus.Plugins.Contracts.Extensions;
using Zeus.Server.Wav;

namespace Openhpsdr.Zeus.Plugins.Recorder;

// Request DTOs — inlined from the in-core ZeusEndpoints.cs (internal records
// there). Same field names so the UI's POST bodies are unchanged.
public sealed record WavRecordStartRequest(string? Source, string? Folder);
public sealed record WavPlayRequest(string? File, string? Dest);
public sealed record WavRenameRequest(string? From, string? Name);
public sealed record WavMoveRequest(string? From, string? Folder);
public sealed record WavFolderRequest(string? Path);
public sealed record WavDeleteRequest(string? File);
public sealed record WavRootRequest(string? Path);

public sealed class RecorderPlugin : IZeusPlugin, IBackendPlugin, IRxAudioTapPlugin, ITxAudioTapPlugin
{
    // IPluginSettings key holding the operator-chosen recordings root (absent =
    // platform default). Replaces the in-core WavRecorderSettingsStore + LiteDB.
    private const string RootKey = "recordings_root";

    private IPluginContext? _ctx;
    private WavRecorderService? _svc;

    public Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        _ctx = context;
        var log = context.Logger;

        // Resolve the effective root: the persisted custom root if it's set and
        // usable; otherwise the platform default. Migrate loose legacy files ONLY
        // when we land on the default root — never scan an operator-chosen parent.
        string? persisted = context.Settings.GetAsync<string?>(RootKey, ct)
            .GetAwaiter().GetResult();
        string defaultRoot = WavLibrary.DefaultRoot();
        string effectiveRoot;
        bool migrate;
        if (!string.IsNullOrWhiteSpace(persisted) && IsUsableRoot(persisted))
        {
            effectiveRoot = persisted;
            migrate = false;
        }
        else
        {
            effectiveRoot = defaultRoot;
            migrate = true;
        }

        var sink = context.Playback
            ?? throw new InvalidOperationException(
                "WAV Recorder requires the host audio playback sink (AudioStream).");

        _svc = new WavRecorderService(
            sink: sink,
            radioController: context.RadioController, // null ⇒ ride-existing-key-only
            log: log,
            recordingsRoot: effectiveRoot,
            migrate: migrate,
            persistRoot: PersistRoot);

        log.LogInformation(
            "recorder.plugin.init root={Root} control={Control}",
            _svc.RecordingsDir, context.RadioController is not null);
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken ct)
    {
        try { _svc?.Dispose(); }
        finally { _svc = null; _ctx = null; }
        return Task.CompletedTask;
    }

    // Persist the chosen recordings root (null ⇒ clear back to default). The
    // in-core store wrote synchronously; IPluginSettings is async, so fire-and-
    // forget — losing the persist on an abrupt shutdown only reverts to default.
    private void PersistRoot(string? root)
    {
        var settings = _ctx?.Settings;
        if (settings is null) return;
        if (string.IsNullOrWhiteSpace(root))
            _ = settings.DeleteAsync(RootKey);
        else
            _ = settings.SetAsync(RootKey, root);
    }

    // A persisted root is usable if it already exists or can be created.
    private static bool IsUsableRoot(string root)
    {
        try
        {
            Directory.CreateDirectory(root);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    // ---- RX audio tap -------------------------------------------------------

    // Advisory: host RX band audio is mono 48 kHz; the recorder tolerates any
    // block length and reads the actual rate from AudioBlockContext.
    public AudioPluginRequirements Requirements { get; } = new(48_000, 1, 2048);

    public Task InitializeTapAsync(IAudioHost host, CancellationToken ct) => Task.CompletedTask;

    public void OnRxAudio(ReadOnlySpan<float> samples, AudioBlockContext ctx)
    {
        // Main receiver only — exactly the `receiver != 0` filter the in-core
        // service applied. FeedRxAudio idle-guards when no RX recording is active.
        if (ctx.Receiver != 0) return;
        _svc?.FeedRxAudio(samples, ctx.SampleRate);
    }

    public Task ShutdownTapAsync(CancellationToken ct) => Task.CompletedTask;

    // ---- TX audio tap -------------------------------------------------------

    // Raw mic (pre-processing) — the in-core WavRecordSource.Tx source. The
    // recorder idle-guards when no TX recording is active.
    public void OnTxMicAudio(ReadOnlySpan<float> samples, AudioBlockContext ctx)
        => _svc?.FeedTxMicAudio(samples, ctx.SampleRate);

    // Processed/air-side TX audio — unused by the recorder (TX records the raw
    // mic, matching the in-core deck).
    public void OnTxAirAudio(ReadOnlySpan<float> samples, AudioBlockContext ctx) { }

    // ---- HTTP endpoints (re-mapped under /api/plugins/{id}/...) -------------
    // Names + JSON DTO shapes are byte-for-byte the in-core /api/wav/* contract
    // so the verbatim-ported WavRecorderPanel does not 404.

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("status", () => Results.Ok(Svc.GetStatus()));

        endpoints.MapGet("list", () => Results.Ok(new
        {
            root = Svc.RecordingsDir,
            folders = Svc.ListFolders(),
            recordings = Svc.ListRecordings(),
        }));

        endpoints.MapGet("waveform", (string? file, int? buckets) =>
        {
            if (string.IsNullOrWhiteSpace(file))
                return Results.BadRequest(new { error = "file is required" });
            try { return Results.Ok(new { file, buckets = Svc.ComputeWaveform(file, buckets ?? 400) }); }
            catch (FileNotFoundException) { return Results.NotFound(new { error = "recording not found" }); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        endpoints.MapPost("record/start", (WavRecordStartRequest body) =>
        {
            var source = string.Equals(body?.Source, "tx", StringComparison.OrdinalIgnoreCase)
                ? WavRecordSource.Tx
                : WavRecordSource.Rx;
            try { return Results.Ok(new { relPath = Svc.StartRecording(source, body?.Folder) }); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });

        endpoints.MapPost("record/stop", () =>
        {
            var r = Svc.StopRecording();
            return r is { } x
                ? Results.Ok(new { relPath = x.RelPath, samples = x.Samples })
                : Results.Ok(new { relPath = (string?)null, samples = 0L });
        });

        endpoints.MapPost("play", (WavPlayRequest body) =>
        {
            if (string.IsNullOrWhiteSpace(body?.File))
                return Results.BadRequest(new { error = "file is required" });
            var dest = string.Equals(body?.Dest, "air", StringComparison.OrdinalIgnoreCase)
                ? WavPlayDest.Air
                : WavPlayDest.Local;
            try { Svc.Play(body!.File!, dest); return Results.Ok(Svc.GetStatus()); }
            catch (FileNotFoundException) { return Results.NotFound(new { error = "recording not found" }); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });

        endpoints.MapPost("stop", () =>
        {
            Svc.StopPlayback();
            return Results.Ok(Svc.GetStatus());
        });

        endpoints.MapPost("rename", (WavRenameRequest body) =>
        {
            if (string.IsNullOrWhiteSpace(body?.From) || string.IsNullOrWhiteSpace(body?.Name))
                return Results.BadRequest(new { error = "from and name are required" });
            try { return Results.Ok(new { relPath = Svc.RenameRecording(body.From, body.Name) }); }
            catch (FileNotFoundException) { return Results.NotFound(new { error = "recording not found" }); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });

        endpoints.MapPost("move", (WavMoveRequest body) =>
        {
            if (string.IsNullOrWhiteSpace(body?.From))
                return Results.BadRequest(new { error = "from is required" });
            try { return Results.Ok(new { relPath = Svc.MoveRecording(body.From, body.Folder ?? "") }); }
            catch (FileNotFoundException) { return Results.NotFound(new { error = "recording not found" }); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });

        endpoints.MapPost("folder/create", (WavFolderRequest body) =>
        {
            if (string.IsNullOrWhiteSpace(body?.Path))
                return Results.BadRequest(new { error = "path is required" });
            try { return Results.Ok(new { folder = Svc.CreateFolder(body.Path) }); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });

        endpoints.MapPost("folder/delete", (WavFolderRequest body) =>
        {
            if (string.IsNullOrWhiteSpace(body?.Path))
                return Results.BadRequest(new { error = "path is required" });
            try { return Results.Ok(new { deleted = Svc.DeleteFolder(body.Path) }); }
            catch (FileNotFoundException) { return Results.NotFound(new { error = "folder not found" }); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
            // Sharing violation (a file in the folder is open/in use) on Windows.
            catch (IOException ex) { return Results.Conflict(new { error = ex.Message }); }
        });

        endpoints.MapPost("delete", (WavDeleteRequest body) =>
        {
            if (string.IsNullOrWhiteSpace(body?.File))
                return Results.BadRequest(new { error = "file is required" });
            try { Svc.DeleteRecording(body.File); return Results.Ok(new { deleted = body.File }); }
            catch (FileNotFoundException) { return Results.NotFound(new { error = "recording not found" }); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            // Active recording/playback target — refused up front.
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
            // Sharing violation (file open/in use) on Windows.
            catch (IOException ex) { return Results.Conflict(new { error = ex.Message }); }
        });

        endpoints.MapGet("root", () =>
        {
            var (root, isDefault) = Svc.GetRecordingsRoot();
            return Results.Ok(new { root, isDefault });
        });

        endpoints.MapPost("root", (WavRootRequest body) =>
        {
            try
            {
                // Null/empty/missing path resets to the platform default.
                var (root, isDefault) = Svc.SetRecordingsRoot(body?.Path);
                return Results.Ok(new { root, isDefault });
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });

        // Read-only server-side directory browser so the web UI can pick a
        // recordings root on the machine the backend runs on. Deliberately not
        // confined to the recordings root.
        endpoints.MapGet("dirs", (string? path) =>
        {
            try
            {
                var listing = WavLibrary.BrowseDirectories(path);
                return Results.Ok(new
                {
                    path = listing.Path,
                    parent = listing.Parent,
                    separator = listing.Separator,
                    dirs = listing.Dirs.Select(d => new { name = d.Name, path = d.Path }),
                });
            }
            catch (Exception ex) when (ex is DirectoryNotFoundException or ArgumentException or IOException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }

    private WavRecorderService Svc => _svc ?? throw new InvalidOperationException("RecorderPlugin not initialised");
}

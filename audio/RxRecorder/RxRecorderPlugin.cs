// SPDX-License-Identifier: GPL-2.0-or-later
//
// RX Recorder — records the demodulated RX (band) audio to a WAV file via the
// host's read-only RX audio tap (IRxAudioTapPlugin). Capture-only; does not
// alter what the operator hears and does not occupy an insert-chain slot.
// Copyright (C) 2026 contributors.

using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Audio;
using Zeus.Plugins.Contracts.Extensions;

namespace Openhpsdr.Zeus.Samples.RxRecorder;

public sealed class RxRecorderPlugin : IZeusPlugin, IRxAudioTapPlugin, IBackendPlugin
{
    private IPluginContext? _ctx;
    private RecorderEngine? _engine;
    private string _recordingsDir = "";

    // ------------------------------------------------------------------
    // IZeusPlugin
    // ------------------------------------------------------------------

    public Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        _ctx = context;
        // Plugins may read/write under their own root without a filesystem
        // capability (IPluginContext.PluginRootPath contract).
        _recordingsDir = Path.Combine(context.PluginRootPath, "recordings");
        context.Logger.LogInformation("RX Recorder initialising; recordings dir = {Dir}", _recordingsDir);
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken ct)
    {
        _engine?.Dispose();
        _engine = null;
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------
    // IRxAudioTapPlugin
    // ------------------------------------------------------------------

    public AudioPluginRequirements Requirements => new(SampleRate: 48000, Channels: 1, BlockSize: 2048);

    public Task InitializeTapAsync(IAudioHost host, CancellationToken ct)
    {
        _engine = new RecorderEngine(host.CurrentSampleRate, _recordingsDir);
        _ctx?.Logger.LogInformation(
            "RX Recorder tap init: SR={SampleRate} Hz, slot={Slot}", host.CurrentSampleRate, host.Slot);
        return Task.CompletedTask;
    }

    public void OnRxAudio(ReadOnlySpan<float> samples, AudioBlockContext ctx)
        => _engine?.OnRxAudio(samples);

    public Task ShutdownTapAsync(CancellationToken ct)
    {
        _engine?.Dispose();
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------
    // IBackendPlugin — control + file REST surface
    // ------------------------------------------------------------------

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("status", GetStatus);
        endpoints.MapPost("record/start", StartRecording);
        endpoints.MapPost("record/stop", StopRecording);
        endpoints.MapGet("recordings", ListRecordings);
        endpoints.MapGet("download/{file}", Download);
        endpoints.MapDelete("recordings/{file}", DeleteRecording);
    }

    private IResult GetStatus()
    {
        var e = _engine;
        return Results.Ok(new RecorderStatusDto
        {
            Recording = e?.IsRecording ?? false,
            File = e?.CurrentFileName,
            Seconds = e?.ElapsedSeconds ?? 0,
            Peak = e?.LastPeak ?? 0f,
            Dropped = e?.DroppedSamples ?? 0,
            TapReady = e is not null,
        });
    }

    private IResult StartRecording()
    {
        var e = _engine;
        if (e is null) return Results.Problem("RX audio tap not ready (no radio/audio yet)", statusCode: 409);
        if (e.IsRecording) return Results.Conflict(new { error = "already recording", file = e.CurrentFileName });
        var name = e.Start();
        _ctx?.Logger.LogInformation("RX Recorder started: {File}", name);
        return Results.Ok(new { file = name });
    }

    private IResult StopRecording()
    {
        var e = _engine;
        var res = e?.Stop();
        if (res is null) return Results.Ok(new { stopped = false });
        _ctx?.Logger.LogInformation("RX Recorder stopped: {File} ({Samples} samples)", res.Value.File, res.Value.Samples);
        return Results.Ok(new { stopped = true, file = res.Value.File, samples = res.Value.Samples });
    }

    private IResult ListRecordings()
    {
        if (!Directory.Exists(_recordingsDir))
            return Results.Ok(new { recordings = Array.Empty<RecordingInfoDto>() });
        var files = new DirectoryInfo(_recordingsDir)
            .GetFiles("*.wav")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => new RecordingInfoDto
            {
                Name = f.Name,
                Bytes = f.Length,
                ModifiedUnixMs = new DateTimeOffset(f.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
            })
            .ToArray();
        return Results.Ok(new { recordings = files });
    }

    private IResult Download(string file)
    {
        var path = SafePath(file);
        if (path is null || !File.Exists(path)) return Results.NotFound();
        return Results.File(path, "audio/wav", Path.GetFileName(path));
    }

    private IResult DeleteRecording(string file)
    {
        var path = SafePath(file);
        if (path is null) return Results.BadRequest(new { error = "invalid file" });
        if (_engine?.IsRecording == true && string.Equals(_engine.CurrentFileName, Path.GetFileName(path), StringComparison.Ordinal))
            return Results.Conflict(new { error = "cannot delete the active recording" });
        if (File.Exists(path)) File.Delete(path);
        return Results.Ok(new { deleted = Path.GetFileName(path) });
    }

    /// <summary>Resolve a user-supplied file name strictly inside the recordings
    /// dir — rejects path traversal / absolute paths.</summary>
    private string? SafePath(string file)
    {
        if (string.IsNullOrWhiteSpace(file)) return null;
        var name = Path.GetFileName(file);
        if (name != file) return null; // contained a directory separator
        var full = Path.GetFullPath(Path.Combine(_recordingsDir, name));
        var root = Path.GetFullPath(_recordingsDir);
        return full.StartsWith(root, StringComparison.Ordinal) ? full : null;
    }

    // ------------------------------------------------------------------
    // DTOs
    // ------------------------------------------------------------------

    public sealed record RecorderStatusDto
    {
        [JsonPropertyName("recording")] public bool Recording { get; init; }
        [JsonPropertyName("file")]      public string? File { get; init; }
        [JsonPropertyName("seconds")]   public double Seconds { get; init; }
        [JsonPropertyName("peak")]      public float Peak { get; init; }
        [JsonPropertyName("dropped")]   public long Dropped { get; init; }
        [JsonPropertyName("tapReady")]  public bool TapReady { get; init; }
    }

    public sealed record RecordingInfoDto
    {
        [JsonPropertyName("name")]           public string Name { get; init; } = "";
        [JsonPropertyName("bytes")]          public long Bytes { get; init; }
        [JsonPropertyName("modifiedUnixMs")] public long ModifiedUnixMs { get; init; }
    }
}

// SPDX-License-Identifier: GPL-2.0-or-later
//
// Recorder — records radio audio from one of three operator-selectable
// sources (RX band audio, raw TX mic, or processed TX "air" audio) via the
// host's read-only audio taps, and plays clips back locally or on the air via
// the host playback sink. Capture is non-destructive and occupies no
// insert-chain slot. Copyright (C) 2026 contributors.

using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Audio;
using Zeus.Plugins.Contracts.Extensions;

namespace Openhpsdr.Zeus.Samples.Recorder;

public sealed class RecorderPlugin : IZeusPlugin, IRxAudioTapPlugin, ITxAudioTapPlugin, IBackendPlugin
{
    // Capture source. Volatile single-writer (control thread) / multi-reader
    // (audio threads).
    private enum Source { Rx, TxMic, TxAir }
    private volatile Source _source = Source.Rx;

    private IPluginContext? _ctx;
    private RecorderEngine? _engine;
    private Player? _player;
    private string _recordingsDir = "";

    // ------------------------------------------------------------------ IZeusPlugin

    public async Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        _ctx = context;
        _recordingsDir = Path.Combine(context.PluginRootPath, "recordings");
        var saved = await context.Settings.GetAsync<string?>("source", ct);
        _source = ParseSource(saved) ?? Source.Rx;
        context.Logger.LogInformation(
            "Recorder initialising; dir={Dir} source={Source} playback={Playback}",
            _recordingsDir, _source, context.Playback is not null ? "available" : "none");
    }

    public Task ShutdownAsync(CancellationToken ct)
    {
        _player?.Dispose();
        _engine?.Dispose();
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------ taps

    public AudioPluginRequirements Requirements => new(SampleRate: 48000, Channels: 1, BlockSize: 2048);

    public Task InitializeTapAsync(IAudioHost host, CancellationToken ct)
    {
        _engine = new RecorderEngine(host.CurrentSampleRate, _recordingsDir);
        if (_ctx?.Playback is { } sink) _player = new Player(sink);
        _ctx?.Logger.LogInformation("Recorder tap init: SR={SampleRate} Hz", host.CurrentSampleRate);
        return Task.CompletedTask;
    }

    // RX band audio.
    public void OnRxAudio(ReadOnlySpan<float> samples, AudioBlockContext ctx)
    {
        if (_source == Source.Rx) _engine?.Feed(samples);
    }

    // Raw mic (pre-processing).
    public void OnTxMicAudio(ReadOnlySpan<float> samples, AudioBlockContext ctx)
    {
        if (_source == Source.TxMic) _engine?.Feed(samples);
    }

    // Processed transmit audio — what goes on the air.
    public void OnTxAirAudio(ReadOnlySpan<float> samples, AudioBlockContext ctx)
    {
        if (_source == Source.TxAir) _engine?.Feed(samples);
    }

    public Task ShutdownTapAsync(CancellationToken ct)
    {
        _engine?.Dispose();
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------ REST

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("status", GetStatus);
        endpoints.MapPost("source", SetSource);
        endpoints.MapPost("record/start", StartRecording);
        endpoints.MapPost("record/stop", StopRecording);
        endpoints.MapPost("play/start", StartPlayback);
        endpoints.MapPost("play/stop", StopPlayback);
        endpoints.MapGet("recordings", ListRecordings);
        endpoints.MapGet("download/{file}", Download);
        endpoints.MapDelete("recordings/{file}", DeleteRecording);
    }

    private IResult GetStatus()
    {
        var e = _engine;
        return Results.Ok(new StatusDto
        {
            Recording = e?.IsRecording ?? false,
            File = e?.CurrentFileName,
            Seconds = e?.ElapsedSeconds ?? 0,
            Peak = e?.LastPeak ?? 0f,
            Dropped = e?.DroppedSamples ?? 0,
            Source = SourceWire(_source),
            TapReady = e is not null,
            Playing = _player?.IsPlaying ?? false,
            PlayingFile = _player?.CurrentFile,
            PlaybackAvailable = _player is not null,
            MoxOn = _ctx?.Playback?.IsMoxOn ?? false,
        });
    }

    private async Task<IResult> SetSource(SourceDto body, CancellationToken ct)
    {
        var s = ParseSource(body.Source);
        if (s is null) return Results.BadRequest(new { error = "source must be rx|txmic|txair" });
        if (_engine?.IsRecording == true) return Results.Conflict(new { error = "stop recording before changing source" });
        _source = s.Value;
        if (_ctx is not null) await _ctx.Settings.SetAsync("source", SourceWire(_source), ct);
        return Results.Ok(new { source = SourceWire(_source) });
    }

    private IResult StartRecording()
    {
        var e = _engine;
        if (e is null) return Results.Problem("audio tap not ready (no radio/audio yet)", statusCode: 409);
        if (e.IsRecording) return Results.Conflict(new { error = "already recording", file = e.CurrentFileName });
        var name = e.Start();
        _ctx?.Logger.LogInformation("Recorder started: {File} (source={Source})", name, _source);
        return Results.Ok(new { file = name });
    }

    private IResult StopRecording()
    {
        var res = _engine?.Stop();
        if (res is null) return Results.Ok(new { stopped = false });
        bool empty = res.Value.Samples == 0;
        _ctx?.Logger.LogInformation(
            "Recorder stopped: {File} ({Samples} samples){Empty}",
            res.Value.File, res.Value.Samples, empty ? " — empty, discarded" : "");
        return Results.Ok(new { stopped = true, file = res.Value.File, samples = res.Value.Samples, discarded = empty });
    }

    private IResult StartPlayback(PlayDto body)
    {
        if (_player is null) return Results.Problem("playback not available on this host", statusCode: 409);
        var path = SafePath(body.File);
        if (path is null || !File.Exists(path)) return Results.NotFound();
        if (!_player.Start(path)) return Results.Conflict(new { error = "already playing" });
        _ctx?.Logger.LogInformation("Recorder playback started: {File} (mox={Mox})", Path.GetFileName(path), _ctx?.Playback?.IsMoxOn);
        return Results.Ok(new { playing = Path.GetFileName(path), onAir = _ctx?.Playback?.IsMoxOn ?? false });
    }

    private IResult StopPlayback()
    {
        _player?.Stop();
        return Results.Ok(new { stopped = true });
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
        var name = Path.GetFileName(path);
        if (_engine?.IsRecording == true && string.Equals(_engine.CurrentFileName, name, StringComparison.Ordinal))
            return Results.Conflict(new { error = "cannot delete the active recording" });
        if (_player?.IsPlaying == true && string.Equals(_player.CurrentFile, name, StringComparison.Ordinal))
            return Results.Conflict(new { error = "cannot delete a playing recording" });
        if (File.Exists(path)) File.Delete(path);
        return Results.Ok(new { deleted = name });
    }

    private string? SafePath(string file)
    {
        if (string.IsNullOrWhiteSpace(file)) return null;
        var name = Path.GetFileName(file);
        if (name != file) return null;
        var full = Path.GetFullPath(Path.Combine(_recordingsDir, name));
        var root = Path.GetFullPath(_recordingsDir);
        return full.StartsWith(root, StringComparison.Ordinal) ? full : null;
    }

    private static Source? ParseSource(string? s) => s switch
    {
        "rx" => Source.Rx,
        "txmic" => Source.TxMic,
        "txair" => Source.TxAir,
        _ => null,
    };

    private static string SourceWire(Source s) => s switch
    {
        Source.Rx => "rx",
        Source.TxMic => "txmic",
        Source.TxAir => "txair",
        _ => "rx",
    };

    // ------------------------------------------------------------------ DTOs

    public sealed record SourceDto
    {
        [JsonPropertyName("source")] public string? Source { get; init; }
    }

    public sealed record PlayDto
    {
        [JsonPropertyName("file")] public string File { get; init; } = "";
    }

    public sealed record StatusDto
    {
        [JsonPropertyName("recording")]         public bool Recording { get; init; }
        [JsonPropertyName("file")]              public string? File { get; init; }
        [JsonPropertyName("seconds")]           public double Seconds { get; init; }
        [JsonPropertyName("peak")]              public float Peak { get; init; }
        [JsonPropertyName("dropped")]           public long Dropped { get; init; }
        [JsonPropertyName("source")]            public string Source { get; init; } = "rx";
        [JsonPropertyName("tapReady")]          public bool TapReady { get; init; }
        [JsonPropertyName("playing")]           public bool Playing { get; init; }
        [JsonPropertyName("playingFile")]       public string? PlayingFile { get; init; }
        [JsonPropertyName("playbackAvailable")] public bool PlaybackAvailable { get; init; }
        [JsonPropertyName("moxOn")]             public bool MoxOn { get; init; }
    }

    public sealed record RecordingInfoDto
    {
        [JsonPropertyName("name")]           public string Name { get; init; } = "";
        [JsonPropertyName("bytes")]          public long Bytes { get; init; }
        [JsonPropertyName("modifiedUnixMs")] public long ModifiedUnixMs { get; init; }
    }
}

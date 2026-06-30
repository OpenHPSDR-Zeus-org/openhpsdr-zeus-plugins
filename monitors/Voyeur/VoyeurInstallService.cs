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

using System.IO.Compression;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Zeus.Server.Voyeur;

/// <summary>
/// In-app, terminal-free setup for Voyeur Mode (zeus-la5) — cross-platform
/// (macOS / Linux / Windows). The operator never runs a command: this service
/// downloads, with live progress, everything the AI features need into the Zeus
/// app-data folders, and discovery is dynamic so each piece is picked up the
/// moment its download finishes — no restart.
///
/// Two kinds of payload:
///  • MODELS — the large, license-/choice-dependent speech (whisper.cpp) and
///    digest (Qwen GGUF) files, streamed from Hugging Face into <c>whisper/</c>
///    and <c>llama/</c>.
///  • ENGINES — the native binaries (whisper-cli, llama completion tool), shipped
///    as a per-RID zip built by <c>build-voyeur-engines.yml</c> and published as
///    a Zeus release asset, extracted into each engine's <c>bin/</c> dir. They
///    are static/self-contained and run without notarization because an
///    app-download applies no macOS quarantine xattr (see EngineTag below).
///
/// Keeping engines OUT of the base Zeus download (button-fetched on demand)
/// keeps the install lean for the majority who never use Voyeur, and confines
/// the native-binary footprint to those who opt in.
/// </summary>
public sealed class VoyeurInstallService
{
    public enum Phase { Idle, Downloading, Done, Error }

    public sealed record Progress(
        Phase Phase, int Percent, string Message, string? Item,
        bool ModelPresent, bool BinaryPresent,
        bool DigestModelPresent, bool DigestBinaryPresent, string Rid,
        bool SherpaEnginePresent = false, bool ParakeetModelPresent = false,
        bool VadModelPresent = false);

    private readonly ILogger _log;
    private readonly IHttpClientFactory _httpFactory;
    private readonly WhisperTranscriber _whisper;
    private readonly LlamaSummarizer _llama;
    private readonly SherpaParakeetTranscriber _parakeet;
    private readonly SileroVad _vad;

    private readonly object _gate = new();
    private Phase _phase = Phase.Idle;
    private int _percent;
    private string _message = "";
    private string? _item;
    private CancellationTokenSource? _cts;

    // Per-RID engine bundles, built + notarized in CI and published as Zeus
    // release assets under a stable tag. Asset names are <engine>-<rid>.zip
    // (rid = osx-arm64 | osx-x64 | win-x64 | win-arm64 | linux-x64 |
    // linux-arm64). The "{rid}" token is resolved at download time. Bumping
    // the engine build = bump this tag (and re-publish the assets); the app
    // re-fetches the matching bundle. Keep the tag in sync with
    // .github/workflows/build-voyeur-engines.yml.
    private const string EngineTag = "voyeur-engines-v1";
    private const string EngineBase =
        "https://github.com/Kb2uka/openhpsdr-zeus/releases/download/" + EngineTag;

    // Models + engines offered in-app. Speech/digest MODELS are stable Hugging
    // Face files (whisper.cpp + Qwen GGUF, both permissively licensed). ENGINES
    // are the native binaries + their dylibs, shipped as a per-RID zip that we
    // extract into the engine's bin/ dir (where discovery already looks).
    private enum Kind { Whisper, Llama, EngineWhisper, EngineLlama, EngineSherpa, Parakeet, SileroVad }
    // Archive=true ⇒ payload is a zip to extract into DestDir; else a single
    // file moved into place atomically.
    private sealed record ModelDef(string Url, long MinBytes, string Label, Kind Kind, string FileName, bool Archive = false);

    private static readonly Dictionary<string, ModelDef> Models = new()
    {
        // --- engines (native binaries — required before models do anything) ---
        // Min-size is a low floor to reject error pages, NOT a tight bound: a
        // static whisper-cli zips to well under 1 MB, so keep this small.
        ["engine-whisper"] = new(EngineBase + "/whisper-{rid}.zip",
            200_000, "Speech engine (whisper.cpp) — required for transcription", Kind.EngineWhisper, "whisper-engine.zip", Archive: true),
        ["engine-llama"] = new(EngineBase + "/llama-{rid}.zip",
            200_000, "AI-summary engine (llama.cpp) — required for digests", Kind.EngineLlama, "llama-engine.zip", Archive: true),
        // --- transcription (whisper) ---
        ["medium.en"] = new("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-medium.en.bin",
            1_300_000_000, "Transcription: Medium (English) — ~1.5 GB, recommended", Kind.Whisper, "ggml-medium.en.bin"),
        ["small.en"] = new("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.en.bin",
            400_000_000, "Transcription: Small (English) — ~0.5 GB, faster", Kind.Whisper, "ggml-small.en.bin"),
        // --- digest (local LLM) ---
        ["digest-small"] = new("https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct-GGUF/resolve/main/qwen2.5-0.5b-instruct-q4_k_m.gguf",
            300_000_000, "Digest LLM: Small — ~0.5 GB, fast", Kind.Llama, "qwen2.5-0.5b-instruct-q4_k_m.gguf"),
        ["digest-medium"] = new("https://huggingface.co/Qwen/Qwen2.5-1.5B-Instruct-GGUF/resolve/main/qwen2.5-1.5b-instruct-q4_k_m.gguf",
            900_000_000, "Digest LLM: Medium — ~1 GB, better summaries", Kind.Llama, "qwen2.5-1.5b-instruct-q4_k_m.gguf"),
        // --- sherpa engine + Parakeet (opt-in alternative STT) + Silero VAD ---
        // Path 1: CI repacks the upstream sherpa-onnx .tar.bz2 → per-rid .zip
        // (the BCL has no bzip2 decoder), so the existing ExtractEngine/{rid}
        // path is reused unchanged. Keep CI asset names aligned to Zeus rids.
        ["engine-sherpa"] = new(EngineBase + "/sherpa-{rid}.zip",
            1_000_000, "Speech engine (sherpa-onnx) — required for Parakeet/VAD", Kind.EngineSherpa, "sherpa-engine.zip", Archive: true),
        ["parakeet"] = new(EngineBase + "/parakeet-tdt-06b-int8.zip",
            600_000_000, "Transcription: Parakeet-TDT 0.6B (int8) — ~660 MB, high accuracy", Kind.Parakeet, "parakeet.zip", Archive: true),
        ["silero-vad"] = new("https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/silero_vad.onnx",
            1_000_000, "VAD: Silero (optional — sharper 'over' boundaries)", Kind.SileroVad, "silero_vad.onnx"),
    };

    private static string DestDir(Kind k) => k switch
    {
        Kind.Llama => LlamaSummarizer.ModelDir,
        Kind.Whisper => WhisperTranscriber.ModelDir,
        Kind.EngineLlama => LlamaSummarizer.BinDir,
        Kind.EngineWhisper => WhisperTranscriber.BinDir,
        Kind.EngineSherpa => SherpaParakeetTranscriber.BinDir,
        Kind.Parakeet => SherpaParakeetTranscriber.ModelDir,
        Kind.SileroVad => SileroVad.ModelDir,
        _ => WhisperTranscriber.ModelDir,
    };

    public VoyeurInstallService(
        ILogger log,
        IHttpClientFactory httpFactory,
        WhisperTranscriber whisper,
        LlamaSummarizer llama,
        SherpaParakeetTranscriber parakeet,
        SileroVad vad)
    {
        _log = log;
        _httpFactory = httpFactory;
        _whisper = whisper;
        _llama = llama;
        _parakeet = parakeet;
        _vad = vad;
    }

    public static IEnumerable<object> AvailableModels =>
        Models.Select(m => new { id = m.Key, label = m.Value.Label, kind = KindTag(m.Value.Kind) });

    private static string KindTag(Kind k) => k switch
    {
        Kind.EngineWhisper => "engine-whisper",
        Kind.EngineLlama => "engine-llama",
        Kind.EngineSherpa => "engine-sherpa",
        Kind.SileroVad => "vad",
        Kind.Llama => "digest",
        _ => "transcription",   // Whisper + Parakeet group under transcription
    };

    public Progress Status()
    {
        lock (_gate)
        {
            return new Progress(
                _phase, _percent, _message, _item,
                ModelPresent: _whisper.ModelPath is not null,
                BinaryPresent: _whisper.CliPath is not null,
                DigestModelPresent: _llama.ModelPath is not null,
                DigestBinaryPresent: _llama.CliPath is not null,
                Rid: Rid(),
                SherpaEnginePresent: _parakeet.CliPath is not null,
                ParakeetModelPresent: _parakeet.ResolvedModelDir is not null,
                VadModelPresent: _vad.ModelPath is not null);
        }
    }

    /// <summary>Begin downloading <paramref name="modelId"/>. Returns the current
    /// status immediately; progress is polled via <see cref="Status"/>. No-op if
    /// a download is already running.</summary>
    public Progress InstallModel(string modelId)
    {
        lock (_gate)
        {
            if (_phase == Phase.Downloading) return Status();
            if (!Models.ContainsKey(modelId))
            {
                _phase = Phase.Error;
                _message = $"unknown model '{modelId}'";
                return Status();
            }
            _phase = Phase.Downloading;
            _percent = 0;
            _item = modelId;
            _message = "starting…";
            _cts = new CancellationTokenSource();
        }
        var ct = _cts!.Token;
        _ = Task.Run(() => DownloadModelAsync(modelId, ct), ct);
        return Status();
    }

    /// <summary>Cancel an in-flight download.</summary>
    public void Cancel() { lock (_gate) { _cts?.Cancel(); } }

    private async Task DownloadModelAsync(string modelId, CancellationToken ct)
    {
        var def = Models[modelId];
        var destDir = DestDir(def.Kind);
        Directory.CreateDirectory(destDir);
        var finalPath = Path.Combine(destDir, def.FileName);
        var tmpPath = finalPath + ".part";
        long minBytes = def.MinBytes;
        var url = def.Url.Replace("{rid}", Rid());

        try
        {
            var http = _httpFactory.CreateClient("voyeur-install");
            http.Timeout = Timeout.InfiniteTimeSpan; // large file; we cap via ct, not a fixed timeout

            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            long? total = resp.Content.Headers.ContentLength;

            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(tmpPath))
            {
                var buf = new byte[1 << 20];
                long read = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    read += n;
                    if (total is > 0)
                        SetProgress((int)(read * 100 / total.Value),
                            $"downloading {modelId} — {read / 1_000_000} / {total / 1_000_000} MB");
                    else
                        SetProgress(0, $"downloading {modelId} — {read / 1_000_000} MB");
                }
            }

            var size = new FileInfo(tmpPath).Length;
            if (size < minBytes)
                throw new InvalidOperationException(
                    $"download too small ({size} bytes) — likely an error page, not the model");

            if (def.Archive)
            {
                // Engine bundle: extract the binary + its dylibs into the
                // engine's bin/ dir (where discovery looks), then mark them
                // executable on Unix. The bundle is signed/notarized in CI, and
                // an HttpClient download applies no quarantine xattr, so the
                // binaries run without a Gatekeeper prompt.
                SetProgress(100, $"installing {modelId}…");
                ExtractEngine(tmpPath, destDir);
                TryDelete(tmpPath);
            }
            else
            {
                // Atomic publish: only a fully-downloaded, sane-sized file
                // becomes the live model. A crash mid-download leaves only the
                // .part.
                File.Move(tmpPath, finalPath, overwrite: true);
            }

            lock (_gate)
            {
                _phase = Phase.Done;
                _percent = 100;
                _message = $"{modelId} ready";
            }
            _log.LogInformation("voyeur.install {Model} done ({Mb} MB)", modelId, size / 1_000_000);
        }
        catch (OperationCanceledException)
        {
            TryDelete(tmpPath);
            lock (_gate) { _phase = Phase.Idle; _percent = 0; _message = "cancelled"; }
        }
        catch (Exception ex)
        {
            TryDelete(tmpPath);
            // Generic client-facing message — the raw exception can carry the
            // download URL / host / local path, which we keep in the log only.
            lock (_gate) { _phase = Phase.Error; _message = $"download of '{modelId}' failed (see logs)"; }
            _log.LogWarning(ex, "voyeur.install model {Model} failed", modelId);
        }
    }

    private void SetProgress(int percent, string message)
    {
        lock (_gate) { _percent = percent; _message = message; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }

    // Extract an engine zip into binDir (flattening any single top-level folder
    // the archiver may have added), overwriting prior copies, then add the
    // executable bit on Unix so the binary can be launched. Dylibs/DLLs are
    // harmless to mark +x.
    internal static void ExtractEngine(string zipPath, string binDir)
    {
        Directory.CreateDirectory(binDir);
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            foreach (var entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue; // directory entry
                // Flatten a leading "<something>/" so files land directly in binDir.
                var rel = entry.FullName.Replace('\\', '/');
                var slash = rel.IndexOf('/');
                var name = slash >= 0 ? rel[(slash + 1)..] : rel;
                // Stock sherpa-onnx archives nest the executable under bin/ and the
                // shared libraries under lib/ (e.g. "<top>/bin/sherpa-onnx-offline",
                // "<top>/lib/libonnxruntime.so"). After stripping the wrapper that
                // leaves "bin/<exe>" / "lib/<lib>"; flatten that one level too so the
                // binary and its sibling libs land TOGETHER in binDir — where both
                // LocateCli's flat probe and the loader's @loader_path/$ORIGIN
                // resolution find them. A truly flat repack is untouched.
                if (name.StartsWith("bin/", StringComparison.Ordinal) ||
                    name.StartsWith("lib/", StringComparison.Ordinal))
                    name = name[4..];
                if (string.IsNullOrEmpty(name)) name = entry.Name;
                var target = Path.GetFullPath(Path.Combine(binDir, name));
                // Guard against zip-slip: never write outside binDir.
                if (!target.StartsWith(Path.GetFullPath(binDir) + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    && !string.Equals(target, Path.Combine(Path.GetFullPath(binDir), name), StringComparison.Ordinal))
                    continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
                if (!OperatingSystem.IsWindows())
                {
                    try
                    {
                        var mode = File.GetUnixFileMode(target);
                        File.SetUnixFileMode(target,
                            mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
                    }
                    catch { /* best effort */ }
                }
            }
        }
    }

    private static string Rid()
    {
        string os = OperatingSystem.IsWindows() ? "win"
            : OperatingSystem.IsMacOS() ? "osx"
            : OperatingSystem.IsLinux() ? "linux" : "unknown";
        string arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            _ => RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
        };
        return $"{os}-{arch}";
    }
}

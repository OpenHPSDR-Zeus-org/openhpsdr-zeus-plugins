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

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Zeus.Server.Voyeur;

/// <summary>
/// Opt-in alternative STT engine: NVIDIA Parakeet-TDT-0.6B (int8) via the
/// <c>sherpa-onnx-offline</c> CLI. Like <see cref="WhisperTranscriber"/> this
/// runs as a SEPARATE, SUPERVISED OS CHILD PROCESS — NEVER P/Invoke — so a
/// segfault / OOM in onnxruntime/sherpa native code kills only the child, never
/// the Zeus host. The child runs at below-normal priority and is bounded by a
/// hard timeout (kill + drop the over) so a wedged child can't stall the queue.
///
/// Graceful absence: if the sherpa binary or the Parakeet model isn't installed,
/// transcription with this engine is simply unavailable (capture-only). The
/// in-app installer (VoyeurInstallService) button-downloads both on demand into
/// the Zeus app-data <c>sherpa/</c> folder; discovery is dynamic and
/// env-overridable (<c>ZEUS_SHERPA_CLI</c> / <c>ZEUS_PARAKEET_DIR</c>).
///
/// CONTRACT NOTES (verified against sherpa-onnx source):
///  • Model is a NeMo transducer (encoder/decoder/joiner int8 + tokens.txt),
///    invoked with <c>--model-type=nemo_transducer --decoding-method=greedy_search</c>.
///  • Input MUST be 16 kHz mono — reuse <see cref="WhisperWav.Prepare"/>.
///  • Output is ONE JSON object per wav on STDOUT; we read its <c>"text"</c>
///    field. Progress/diagnostics go to STDERR and are ignored.
///  • Hotwords are NOT honoured by Parakeet-TDT: sherpa's NeMo-transducer impl
///    only allows greedy_search, while <c>--hotwords-file</c> requires
///    modified_beam_search. We still BUILD the hotwords file from
///    <see cref="SttOptions.Hotwords"/> (so callsign biasing is one model-swap
///    away on a hotwords-capable Zipformer model) but do NOT pass it under
///    greedy decoding — passing it would make sherpa error out. Callsign biasing
///    for Parakeet lives in <see cref="CallsignExtractor"/> post-processing.
/// </summary>
public sealed class SherpaParakeetTranscriber : ISttEngine
{
    /// <summary>Injectable child-process seam: returns (exitCode, stdout, stderr).
    /// Real implementation shells <c>sherpa-onnx-offline</c>; tests inject a fake
    /// so no process is ever spawned.</summary>
    internal delegate Task<(int ExitCode, string StdOut, string StdErr)> ProcessRunner(
        string fileName, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct);

    // NeMo transducer hard-requires greedy_search in sherpa-onnx; hotwords would
    // require modified_beam_search, which it rejects. See class remarks.
    private const string DecodingMethod = "greedy_search";
    private const string ModelType = "nemo_transducer";
    private const bool HotwordsSupported = false; // greedy decoding ⇒ no hotwords

    // Model files inside the resolved model dir (sherpa int8 NeMo transducer).
    internal const string EncoderName = "encoder.int8.onnx";
    internal const string DecoderName = "decoder.int8.onnx";
    internal const string JoinerName = "joiner.int8.onnx";
    internal const string TokensName = "tokens.txt";

    private readonly ILogger _log;
    private readonly ProcessRunner _runner;
    private readonly Func<string?> _cliLocator;
    private readonly Func<string?> _modelDirLocator;

    public SherpaParakeetTranscriber(ILogger log)
        : this(log, RealRunner, LocateCli, LocateModelDir)
    {
        _log.LogInformation(
            "voyeur.parakeet init cli={Cli} model={Model}",
            _cliLocator() ?? "missing", _modelDirLocator() ?? "missing");
    }

    // Test/DI seam: inject a fake runner + locators so no process spawns and
    // CLI/model presence is deterministic.
    internal SherpaParakeetTranscriber(
        ILogger log, ProcessRunner runner, Func<string?> cliLocator, Func<string?> modelDirLocator)
    {
        _log = log;
        _runner = runner;
        _cliLocator = cliLocator;
        _modelDirLocator = modelDirLocator;
    }

    public SttEngineKind Kind => SttEngineKind.Parakeet;

    /// <summary>True when both the sherpa CLI and a complete Parakeet model are
    /// present right now (dynamic re-resolution — no restart needed).</summary>
    public bool Available => _cliLocator() is not null && _modelDirLocator() is not null;

    /// <summary>Sherpa CLI path, or null if not installed.</summary>
    public string? CliPath => _cliLocator();

    /// <summary>Resolved Parakeet model dir (containing the encoder/decoder/joiner
    /// + tokens), or null if incomplete/absent.</summary>
    public string? ResolvedModelDir => _modelDirLocator();

    /// <summary>Where the in-app installer drops the sherpa-onnx binaries.</summary>
    public static string BinDir => Path.Combine(ZeusAppData(), "sherpa", "bin");

    /// <summary>Where the in-app installer drops the Parakeet model.</summary>
    public static string ModelDir => Path.Combine(ZeusAppData(), "sherpa", "parakeet");

    public async Task<SttResult> TranscribeAsync(string wavPath16k, SttOptions opt, CancellationToken ct)
    {
        var cli = _cliLocator();
        var modelDir = _modelDirLocator();
        if (cli is null || modelDir is null || !File.Exists(wavPath16k))
            return SttResult.NotInstalled("Parakeet");

        // sherpa-onnx requires 16 kHz mono; resample to a throw-away copy if the
        // over isn't already at that rate (reuses the whisper path; idempotent).
        var feedPath = WhisperWav.Prepare(wavPath16k, out bool createdTempWav);
        if (feedPath is null)
        {
            _log.LogWarning("voyeur.parakeet unreadable WAV {Path} — segment left untranscribed", wavPath16k);
            return SttResult.NoSpeech();
        }

        // Build the hotwords file even though greedy decoding won't consume it —
        // keeps the artefact ready for a hotwords-capable model swap, and the
        // builder is unit-tested. Only passed to the CLI when supported.
        string? hotwordsFile = null;
        try
        {
            if (HotwordsSupported && opt.Hotwords.Count > 0)
                hotwordsFile = WriteHotwordsFile(opt.Hotwords, opt.HotwordsScore);

            var args = BuildArgs(modelDir, feedPath, opt, hotwordsFile);
            var (exit, stdout, stderr) = await _runner(cli, args, opt.EffectiveTimeout, ct);
            if (exit != 0)
                _log.LogWarning("voyeur.parakeet exit={Code} stderr={Err}", exit, Truncate(stderr));
            return ParseResult(exit, stdout, stderr, available: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return SttResult.NoSpeech();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "voyeur.parakeet transcribe failed — segment left untranscribed");
            return SttResult.NoSpeech();
        }
        finally
        {
            if (createdTempWav) TryDelete(feedPath);
            if (hotwordsFile is not null) TryDelete(hotwordsFile);
        }
    }

    // --- pure helpers (unit-tested) -----------------------------------------

    /// <summary>Build the exact <c>sherpa-onnx-offline</c> argument list.</summary>
    internal static List<string> BuildArgs(string modelDir, string feedWav, SttOptions opt, string? hotwordsFile)
    {
        var a = new List<string>
        {
            "--encoder=" + Path.Combine(modelDir, EncoderName),
            "--decoder=" + Path.Combine(modelDir, DecoderName),
            "--joiner="  + Path.Combine(modelDir, JoinerName),
            "--tokens="  + Path.Combine(modelDir, TokensName),
            "--model-type=" + ModelType,
            "--num-threads=" + opt.EffectiveThreads.ToString(CultureInfo.InvariantCulture),
            "--decoding-method=" + DecodingMethod,
            "--provider=" + NormalizeProvider(opt.Provider),
        };
        if (hotwordsFile is not null)
        {
            a.Add("--hotwords-file=" + hotwordsFile);
            a.Add("--hotwords-score=" + opt.HotwordsScore.ToString("0.###", CultureInfo.InvariantCulture));
        }
        a.Add(feedWav); // positional input wav (last)
        return a;
    }

    // sherpa accepts a fixed set of provider strings; anything unknown falls back
    // to cpu inside sherpa, but normalise the obvious cases up front.
    private static string NormalizeProvider(string? provider)
    {
        var p = (provider ?? "").Trim().ToLowerInvariant();
        return p.Length == 0 ? "cpu" : p;
    }

    /// <summary>
    /// Parse a sherpa-onnx-offline run into an <see cref="SttResult"/>. sherpa
    /// prints one JSON object per wav to STDOUT; we read its <c>"text"</c> and,
    /// when present, the <c>"tokens"</c> array. Robust to leading/trailing noise
    /// and to a nonzero exit (returns no-speech rather than throwing).
    /// </summary>
    internal static SttResult ParseResult(int exitCode, string stdout, string stderr, bool available)
    {
        if (!available) return SttResult.NotInstalled("Parakeet");

        var json = ExtractFirstJsonObject(stdout);
        if (json is null) return SttResult.NoSpeech();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? text = root.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;
            text = Clean(text);

            List<SttToken>? tokens = null;
            if (root.TryGetProperty("tokens", out var toks) && toks.ValueKind == JsonValueKind.Array)
            {
                tokens = new List<SttToken>();
                foreach (var e in toks.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.String)
                        tokens.Add(new SttToken(e.GetString() ?? "", float.NaN));
                if (tokens.Count == 0) tokens = null;
            }

            return string.IsNullOrWhiteSpace(text)
                ? SttResult.NoSpeech()
                : new SttResult(text, EngineAvailable: true, Tokens: tokens);
        }
        catch (JsonException)
        {
            return SttResult.NoSpeech();
        }
    }

    // sherpa stdout is a single JSON object; tolerate any stray prefix/suffix by
    // slicing the outermost braces. Brace-aware so embedded '}' in strings don't
    // truncate early.
    internal static string? ExtractFirstJsonObject(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        int start = s.IndexOf('{');
        if (start < 0) return null;

        int depth = 0;
        bool inStr = false, esc = false;
        for (int i = start; i < s.Length; i++)
        {
            char c = s[i];
            if (inStr)
            {
                if (esc) esc = false;
                else if (c == '\\') esc = true;
                else if (c == '"') inStr = false;
            }
            else
            {
                if (c == '"') inStr = true;
                else if (c == '{') depth++;
                else if (c == '}' && --depth == 0)
                    return s.Substring(start, i - start + 1);
            }
        }
        return null;
    }

    /// <summary>One hotword phrase per line, optional trailing <c>:score</c>.
    /// (Format per sherpa-onnx hotwords docs.) Empty/whitespace phrases dropped;
    /// internal whitespace collapsed to single spaces.</summary>
    internal static string BuildHotwordsContent(IReadOnlyList<string> phrases, float? score)
    {
        var sb = new StringBuilder();
        string? suffix = score is { } v ? " :" + v.ToString("0.###", CultureInfo.InvariantCulture) : null;
        foreach (var raw in phrases)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var phrase = string.Join(' ', raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (phrase.Length == 0) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(phrase);
            if (suffix is not null) sb.Append(suffix);
        }
        return sb.ToString();
    }

    private static string WriteHotwordsFile(IReadOnlyList<string> phrases, float score)
    {
        var path = Path.Combine(Path.GetTempPath(), "zeus-voyeur-hw-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, BuildHotwordsContent(phrases, score));
        return path;
    }

    // Collapse sherpa's (rare) bracketed non-speech tags + whitespace, mirroring
    // WhisperTranscriber.Clean so a quiet over yields empty (⇒ dropped).
    private static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var lines = text.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('[') && !l.StartsWith('('));
        return string.Join(' ', lines).Trim();
    }

    private static string Truncate(string? s, int max = 240) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");

    // --- real child-process runner ------------------------------------------

    private static async Task<(int, string, string)> RealRunner(
        string fileName, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var proc = new Process { StartInfo = psi };
        if (!proc.Start()) return (-1, "", "failed to start");

        // Below-normal priority so ASR bursts never preempt realtime threads.
        try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { /* best effort */ }

        // Read both streams concurrently to avoid a full-pipe deadlock.
        var outTask = proc.StandardOutput.ReadToEndAsync(ct);
        var errTask = proc.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
            throw;
        }

        string stdout = await outTask.ConfigureAwait(false);
        string stderr = await errTask.ConfigureAwait(false);
        return (proc.ExitCode, stdout, stderr);
    }

    // --- discovery (mirrors WhisperTranscriber) -----------------------------

    private static string? LocateCli()
    {
        var env = Environment.GetEnvironmentVariable("ZEUS_SHERPA_CLI");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;

        bool win = OperatingSystem.IsWindows();
        string[] names = win
            ? new[] { "sherpa-onnx-offline.exe" }
            : new[] { "sherpa-onnx-offline" };

        var dirs = new List<string>
        {
            BinDir,
            AppContext.BaseDirectory,
            Path.Combine(AppContext.BaseDirectory, "sherpa"),
        };
        if (!win)
        {
            dirs.Add("/opt/homebrew/bin");
            dirs.Add("/usr/local/bin");
            dirs.Add("/usr/bin");
        }
        foreach (var d in dirs)
            foreach (var n in names)
            {
                try { var p = Path.Combine(d, n); if (File.Exists(p)) return p; }
                catch { /* ignore */ }
            }

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var d in pathVar.Split(Path.PathSeparator))
            foreach (var n in names)
            {
                try { var p = Path.Combine(d, n); if (File.Exists(p)) return p; }
                catch { /* malformed PATH entry */ }
            }
        return null;
    }

    // Resolve a dir that holds a complete int8 NeMo transducer model. Honour the
    // env override, then ModelDir itself, then a single nested folder (the
    // upstream archive nests one level, e.g.
    // sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8/).
    private static string? LocateModelDir()
    {
        var env = Environment.GetEnvironmentVariable("ZEUS_PARAKEET_DIR");
        if (!string.IsNullOrWhiteSpace(env) && IsCompleteModelDir(env)) return env;

        var root = ModelDir;
        if (IsCompleteModelDir(root)) return root;
        if (!Directory.Exists(root)) return null;
        foreach (var sub in Directory.EnumerateDirectories(root))
            if (IsCompleteModelDir(sub)) return sub;
        return null;
    }

    internal static bool IsCompleteModelDir(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
        return File.Exists(Path.Combine(dir, EncoderName))
            && File.Exists(Path.Combine(dir, DecoderName))
            && File.Exists(Path.Combine(dir, JoinerName))
            && File.Exists(Path.Combine(dir, TokensName));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }

    private static string ZeusAppData()
    {
        var appData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);
        return Path.Combine(appData, "Zeus");
    }
}

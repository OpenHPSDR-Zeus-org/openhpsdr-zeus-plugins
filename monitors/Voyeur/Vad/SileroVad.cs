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
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Zeus.Server.Voyeur;

/// <summary>
/// OPTIONAL speech-activity detection for Voyeur "overs", via Silero VAD run by
/// the sherpa-onnx <c>sherpa-onnx-vad</c> CLI as a SEPARATE, SUPERVISED OS CHILD
/// PROCESS — never P/Invoke. The child-process boundary is load-bearing safety:
/// sherpa-onnx + onnxruntime are native code, and a segfault / OOM there must
/// kill only the child, never the Zeus host carrying the realtime RX/DSP/TX
/// threads. The call is bounded by a hard timeout (kill the whole tree) and runs
/// at below-normal priority, exactly like <see cref="WhisperTranscriber"/>.
///
/// GRACEFUL ABSENCE is the whole point: the energy gate in
/// <see cref="VoyeurSegmenter"/> is the always-on FLOOR. This refiner runs only
/// when the operator has installed the sherpa engine + Silero model, and returns
/// <c>null</c> the instant either is missing, the input can't be prepared, the
/// child fails, or it times out — so the caller simply keeps the energy-gate
/// boundaries. It NEVER throws into the caller.
///
/// The sherpa VAD CLI prints one segment per line to STDERR in the form
/// "<c>%.3f -- %.3f</c>" (start -- end, seconds) and requires 16 kHz mono input
/// plus a throw-away output WAV path (the silence-stripped audio, which we
/// delete). Input is prepared with <see cref="WhisperWav.Prepare"/>, reused as-is.
/// </summary>
public sealed class SileroVad
{
    /// <summary>A detected speech interval, in seconds, relative to the input WAV.</summary>
    public readonly record struct SpeechSpan(double StartSeconds, double EndSeconds)
    {
        public double DurationSeconds => Math.Max(0.0, EndSeconds - StartSeconds);
    }

    /// <summary>
    /// Seam for launching the CLI, injectable so unit tests exercise the parse /
    /// orchestration path with NO real process and NO sockets. Returns the child
    /// exit code and its captured STDERR (where the spans are printed).
    /// </summary>
    internal delegate Task<(int ExitCode, string StdErr)> CliRunner(
        string cliPath, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct);

    private static readonly Regex SpanLine = new(
        @"^\s*([0-9]+(?:\.[0-9]+)?)\s*--\s*([0-9]+(?:\.[0-9]+)?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ILogger _log;
    private readonly CliRunner _runner;

    public SileroVad(ILogger log) : this(log, RunCliAsync) { }

    // Test/DI ctor with an injectable runner.
    internal SileroVad(ILogger log, CliRunner runner)
    {
        _log = log;
        _runner = runner;
    }

    // --- Discovery (dynamic, env-overridable, no restart needed) --------------

    /// <summary>Where the sherpa engine bundle's binaries are installed. Shared
    /// with the Parakeet STT engine (same per-rid sherpa bundle).</summary>
    public static string BinDir => Path.Combine(ZeusAppData(), "sherpa", "bin");

    /// <summary>Where the Silero VAD model is installed.</summary>
    public static string ModelDir => Path.Combine(ZeusAppData(), "sherpa", "vad");

    /// <summary>The <c>sherpa-onnx-vad</c> binary path, or null if not found.</summary>
    public string? CliPath => LocateCli();

    /// <summary>The Silero VAD onnx model path, or null if none present.</summary>
    public string? ModelPath => LocateModel();

    /// <summary>True when both the VAD binary and a model are present right now.</summary>
    public bool Available => LocateCli() is not null && LocateModel() is not null;

    private static string? LocateCli()
    {
        var env = Environment.GetEnvironmentVariable("ZEUS_SHERPA_VAD_CLI");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;

        string name = OperatingSystem.IsWindows() ? "sherpa-onnx-vad.exe" : "sherpa-onnx-vad";
        var dirs = new[]
        {
            BinDir,
            AppContext.BaseDirectory,
            Path.Combine(AppContext.BaseDirectory, "sherpa"),
        };
        foreach (var d in dirs)
        {
            try { var p = Path.Combine(d, name); if (File.Exists(p)) return p; }
            catch { /* ignore malformed path */ }
        }
        return null;
    }

    private static string? LocateModel()
    {
        var env = Environment.GetEnvironmentVariable("ZEUS_SILERO_VAD_MODEL");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;

        var dir = ModelDir;
        if (!Directory.Exists(dir)) return null;
        // Prefer the float model; fall back to the int8 / v5-aliased variants.
        string[] prefs = { "silero_vad.onnx", "silero_vad_v5.onnx", "silero_vad.int8.onnx" };
        foreach (var pref in prefs)
        {
            var p = Path.Combine(dir, pref);
            if (File.Exists(p)) return p;
        }
        return Directory.EnumerateFiles(dir, "silero_vad*.onnx").FirstOrDefault();
    }

    // --- Public API -----------------------------------------------------------

    /// <summary>
    /// Detect speech spans in a captured over. Returns the spans (possibly empty)
    /// on success, or <c>null</c> when VAD is unavailable / failed — the caller
    /// then keeps the energy-gate boundaries. Never throws, never blocks past
    /// <paramref name="timeout"/>.
    /// </summary>
    public Task<IReadOnlyList<SpeechSpan>?> DetectAsync(
        string wavPath, SegSettings settings, TimeSpan timeout, CancellationToken ct)
    {
        var cli = LocateCli();
        var model = LocateModel();
        if (cli is null || model is null)
            return Task.FromResult<IReadOnlyList<SpeechSpan>?>(null);
        return RunAsync(cli, model, wavPath, settings, timeout, ct);
    }

    // Core orchestration, separated from discovery so tests can drive it with a
    // fake runner and explicit cli/model paths (no env, no real process).
    internal async Task<IReadOnlyList<SpeechSpan>?> RunAsync(
        string cliPath, string modelPath, string wavPath,
        SegSettings settings, TimeSpan timeout, CancellationToken ct)
    {
        if (!File.Exists(wavPath)) return null;

        // sherpa-onnx-vad hard-requires 16 kHz; reuse the whisper resampler.
        string? feed;
        bool createdTemp;
        try { feed = WhisperWav.Prepare(wavPath, out createdTemp); }
        catch { return null; }
        if (feed is null)
        {
            _log.LogWarning("voyeur.vad unreadable WAV {Path} — keeping energy-gate boundaries", wavPath);
            return null;
        }

        // Throw-away output WAV (the silence-stripped audio sherpa writes); we
        // only want the spans printed to stderr, so this is deleted after.
        var outWav = Path.Combine(Path.GetTempPath(), "zeus-vad-" + Guid.NewGuid().ToString("N") + ".wav");

        var args = new List<string>
        {
            "--silero-vad-model=" + modelPath,
        };
        if (settings.MaxOverSeconds > 0)
            args.Add("--silero-vad-max-speech-duration="
                + settings.MaxOverSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        args.Add(feed);    // positional: input
        args.Add(outWav);  // positional: throw-away output

        try
        {
            var (exit, stderr) = await _runner(cliPath, args, timeout, ct);
            if (exit != 0)
            {
                _log.LogWarning("voyeur.vad exit={Code} — keeping energy-gate boundaries", exit);
                return null;
            }
            return ParseSpans(stderr);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "voyeur.vad failed — keeping energy-gate boundaries");
            return null;
        }
        finally
        {
            TryDelete(outWav);
            if (createdTemp) TryDelete(feed);
        }
    }

    // --- Pure helpers (unit-tested) ------------------------------------------

    /// <summary>
    /// Parse sherpa-onnx-vad's STDERR ("<c>start -- end</c>" seconds, one per
    /// line). Non-matching lines (progress, banners) are ignored. Reversed spans
    /// are dropped. Never throws.
    /// </summary>
    internal static IReadOnlyList<SpeechSpan> ParseSpans(string? stderr)
    {
        var list = new List<SpeechSpan>();
        if (string.IsNullOrEmpty(stderr)) return list;

        foreach (var raw in stderr.Split('\n'))
        {
            var m = SpanLine.Match(raw.TrimEnd('\r'));
            if (!m.Success) continue;
            if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var s)) continue;
            if (!double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var e)) continue;
            if (e <= s) continue; // ignore zero-length / reversed
            list.Add(new SpeechSpan(s, e));
        }
        return list;
    }

    /// <summary>
    /// Reduce VAD spans to the single [start,end] that brackets ALL detected
    /// speech, optionally padded by <paramref name="guardSeconds"/> on each side
    /// and clamped to <paramref name="clampEndSeconds"/> (the over's length).
    /// Returns null when there is no speech — the caller keeps the energy-gate
    /// span. This is what the drain loop uses to TRIM a saved over to just the
    /// speech (so whisper doesn't hallucinate on leading/trailing silence).
    /// </summary>
    internal static SpeechSpan? Bracket(
        IReadOnlyList<SpeechSpan> spans, double guardSeconds = 0.0, double clampEndSeconds = double.PositiveInfinity)
    {
        if (spans is null || spans.Count == 0) return null;
        double start = double.PositiveInfinity, end = double.NegativeInfinity;
        foreach (var sp in spans)
        {
            if (sp.StartSeconds < start) start = sp.StartSeconds;
            if (sp.EndSeconds > end) end = sp.EndSeconds;
        }
        start = Math.Max(0.0, start - Math.Max(0.0, guardSeconds));
        end = Math.Min(clampEndSeconds, end + Math.Max(0.0, guardSeconds));
        if (end <= start) return null;
        return new SpeechSpan(start, end);
    }

    // --- Default real CLI runner ---------------------------------------------

    private static async Task<(int ExitCode, string StdErr)> RunCliAsync(
        string cliPath, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = cliPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        if (!proc.Start()) return (-1, "");

        // Below-normal priority so a VAD burst never preempts realtime threads.
        try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { /* best effort */ }

        // Drain BOTH pipes concurrently to avoid a full-buffer deadlock; spans
        // are on stderr, stdout is discarded.
        var errTask = proc.StandardError.ReadToEndAsync(ct);
        var outTask = proc.StandardOutput.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
            return (-1, "");
        }

        string err;
        try { err = await errTask; _ = await outTask; }
        catch { err = ""; }
        return (proc.ExitCode, err);
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

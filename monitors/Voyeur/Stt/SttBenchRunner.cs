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
using Microsoft.Extensions.Logging.Abstractions;

namespace Zeus.Server.Voyeur;

/// <summary>
/// A/B bench harness KB2UKA runs on REAL HF net audio to compare STT engines.
/// Given a folder of <c>.wav</c> overs (each with an OPTIONAL sibling
/// <c>&lt;name&gt;.txt</c> ground-truth callsign / transcript), it runs EVERY
/// supplied <see cref="ISttEngine"/> over every wav and emits one CSV row per
/// (file, engine): the transcript, whether a callsign was extractable, the
/// wall-clock ms, and (when a truth file exists) whether the expected callsign
/// was recovered.
///
/// Pure orchestration over <see cref="ISttEngine"/> — it spawns no processes
/// itself, so it's fully unit-testable with fake engines. Engines that aren't
/// installed simply report unavailable rows rather than failing the run. This is
/// a developer/operator tool, NOT part of the live capture path.
/// </summary>
public sealed class SttBenchRunner
{
    /// <summary>One bench measurement.</summary>
    public sealed record BenchRow(
        string File,
        string Engine,
        string Text,
        bool CallsignHit,
        long Ms,
        bool Available,
        bool? TruthMatch);

    private readonly IReadOnlyList<ISttEngine> _engines;

    public SttBenchRunner(params ISttEngine[] engines)
    {
        _engines = engines ?? Array.Empty<ISttEngine>();
    }

    /// <summary>
    /// Run every engine over every <c>.wav</c> in <paramref name="folder"/>.
    /// Reads an optional ground-truth string from a sibling <c>.txt</c>.
    /// Never throws on a single file/engine failure — that row records the
    /// failure and the run continues.
    /// </summary>
    public async Task<IReadOnlyList<BenchRow>> RunAsync(string folder, SttOptions opt, CancellationToken ct = default)
    {
        var rows = new List<BenchRow>();
        if (!Directory.Exists(folder)) return rows;

        var wavs = Directory.EnumerateFiles(folder, "*.wav")
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        foreach (var wav in wavs)
        {
            ct.ThrowIfCancellationRequested();
            string? truth = ReadTruth(wav);

            foreach (var engine in _engines)
            {
                var sw = Stopwatch.StartNew();
                SttResult result;
                try
                {
                    result = await engine.TranscribeAsync(wav, opt, ct);
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    result = new SttResult(null, EngineAvailable: engine.Available, Error: ex.Message);
                }
                sw.Stop();

                var text = result.Text ?? "";
                bool hit = CallsignExtractor.Extract(text).Count > 0;
                bool? truthMatch = truth is null ? null : MatchesTruth(text, truth);

                rows.Add(new BenchRow(
                    File: Path.GetFileName(wav),
                    Engine: engine.Kind.ToString(),
                    Text: text,
                    CallsignHit: hit,
                    Ms: sw.ElapsedMilliseconds,
                    Available: result.EngineAvailable,
                    TruthMatch: truthMatch));
            }
        }
        return rows;
    }

    /// <summary>Run the bench and write the CSV to <paramref name="csvPath"/>.</summary>
    public async Task<IReadOnlyList<BenchRow>> RunToCsvAsync(
        string folder, string csvPath, SttOptions opt, CancellationToken ct = default)
    {
        var rows = await RunAsync(folder, opt, ct);
        await File.WriteAllTextAsync(csvPath, ToCsv(rows), ct);
        return rows;
    }

    /// <summary>Truth match: the expected callsign (any token in the truth file
    /// that looks like a callsign, else the whole truth string) appears among the
    /// extracted candidates. Case-insensitive, whitespace/punctuation tolerant.</summary>
    internal static bool MatchesTruth(string transcriptText, string truth)
    {
        var expected = ExpectedCallsigns(truth);
        if (expected.Count == 0) return false;

        var got = new HashSet<string>(CallsignExtractor.Extract(transcriptText), StringComparer.OrdinalIgnoreCase);
        // Also treat the raw transcript as a fallback haystack so a truth string
        // that isn't a clean callsign (e.g. a phrase) can still match a literal
        // occurrence.
        foreach (var e in expected)
            if (got.Contains(e) || transcriptText.Contains(e, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    // Pull callsign-shaped tokens out of a truth string; if none look like a
    // callsign, fall back to the trimmed whole string as a single expectation.
    internal static IReadOnlyList<string> ExpectedCallsigns(string truth)
    {
        if (string.IsNullOrWhiteSpace(truth)) return Array.Empty<string>();
        var calls = CallsignExtractor.Extract(truth);
        if (calls.Count > 0) return calls;
        var trimmed = truth.Trim();
        return trimmed.Length == 0 ? Array.Empty<string>() : new[] { trimmed };
    }

    /// <summary>Render rows as RFC-4180-style CSV (header + escaped fields).</summary>
    public static string ToCsv(IEnumerable<BenchRow> rows)
    {
        var sb = new StringBuilder();
        sb.Append("file,engine,text,callsign_hit,ms,available,truth_match\n");
        foreach (var r in rows)
        {
            sb.Append(Esc(r.File)).Append(',');
            sb.Append(Esc(r.Engine)).Append(',');
            sb.Append(Esc(r.Text)).Append(',');
            sb.Append(r.CallsignHit ? "1" : "0").Append(',');
            sb.Append(r.Ms.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(r.Available ? "1" : "0").Append(',');
            sb.Append(r.TruthMatch is null ? "" : (r.TruthMatch.Value ? "1" : "0"));
            sb.Append('\n');
        }
        return sb.ToString();
    }

    // Quote fields containing comma/quote/CR/LF; double embedded quotes.
    internal static string Esc(string? field)
    {
        var s = field ?? "";
        if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    private static string? ReadTruth(string wavPath)
    {
        try
        {
            var txt = Path.ChangeExtension(wavPath, ".txt");
            if (!File.Exists(txt)) return null;
            var content = File.ReadAllText(txt).Trim();
            return content.Length == 0 ? null : content;
        }
        catch { return null; }
    }

    /// <summary>Convenience: a bench over the two stock engines with a default
    /// logger. (CLI/host entry points wire this up.)</summary>
    public static SttBenchRunner ForStockEngines(WhisperTranscriber whisper) =>
        new(new WhisperSttEngine(whisper, NullLogger.Instance),
            new SherpaParakeetTranscriber(NullLogger.Instance));
}

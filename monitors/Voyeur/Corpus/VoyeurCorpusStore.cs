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

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Zeus.Server.Voyeur;

/// <summary>
/// Training-corpus retention for Voyeur Mode (zeus-la5). When an over's callsign
/// attribution is OPERATOR-CONFIRMED (QRZ-validated — the "confirmed" state the
/// transcription pipeline assigns), this banks a durable, ground-truthed
/// (audio, callsign, transcript) example so a future ham-specific ASR fine-tune
/// is possible from real net traffic.
///
/// <para>What it does, and nothing more:</para>
/// <list type="bullet">
///   <item>Down-converts the over to 16 kHz mono via the same
///   <see cref="WhisperWav"/> the transcriber uses, and copies that clip into a
///   <c>corpus/</c> dir under the Voyeur data root (the original recording is
///   left untouched).</item>
///   <item>Appends one JSON line per clip to <c>corpus.jsonl</c>
///   (<c>{wavRelPath, callsign, transcript, freqHz, band, utc, engine}</c>) —
///   a standard JSONL manifest a training script reads directly.</item>
///   <item>Bounds disk use: once the bank exceeds <see cref="CorpusSettings.MaxClips"/>
///   the oldest clips (audio + index lines) are pruned.</item>
/// </list>
///
/// <para>This is low-risk persistence only — filesystem + a text index, no DB,
/// no network, no audio/DSP/TX path. It is DEFAULT-OFF
/// (<see cref="CorpusSettings.RetainCorpus"/> = false); until the operator opts
/// in, <see cref="Bank"/> is a no-op and not a single byte is written. All
/// access is serialized under <c>_gate</c> because banking happens on the
/// single transcription worker while the REST thread may read
/// <see cref="GetStats"/>.</para>
/// </summary>
public sealed class VoyeurCorpusStore
{
    /// <summary>JSONL manifest file name inside the corpus dir.</summary>
    public const string IndexFileName = "corpus.jsonl";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        // Single-line output — one JSON object per JSONL record.
        WriteIndented = false,
    };

    private readonly object _gate = new();
    private readonly string _corpusRoot;
    private readonly ILogger _log;

    /// <summary>Live settings. The plugin updates this when the operator changes
    /// the toggle/cap; <see cref="Bank"/> reads it on every call so a change
    /// takes effect immediately with no restart.</summary>
    public CorpusSettings Settings { get; set; }

    public VoyeurCorpusStore(string corpusRoot, ILogger log, CorpusSettings? settings = null)
    {
        _corpusRoot = Path.GetFullPath(corpusRoot);
        _log = log;
        Settings = settings ?? new CorpusSettings();
    }

    /// <summary>Absolute corpus dir. Created lazily on first successful bank — an
    /// operator who never opts in gets no directory.</summary>
    public string CorpusRoot => _corpusRoot;

    /// <summary>Absolute path to the JSONL manifest.</summary>
    public string IndexPath => Path.Combine(_corpusRoot, IndexFileName);

    /// <summary>
    /// Bank one operator-confirmed over. No-op (returns null) when retention is
    /// off, the callsign is blank, or the source WAV can't be read. On success
    /// returns the clip's relative path (bare file name) as written to the index.
    /// Never throws into the caller — a corpus failure must never disturb the
    /// transcription worker.
    /// </summary>
    public string? Bank(
        string sourceWavPath, string callsign, string? transcript,
        long freqHz, string band, string engine, DateTime utc)
    {
        var settings = Settings;
        if (!settings.RetainCorpus) return null;
        if (string.IsNullOrWhiteSpace(callsign)) return null;
        if (string.IsNullOrWhiteSpace(sourceWavPath) || !File.Exists(sourceWavPath)) return null;

        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(_corpusRoot);

                // Reuse the transcriber's 16 kHz mono prep so the banked clip is
                // exactly what an ASR pipeline trains on. Prepare returns the
                // source unchanged when it is already 16 kHz (createdTemp=false),
                // else a throw-away temp we copy from and delete.
                var prepared = WhisperWav.Prepare(sourceWavPath, out bool createdTemp);
                if (prepared is null) return null; // unparseable WAV — skip
                try
                {
                    var fileName = BuildClipName(callsign, utc);
                    var destPath = SafeCorpusPath(fileName);
                    File.Copy(prepared, destPath, overwrite: true);

                    var entry = new CorpusEntry(
                        WavRelPath: fileName,
                        Callsign: callsign.Trim(),
                        Transcript: transcript,
                        FreqHz: freqHz,
                        Band: band ?? "",
                        Utc: utc,
                        Engine: string.IsNullOrWhiteSpace(engine) ? "unknown" : engine);

                    File.AppendAllText(IndexPath,
                        JsonSerializer.Serialize(entry, JsonOpts) + "\n");

                    PruneLocked(settings.MaxClips);
                    return fileName;
                }
                finally
                {
                    if (createdTemp) TryDelete(prepared);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "voyeur.corpus bank failed call={Call}", callsign);
            return null;
        }
    }

    /// <summary>Current corpus size for the status endpoint: clip count (index
    /// lines), the configured cap, total clip bytes on disk, the absolute path,
    /// and the live retain flag. Cheap; safe when nothing has been banked yet.</summary>
    public CorpusStats GetStats()
    {
        lock (_gate)
        {
            int clips = 0;
            long bytes = 0;
            if (File.Exists(IndexPath))
            {
                foreach (var line in File.ReadLines(IndexPath))
                    if (!string.IsNullOrWhiteSpace(line)) clips++;
            }
            if (Directory.Exists(_corpusRoot))
            {
                foreach (var f in Directory.EnumerateFiles(_corpusRoot, "*.wav"))
                {
                    try { bytes += new FileInfo(f).Length; } catch { /* raced delete */ }
                }
            }
            return new CorpusStats(Settings.RetainCorpus, clips, Settings.MaxClips, bytes, _corpusRoot);
        }
    }

    // Evict oldest clips beyond the cap. The index is append-only chronological,
    // so the first N lines are the oldest. We delete their WAVs, then rewrite the
    // index with the survivors. Caller holds _gate.
    private void PruneLocked(int maxClips)
    {
        if (maxClips <= 0) return; // 0/negative cap = unbounded; never prune
        if (!File.Exists(IndexPath)) return;

        var lines = File.ReadAllLines(IndexPath)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();
        if (lines.Count <= maxClips) return;

        int dropCount = lines.Count - maxClips;
        for (int i = 0; i < dropCount; i++)
        {
            var name = TryReadRelPath(lines[i]);
            if (name is null) continue;
            try
            {
                var p = SafeCorpusPath(Path.GetFileName(name));
                if (File.Exists(p)) File.Delete(p);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "voyeur.corpus prune: failed to delete {Name}", name);
            }
        }

        // Rewrite atomically: survivors → temp → replace, so a crash mid-prune
        // can't truncate the manifest.
        var survivors = lines.Skip(dropCount);
        var tmp = IndexPath + ".tmp";
        // Keep the manifest single-newline ('\n') regardless of platform so the
        // JSONL stays byte-stable for a training script that reads it elsewhere.
        File.WriteAllText(tmp, string.Concat(survivors.Select(l => l + "\n")));
        File.Move(tmp, IndexPath, overwrite: true);
        _log.LogInformation("voyeur.corpus pruned {Dropped} oldest clips (cap {Cap})", dropCount, maxClips);
    }

    private static string? TryReadRelPath(string jsonLine)
    {
        try
        {
            var e = JsonSerializer.Deserialize<CorpusEntry>(jsonLine, JsonOpts);
            return e?.WavRelPath;
        }
        catch { return null; }
    }

    // Server-generated, but traversal-guarded as defense-in-depth: a crafted
    // callsign can never escape the corpus root. Mirrors VoyeurStore's guards.
    private string SafeCorpusPath(string fileName)
    {
        var name = Path.GetFileName(fileName); // strip any separators
        if (string.IsNullOrEmpty(name)) name = "clip.wav";
        var full = Path.GetFullPath(Path.Combine(_corpusRoot, name));
        var rootFull = Path.GetFullPath(_corpusRoot) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootFull, StringComparison.Ordinal))
            throw new InvalidOperationException("corpus path escaped the corpus root");
        return full;
    }

    private static string BuildClipName(string callsign, DateTime utc)
    {
        var safe = SanitizeCallsign(callsign);
        var ts = utc.ToUniversalTime().ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        var rand = Guid.NewGuid().ToString("N").Substring(0, 8);
        return $"{safe}-{ts}-{rand}.wav";
    }

    // Keep letters/digits only (callsigns are A-Z/0-9 by construction); this is
    // belt-and-suspenders so a hostile transcript-derived string can't inject a
    // path separator into the file name.
    private static string SanitizeCallsign(string callsign)
    {
        Span<char> buf = stackalloc char[callsign.Length];
        int n = 0;
        foreach (var c in callsign)
            if (char.IsLetterOrDigit(c)) buf[n++] = char.ToUpperInvariant(c);
        return n == 0 ? "unknown" : new string(buf[..n]);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }
}

/// <summary>One JSONL manifest record. Serialized camelCase →
/// <c>{wavRelPath, callsign, transcript, freqHz, band, utc, engine}</c>.</summary>
internal sealed record CorpusEntry(
    string WavRelPath,
    string Callsign,
    string? Transcript,
    long FreqHz,
    string Band,
    DateTime Utc,
    string Engine);

/// <summary>Corpus size snapshot for the status endpoint.</summary>
public sealed record CorpusStats(
    bool RetainCorpus,
    int ClipCount,
    int MaxClips,
    long Bytes,
    string Path);

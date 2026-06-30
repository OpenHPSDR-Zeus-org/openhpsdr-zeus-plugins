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

using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Zeus.Plugins.Contracts;

namespace Zeus.Server.Voyeur;

/// <summary>
/// Voyeur Mode (zeus-la5) Phase 2 — the transcription + enrichment pipeline.
/// Captured "overs" (Phase 1) are enqueued here; a SINGLE background worker
/// pulls each one, runs whisper.cpp (in a supervised child process — see
/// <see cref="WhisperTranscriber"/>), extracts candidate callsigns, validates
/// them against QRZ, and writes the transcript + the best attribution back to
/// the segment record.
///
/// This runs entirely OFF the audio/DSP path — it consumes already-saved WAV
/// files, never the live ring — so nothing here can affect RX/PS/TX. A single
/// worker bounds CPU/RAM (whisper at ~20× realtime keeps up with a net easily);
/// the queue is bounded and drops oldest on overflow so a slow run can't grow
/// memory without limit. QRZ lookups are de-duplicated per session and rate-
/// limited, because <c>QrzService.LookupAsync</c> serializes every caller on a
/// single gate — an unthrottled roster storm would starve the operator's own
/// manual lookups.
/// </summary>
public sealed class VoyeurTranscriptionService
{
    public readonly record struct Job(
        string SessionId, string SegmentId, string WavPath, int DurationMs,
        long FreqHz, string Band);

    private readonly ISttEngine _whisper;
    private readonly ISttEngine _parakeet;
    private readonly Func<EngineSettings> _engineSettings;
    private readonly VoyeurStore _store;
    private readonly IQrzLookup? _qrz;
    private readonly ILogger _log;
    private readonly CallsignSettings _callsigns;
    private readonly VoyeurCorpusStore? _corpus;
    private readonly Alerts.VoyeurAlertService? _alerts;
    private readonly SileroVad? _vad;

    // Segmentation settings (only UseVad is read here — the optional VAD refine).
    // Set via Configure on session start; volatile so the worker sees updates.
    private volatile SegSettings _segSettings = new();

    private readonly Channel<Job> _queue = Channel.CreateBounded<Job>(
        new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

    // Per-callsign QRZ cache + a min interval between live lookups. The worker
    // is single-threaded so this needs no locking.
    private readonly Dictionary<string, (QrzLookupResult? Station, bool Valid)> _qrzCache = new(StringComparer.Ordinal);
    private const int QrzCacheCap = 2000; // bound the per-lifetime QRZ cache
    private DateTime _lastQrzLookup = DateTime.MinValue;
    private static readonly TimeSpan QrzMinInterval = TimeSpan.FromMilliseconds(600);

    public VoyeurTranscriptionService(
        ISttEngine whisper,
        ISttEngine parakeet,
        Func<EngineSettings> engineSettings,
        VoyeurStore store,
        IQrzLookup? qrz,
        ILogger log,
        CallsignSettings? callsigns = null,
        VoyeurCorpusStore? corpus = null,
        Alerts.VoyeurAlertService? alerts = null,
        SileroVad? vad = null)
    {
        _whisper = whisper;
        _parakeet = parakeet;
        _engineSettings = engineSettings;
        _store = store;
        _qrz = qrz;
        _log = log;
        _callsigns = callsigns ?? new CallsignSettings();
        _corpus = corpus;
        _alerts = alerts;
        _vad = vad;
    }

    /// <summary>True when at least one STT engine is installed (whisper OR
    /// parakeet). Capture-only when neither is present.</summary>
    public bool Available => _whisper.Available || _parakeet.Available;

    /// <summary>Push the live segmentation settings (only <c>UseVad</c> is read
    /// here, for the optional, fail-safe VAD refine). Called on session start.</summary>
    public void Configure(SegSettings settings) => _segSettings = settings ?? new SegSettings();

    /// <summary>Queue a captured over for transcription. Non-blocking; drops
    /// silently if transcription is unavailable (capture-only mode).</summary>
    public void Enqueue(Job job)
    {
        if (!Available) return;
        _queue.Writer.TryWrite(job);
    }

    /// <summary>Run the single-worker transcription loop until cancelled. The
    /// plugin entry starts this on a Task in InitializeAsync and cancels it in
    /// ShutdownAsync (replaces the BackgroundService host machinery).</summary>
    public async Task RunAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A failed transcription must never take the worker down — the
                // over just stays untranscribed.
                _log.LogWarning(ex, "voyeur.transcribe job failed seg={Seg}", job.SegmentId);
            }
        }
    }

    private async Task ProcessAsync(Job job, CancellationToken ct)
    {
        // Select the active engine (Whisper locked default; Parakeet opt-in) with
        // graceful fallback when the chosen one isn't installed.
        var settings = _engineSettings();
        var engine = SttEngineSelector.Pick(settings, _whisper, _parakeet);
        var engineName = engine.Kind.ToString().ToLowerInvariant();

        // Seed the decode with the live CONFIRMED roster (helps the whisper
        // prompt; no-op for greedy Parakeet). Confirmed-only — see SessionRoster.
        // Fetched ONCE per over and reused for attribution below (was queried
        // twice — two locked LiteDB scans per over).
        var roster = _store.SessionRoster(job.SessionId);
        var hotwords = roster as IReadOnlyList<string> ?? roster.ToList();

        // Generous timeout: ASR runs well above realtime, so even a long over
        // finishes in seconds. Cap at the larger of 60 s or 2× the over length
        // and kill+drop past that (a wedged child must never block the queue).
        var opt = new SttOptions(
            Hotwords: hotwords,
            Provider: settings.ResolveProvider(),
            Threads: Math.Min(4, Environment.ProcessorCount),
            Timeout: TimeSpan.FromMilliseconds(Math.Max(60_000, job.DurationMs * 2)));

        // OPTIONAL VAD refine (fail-SAFE). When the operator opted in AND the
        // sherpa engine + Silero model are installed, run VAD off the audio path
        // and TRIM the saved over to just the bracketed speech before STT, so
        // whisper/Parakeet don't hallucinate on leading/trailing silence. HARD
        // RULE: VAD must NEVER skip transcription — null/empty spans, an
        // unreadable WAV, a child error, or ANY exception all fall through to the
        // ORIGINAL full energy-gate WAV, which is always transcribed.
        string sttWav = job.WavPath;
        string? vadTemp = null;
        if (_segSettings.UseVad && _vad is not null && _vad.Available)
        {
            try
            {
                var spans = await _vad.DetectAsync(job.WavPath, _segSettings, TimeSpan.FromSeconds(30), ct);
                sttWav = ResolveSttWav(job.WavPath, spans, job.DurationMs / 1000.0, out vadTemp);
            }
            catch (Exception ex) { _log.LogDebug(ex, "voyeur.vad refine failed (ignored) — using full over"); }
        }

        SttResult result;
        try
        {
            result = await engine.TranscribeAsync(sttWav, opt, ct);
        }
        finally
        {
            if (vadTemp is not null) TryDelete(vadTemp);
        }
        var transcript = result.Text;

        if (string.IsNullOrWhiteSpace(transcript))
        {
            // No speech (quiet/garbled over) — record nothing to attribute; leave
            // the segment as captured-only.
            _store.UpdateSegmentTranscript(job.SegmentId, transcript: null,
                callsign: null, callsignState: "unknown", callsignName: null);
            return;
        }

        var (callsign, state, name) = await AttributeAsync(transcript, job.SessionId, roster, ct);
        _store.UpdateSegmentTranscript(job.SegmentId, transcript, callsign, state, name);
        _log.LogDebug("voyeur.transcribe seg={Seg} engine={Engine} call={Call} state={State}",
            job.SegmentId, engineName, callsign, state);

        // Watchword alerts: fire-and-forget enqueue (off this worker). No-op when
        // alerts are disabled / unconfigured.
        _alerts?.OnTranscribed(job.SessionId, job.SegmentId, transcript, callsign);

        // Training-corpus retention: bank only OPERATOR-CONFIRMED (QRZ-validated)
        // attributions, tagged with the ACTIVE engine. No-op unless the operator
        // opted in.
        if (state == "confirmed" && callsign is not null)
            _corpus?.Bank(job.WavPath, callsign, transcript, job.FreqHz, job.Band,
                          engine: engineName, utc: DateTime.UtcNow);
    }

    // Pick the best callsign from the transcript and validate it against QRZ.
    // confirmed = QRZ resolves it (real licensee); tentative = well-formed but
    // QRZ has no record (DX/foreign/unlisted); unknown = nothing decodable.
    private async Task<(string? callsign, string state, string? name)> AttributeAsync(
        string transcript, string sessionId, IReadOnlyCollection<string> roster, CancellationToken ct)
    {
        var candidates = CallsignExtractor.Extract(transcript);
        if (candidates.Count == 0) return (null, "unknown", null);

        // Snap each raw decode to the live CONFIRMED "likely on frequency" set
        // (corrects a 1-char ASR miss onto a known call), preserving extractor
        // rank + dedup. Snapping never elevates state on its own — every snapped
        // candidate still goes through QRZ below, so a snap to a confirmed roster
        // call only resolves "confirmed" via that call's own QRZ confirmation
        // (cached), never by laundering. The roster is fetched once in ProcessAsync
        // and threaded through here.
        var snapped = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cand in candidates)
        {
            var m = CallsignMatcher.Match(cand, roster, _callsigns);
            var call = m?.Callsign ?? cand;     // keep verbatim if the matcher rejects
            if (seen.Add(call)) snapped.Add(call);
        }

        // Try candidates in rank order; the first QRZ-confirmed one wins
        // (longest-validated, per the Phase-0 fragment-collision finding).
        foreach (var cand in snapped.Take(5))
        {
            var (station, valid) = await QrzLookupAsync(cand, ct);
            if (valid)
            {
                var name = station?.FirstName ?? station?.Name;
                return (cand, "confirmed", name);
            }
        }

        // None confirmed — surface the top candidate as tentative so the
        // operator sees a best-guess they can verify, clearly marked.
        return (snapped[0], "tentative", null);
    }

    private async Task<(QrzLookupResult? station, bool valid)> QrzLookupAsync(string callsign, CancellationToken ct)
    {
        if (_qrzCache.TryGetValue(callsign, out var cached)) return (cached.Station, cached.Valid);

        // No host QRZ access (NetworkAccess not granted / no subscription) →
        // can't validate; treat as unconfirmed so the callsign surfaces tentative.
        if (_qrz is null) return (null, false);

        // Rate-limit: never hammer QRZ's shared gate and starve manual lookups.
        var since = DateTime.UtcNow - _lastQrzLookup;
        if (since < QrzMinInterval)
            await Task.Delay(QrzMinInterval - since, ct);
        _lastQrzLookup = DateTime.UtcNow;

        QrzLookupResult? station = null;
        try { station = await _qrz.LookupAsync(callsign, ct); }
        catch (Exception ex) { _log.LogDebug(ex, "voyeur.qrz lookup failed {Call}", callsign); }

        bool valid = station is not null;
        // Bound the cache so a multi-day session of garbled candidate strings
        // can't leak memory. Clear-on-cap (cheap; single worker) — the worst case
        // is a few repeat lookups after a flush, never an unbounded dictionary.
        if (_qrzCache.Count >= QrzCacheCap) _qrzCache.Clear();
        _qrzCache[callsign] = (station, valid);
        return (station, valid);
    }

    /// <summary>
    /// Decide which WAV to feed STT given the VAD spans. When VAD produced usable
    /// speech, <see cref="SileroVad.Bracket"/> + <see cref="WhisperWav.TrimToTemp"/>
    /// yield a trimmed temp copy (set in <paramref name="tempToDelete"/>);
    /// otherwise — null/empty spans, an empty bracket, or a trim failure — the
    /// ORIGINAL full WAV is returned and no temp is created. The caller ALWAYS
    /// transcribes the returned path: VAD never skips transcription. Pure +
    /// unit-tested.
    /// </summary>
    internal static string ResolveSttWav(
        string originalWav, IReadOnlyList<SileroVad.SpeechSpan>? spans,
        double overSeconds, out string? tempToDelete)
    {
        tempToDelete = null;
        if (spans is null || spans.Count == 0) return originalWav;
        var bracket = SileroVad.Bracket(
            spans, guardSeconds: 0.2,
            clampEndSeconds: overSeconds > 0 ? overSeconds : double.PositiveInfinity);
        if (bracket is null) return originalWav;
        var trimmed = WhisperWav.TrimToTemp(originalWav, bracket.Value.StartSeconds, bracket.Value.EndSeconds);
        if (trimmed is null) return originalWav; // trim failed → keep full over
        tempToDelete = trimmed;
        return trimmed;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}

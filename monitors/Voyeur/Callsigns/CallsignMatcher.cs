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

namespace Zeus.Server.Voyeur;

/// <summary>
/// The result of snapping a raw decoded callsign candidate to the live set of
/// likely calls. <see cref="Confidence"/> is in 0..1; <see cref="Snapped"/> is
/// true when the returned call differs from the raw candidate (i.e. it was
/// corrected against the roster); <see cref="Validated"/> is true when the call
/// passes <see cref="ItuCallsign"/> structure validation.
/// </summary>
public readonly record struct CallsignMatch(
    string Callsign,
    double Confidence,
    bool Snapped,
    bool Validated);

/// <summary>
/// Pure callsign corrector for Voyeur Mode callsign accuracy (zeus-la5).
///
/// ASR of HF SSB is greedy and noisy: a real call routinely comes back one
/// character off ("KB2UKA" → "KB2OKA", "N9WAR" → "M9WAR") or as a lowercase /
/// spaced fragment. This stage takes ONE decoded candidate plus a LIVE SET of
/// likely calls — the session roster, QRZ-confirmed attributions, and any spot
/// set the caller supplies — and snaps the candidate to the best known call:
///
///   1. <b>Phonetic / skeleton blocking</b> — both the candidate and each known
///      call are reduced to a confusion-class skeleton (aurally-confusable
///      letters and the 0/O, I/Y/1 swaps collapse to one representative). A
///      matching skeleton is a strong signal and adds a confidence bonus.
///   2. <b>Jaro-Winkler + edit-distance ranking</b> — the candidate is ranked
///      against the roster by Jaro-Winkler similarity (which rewards a shared
///      prefix, exactly how callsign prefixes behave), gated by a bounded
///      Levenshtein edit distance so only a genuine 1–2 char miss snaps.
///   3. <b>ITU structure validation</b> — via <see cref="ItuCallsign"/>,
///      including digit-first prefixes (2E0, 4X1, 9A1) the old anchor dropped.
///
/// There is NO network access here — the caller is responsible for building the
/// candidate set (e.g. from <c>VoyeurStore</c> roster + QRZ). When fuzzy matching
/// is disabled or the roster is empty, a structurally-valid candidate is returned
/// on its own at a lower confidence, and garbage is rejected (null).
/// </summary>
public static class CallsignMatcher
{
    // Minimum Jaro-Winkler similarity before we'll consider snapping a candidate
    // onto a roster call. 0.84 admits a single substitution / transposition in a
    // typical 4–6 char call while rejecting unrelated calls.
    private const double SnapThreshold = 0.84;

    // Confidence floor for a structurally-valid candidate that matched no roster
    // call (well-formed but unconfirmed on this frequency).
    private const double StructureOnlyConfidence = 0.55;

    // Bonus added when the candidate and the snapped call share a phonetic
    // skeleton (strong evidence the miss is a confusable-letter error).
    private const double SkeletonBonus = 0.08;

    // A snap can never report full certainty — that's reserved for an exact
    // roster hit.
    private const double MaxSnapConfidence = 0.98;

    /// <summary>
    /// Correct <paramref name="candidate"/> against the live <paramref name="roster"/>
    /// of likely calls. Returns the best match, or null when the candidate is
    /// neither a structurally-valid call nor close enough to any roster entry.
    /// Pure; never throws.
    /// </summary>
    public static CallsignMatch? Match(
        string? candidate,
        IReadOnlyCollection<string> roster,
        CallsignSettings settings)
    {
        var norm = Normalize(candidate);
        if (norm is null) return null;

        // 1) Exact roster hit (case-insensitive, base-call normalised) → certain.
        foreach (var r in roster)
        {
            var rn = Normalize(r);
            if (rn is not null && string.Equals(rn, norm, StringComparison.Ordinal))
                return new CallsignMatch(rn, 1.0, Snapped: false, Validated: ItuCallsign.IsValid(rn));
        }

        // 2) Fuzzy snap against the roster.
        if (settings.FuzzyMatchToRoster && roster.Count > 0)
        {
            var snap = BestSnap(norm, roster, settings);
            if (snap is not null) return snap;
        }

        // 3) No snap — accept a structurally-valid candidate on its own merit,
        //    reject anything that isn't a plausible call.
        if (ItuCallsign.IsValid(norm))
            return new CallsignMatch(norm, StructureOnlyConfidence, Snapped: false, Validated: true);

        return null;
    }

    private static CallsignMatch? BestSnap(
        string norm, IReadOnlyCollection<string> roster, CallsignSettings settings)
    {
        var candSkel = Skeleton(norm);

        // Score every roster call, then keep the top BeamSize before gating —
        // honours the configured beam width and keeps the winner deterministic.
        var scored = new List<(string Call, double Score, int Edits)>(roster.Count);
        foreach (var r in roster)
        {
            var rn = Normalize(r);
            if (rn is null || rn.Length == 0) continue;

            double jw = JaroWinkler(norm, rn);
            bool skelEq = string.Equals(Skeleton(rn), candSkel, StringComparison.Ordinal);
            double score = Math.Min(1.0, jw + (skelEq ? SkeletonBonus : 0));
            int edits = Levenshtein(norm, rn);
            scored.Add((rn, score, edits));
        }
        if (scored.Count == 0) return null;

        IEnumerable<(string Call, double Score, int Edits)> ranked = scored
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Edits)
            .ThenBy(s => s.Call, StringComparer.Ordinal);
        if (settings.BeamSize > 0) ranked = ranked.Take(settings.BeamSize);

        foreach (var (call, score, edits) in ranked)
        {
            if (score < SnapThreshold) break; // ranked desc — nothing better remains
            if (edits > MaxEdits(Math.Max(norm.Length, call.Length))) continue;
            if (!ItuCallsign.IsValid(call)) continue;

            double confidence = string.Equals(call, norm, StringComparison.Ordinal)
                ? Math.Min(1.0, score) // candidate already equals this roster call
                : Math.Min(MaxSnapConfidence, score);
            return new CallsignMatch(call, confidence,
                Snapped: !string.Equals(call, norm, StringComparison.Ordinal),
                Validated: true);
        }
        return null;
    }

    // Allow a single edit for short calls, two for longer ones — keeps a 1-char
    // ASR miss snapping while refusing to fuse two genuinely different calls.
    private static int MaxEdits(int len) => len <= 4 ? 1 : 2;

    /// <summary>
    /// Normalise a raw token to a comparable base call: upper-cased, portable
    /// constructs reduced to the home call, otherwise stripped to alphanumerics.
    /// Returns null when nothing usable remains.
    /// </summary>
    internal static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var t = raw.Trim().ToUpperInvariant();

        if (t.Contains('/'))
        {
            var b = ItuCallsign.BaseCall(t);
            if (b is not null) return b;
        }

        Span<char> buf = stackalloc char[t.Length];
        int n = 0;
        foreach (var c in t)
            if (char.IsLetterOrDigit(c)) buf[n++] = c;
        return n == 0 ? null : new string(buf[..n]);
    }

    // ---- phonetic / skeleton blocking key ------------------------------------

    // Confusion classes for spoken/mis-heard callsign characters. Each member
    // maps to a single class representative so aurally-confusable calls share a
    // skeleton. Heuristic, deliberately coarse — it's a blocking + bonus key, not
    // the final decision (Jaro-Winkler does the fine ranking).
    private static readonly Dictionary<char, char> Confuse = BuildConfuse();

    private static Dictionary<char, char> BuildConfuse()
    {
        var m = new Dictionary<char, char>();
        void Group(char rep, string members) { foreach (var c in members) m[c] = rep; }

        Group('E', "BCDEGPTVZ3"); // the long-"ee" plosive set (B C D E G P T V Z) + 3 ("three")
        Group('A', "AJK8");        // "ay" set: Alpha/Juliet/Kilo + 8 ("eight")
        Group('S', "FSX");         // fricatives: eff / ess / ex
        Group('N', "MN");          // nasals: em / en
        Group('I', "IY15");        // eye / wye / one / five (high front confusions)
        Group('O', "O0");          // oh / zero
        Group('U', "UQW2");        // you / cue / double-u / two
        // R, L, H, and the remaining distinct digits (4,6,7,9) map to themselves.
        return m;
    }

    /// <summary>Reduce a (normalised) call to its confusion-class skeleton.</summary>
    internal static string Skeleton(string norm)
    {
        Span<char> buf = stackalloc char[norm.Length];
        for (int i = 0; i < norm.Length; i++)
            buf[i] = Confuse.TryGetValue(norm[i], out var rep) ? rep : norm[i];
        return new string(buf);
    }

    // ---- string metrics (pure, internal for direct testing) ------------------

    /// <summary>Jaro-Winkler similarity in 0..1 (1 = identical).</summary>
    internal static double JaroWinkler(string a, string b)
    {
        double jaro = Jaro(a, b);
        // Winkler boost: up to 4 leading chars in common (prefix l), scale 0.1.
        int prefix = 0;
        int max = Math.Min(4, Math.Min(a.Length, b.Length));
        while (prefix < max && a[prefix] == b[prefix]) prefix++;
        return jaro + prefix * 0.1 * (1 - jaro);
    }

    private static double Jaro(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0) return 1.0;
        if (a.Length == 0 || b.Length == 0) return 0.0;
        if (string.Equals(a, b, StringComparison.Ordinal)) return 1.0;

        int matchDistance = Math.Max(a.Length, b.Length) / 2 - 1;
        if (matchDistance < 0) matchDistance = 0;

        var aMatched = new bool[a.Length];
        var bMatched = new bool[b.Length];
        int matches = 0;

        for (int i = 0; i < a.Length; i++)
        {
            int start = Math.Max(0, i - matchDistance);
            int end = Math.Min(i + matchDistance + 1, b.Length);
            for (int j = start; j < end; j++)
            {
                if (bMatched[j] || a[i] != b[j]) continue;
                aMatched[i] = true;
                bMatched[j] = true;
                matches++;
                break;
            }
        }
        if (matches == 0) return 0.0;

        // Count transpositions.
        double transpositions = 0;
        int k = 0;
        for (int i = 0; i < a.Length; i++)
        {
            if (!aMatched[i]) continue;
            while (!bMatched[k]) k++;
            if (a[i] != b[k]) transpositions++;
            k++;
        }
        transpositions /= 2;

        double m = matches;
        return (m / a.Length + m / b.Length + (m - transpositions) / m) / 3.0;
    }

    /// <summary>Levenshtein edit distance (substitution / insert / delete = 1).</summary>
    internal static int Levenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}

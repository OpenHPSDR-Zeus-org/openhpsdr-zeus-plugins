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

using Xunit;
using Zeus.Server.Voyeur;

namespace Zeus.Server.Voyeur.Tests;

public class CallsignMatcherTests
{
    private static readonly CallsignSettings Default = new();

    private static string[] Roster(params string[] calls) => calls;

    // ---- exact roster hit ----------------------------------------------------

    [Fact]
    public void Exact_roster_hit_is_certain()
    {
        var m = CallsignMatcher.Match("KB2UKA", Roster("KB2UKA", "N9WAR"), Default);
        Assert.NotNull(m);
        Assert.Equal("KB2UKA", m!.Value.Callsign);
        Assert.Equal(1.0, m.Value.Confidence, 3);
        Assert.False(m.Value.Snapped);
        Assert.True(m.Value.Validated);
    }

    [Fact]
    public void Lowercase_candidate_snaps_to_roster_call()
    {
        // "kb2uka" should normalise + match KB2UKA exactly (case-insensitive).
        var m = CallsignMatcher.Match("kb2uka", Roster("KB2UKA"), Default);
        Assert.NotNull(m);
        Assert.Equal("KB2UKA", m!.Value.Callsign);
        Assert.Equal(1.0, m.Value.Confidence, 3);
    }

    // ---- one-char fuzzy snap -------------------------------------------------

    [Theory]
    [InlineData("M9WAR", "N9WAR")] // N→M nasal confusion, first char
    [InlineData("N9WAB", "N9WAR")] // last char off
    [InlineData("KB2OKA", "KB2UKA")] // suffix substitution
    [InlineData("KX2UKA", "KB2UKA")] // prefix substitution
    public void One_char_miss_snaps_to_roster(string candidate, string expected)
    {
        var m = CallsignMatcher.Match(candidate, Roster(expected), Default);
        Assert.NotNull(m);
        Assert.Equal(expected, m!.Value.Callsign);
        Assert.True(m.Value.Snapped);
        Assert.True(m.Value.Confidence is > 0.84 and < 1.0);
        Assert.True(m.Value.Validated);
    }

    [Fact]
    public void Phonetic_skeleton_match_boosts_confidence()
    {
        // N9WAR vs M9WAR share a phonetic skeleton (M/N nasal, W→U) → bonus.
        var withSkel = CallsignMatcher.Match("M9WAR", Roster("N9WAR"), Default);
        // K vs B do NOT share a skeleton class → no bonus.
        var noSkel = CallsignMatcher.Match("KX2UKA", Roster("KB2UKA"), Default);
        Assert.NotNull(withSkel);
        Assert.NotNull(noSkel);
        Assert.True(withSkel!.Value.Confidence > noSkel!.Value.Confidence);
    }

    // ---- structurally valid but not on the roster ----------------------------

    [Fact]
    public void Valid_call_absent_from_roster_returns_structure_only()
    {
        var m = CallsignMatcher.Match("W1AW", Roster("KB2UKA"), Default);
        Assert.NotNull(m);
        Assert.Equal("W1AW", m!.Value.Callsign);
        Assert.False(m.Value.Snapped);
        Assert.True(m.Value.Validated);
        Assert.True(m.Value.Confidence is > 0 and < 0.84);
    }

    [Theory]
    [InlineData("2E0XYZ")]
    [InlineData("4X1AB")]
    [InlineData("9A1A")]
    public void Digit_first_calls_validate_without_roster(string call)
    {
        var m = CallsignMatcher.Match(call, Roster(), Default);
        Assert.NotNull(m);
        Assert.Equal(call, m!.Value.Callsign);
        Assert.True(m.Value.Validated);
    }

    // ---- rejection -----------------------------------------------------------

    [Theory]
    [InlineData("HELLO")]
    [InlineData("QSL")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("ANDNINEWAR")]
    public void Garbage_is_rejected(string? candidate)
        => Assert.Null(CallsignMatcher.Match(candidate, Roster("KB2UKA"), Default));

    [Fact]
    public void Unrelated_valid_call_does_not_falsely_snap()
    {
        // "G4XYZ" is a valid call but nowhere near KB2UKA — must NOT snap.
        var m = CallsignMatcher.Match("G4XYZ", Roster("KB2UKA"), Default);
        Assert.NotNull(m);
        Assert.Equal("G4XYZ", m!.Value.Callsign);
        Assert.False(m.Value.Snapped);
    }

    [Fact]
    public void Fuzzy_disabled_takes_candidate_verbatim()
    {
        var settings = Default with { FuzzyMatchToRoster = false };
        var m = CallsignMatcher.Match("KB2OKA", Roster("KB2UKA"), settings);
        Assert.NotNull(m);
        // No snapping — KB2OKA is itself structurally valid, returned as-is.
        Assert.Equal("KB2OKA", m!.Value.Callsign);
        Assert.False(m.Value.Snapped);
    }

    [Fact]
    public void Portable_candidate_matches_roster_base_call()
    {
        var m = CallsignMatcher.Match("KB2UKA/P", Roster("KB2UKA"), Default);
        Assert.NotNull(m);
        Assert.Equal("KB2UKA", m!.Value.Callsign);
        Assert.Equal(1.0, m.Value.Confidence, 3);
    }

    [Fact]
    public void BeamSize_zero_scans_entire_roster()
    {
        var settings = Default with { BeamSize = 0 };
        var m = CallsignMatcher.Match("M9WAR", Roster("KB2UKA", "W1AW", "N9WAR"), settings);
        Assert.NotNull(m);
        Assert.Equal("N9WAR", m!.Value.Callsign);
    }

    // ---- pure metric sanity (internal) ---------------------------------------

    [Fact]
    public void JaroWinkler_identical_is_one()
        => Assert.Equal(1.0, CallsignMatcher.JaroWinkler("KB2UKA", "KB2UKA"), 6);

    [Fact]
    public void JaroWinkler_rewards_shared_prefix()
    {
        double shared = CallsignMatcher.JaroWinkler("KB2UKA", "KB2UKB");
        double notShared = CallsignMatcher.JaroWinkler("KB2UKA", "XB2UKA");
        Assert.True(shared > notShared);
    }

    [Fact]
    public void Levenshtein_counts_single_substitution()
        => Assert.Equal(1, CallsignMatcher.Levenshtein("KB2UKA", "KB2OKA"));

    [Fact]
    public void Skeleton_collapses_confusable_chars()
        => Assert.Equal(CallsignMatcher.Skeleton("N9WAR"), CallsignMatcher.Skeleton("M9WAR"));
}

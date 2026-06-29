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

/// <summary>
/// Guards the error-amplification rule: snapping a decode onto a roster entry
/// must NOT, by itself, confer "confirmed" status. The matcher returns a
/// correction + a sub-certain confidence; only QRZ (downstream) sets the
/// confirmed attribution state, and the live roster is confirmed-only so a
/// tentative call can never be the snap target in production.
/// </summary>
public class CallsignMatcherSnapStateTests
{
    private static readonly CallsignSettings Default = new();

    [Fact]
    public void Snapping_to_a_roster_entry_is_not_full_certainty()
    {
        // A one-char ASR miss against a (would-be tentative) roster call.
        var m = CallsignMatcher.Match("KB2OKA", new[] { "KB2UKA" }, Default);

        Assert.NotNull(m);
        Assert.True(m!.Value.Snapped);                 // corrected onto the roster call
        Assert.Equal("KB2UKA", m.Value.Callsign);
        // A snap is never reported as certain (1.0) — that's reserved for an exact
        // roster hit. So snapping alone cannot launder a decode to "confirmed";
        // the attribution layer still requires QRZ for that state.
        Assert.True(m.Value.Confidence < 1.0);
    }

    [Fact]
    public void Structurally_valid_decode_without_roster_is_only_a_low_confidence_candidate()
    {
        // No roster: a well-formed call is accepted on structure alone, but at a
        // low confidence and NOT snapped — it is not promoted to a confirmed call.
        var m = CallsignMatcher.Match("W1AW", System.Array.Empty<string>(), Default);

        Assert.NotNull(m);
        Assert.False(m!.Value.Snapped);
        Assert.True(m.Value.Validated);
        Assert.True(m.Value.Confidence < 1.0);
    }
}

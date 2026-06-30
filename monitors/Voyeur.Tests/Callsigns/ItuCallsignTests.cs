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

public class ItuCallsignTests
{
    [Theory]
    // Classic letter-prefix calls.
    [InlineData("W1AW")]
    [InlineData("KB2UKA")]
    [InlineData("N9WAR")]
    [InlineData("G4ABC")]
    [InlineData("VE3XYZ")]
    [InlineData("M0XYZ")]
    [InlineData("K1A")]      // 1x1 special-event shape
    [InlineData("AA1A")]     // 2x1
    // letter+digit prefix (A4 Oman etc.)
    [InlineData("A45XYZ")]
    // DIGIT-FIRST prefixes — the class the old anchor regex dropped.
    [InlineData("2E0XYZ")]
    [InlineData("4X1AB")]
    [InlineData("9A1A")]
    [InlineData("3D2AB")]
    [InlineData("7Q7AB")]
    [InlineData("1A0KM")]
    public void IsValid_accepts_well_formed_bare_calls(string call)
        => Assert.True(ItuCallsign.IsValid(call));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("HELLO")]    // no digit
    [InlineData("QSL")]      // Q-code, no digit
    [InlineData("ABCD")]     // letters only
    [InlineData("12345")]    // digits only
    [InlineData("K")]        // prefix only
    [InlineData("AB12")]     // no letter suffix
    [InlineData("KB2UKAXY")] // suffix too long (5)
    [InlineData("/")]
    public void IsValid_rejects_garbage(string? call)
        => Assert.False(ItuCallsign.IsValid(call));

    [Theory]
    [InlineData("KB2UKA/P")]
    [InlineData("W1AW/M")]
    [InlineData("VE3XYZ/MM")]
    [InlineData("KB2UKA/QRP")]
    [InlineData("DL/KB2UKA")]
    [InlineData("9A/OK1XYZ")]
    [InlineData("DL/KB2UKA/P")]
    [InlineData("KB2UKA/9")]   // reciprocal district
    public void IsValid_accepts_portable_and_modifier_forms(string call)
        => Assert.True(ItuCallsign.IsValid(call));

    [Theory]
    [InlineData("KB2UKA/ZZZZZ")] // bogus trailing segment (not modifier/prefix/base)
    [InlineData("HELLO/P")]      // base segment invalid
    public void IsValid_rejects_bad_portable_forms(string call)
        => Assert.False(ItuCallsign.IsValid(call));

    [Theory]
    [InlineData("KB2UKA/P", "KB2UKA")]
    [InlineData("DL/KB2UKA", "KB2UKA")]
    [InlineData("DL/KB2UKA/P", "KB2UKA")]
    [InlineData("9A/OK1XYZ", "OK1XYZ")]
    [InlineData("2E0XYZ", "2E0XYZ")]
    public void BaseCall_resolves_home_call(string input, string expected)
        => Assert.Equal(expected, ItuCallsign.BaseCall(input));

    [Fact]
    public void BaseCall_null_for_garbage()
        => Assert.Null(ItuCallsign.BaseCall("HELLO"));

    [Theory]
    [InlineData("2E0XYZ", true)]
    [InlineData("4X1AB", true)]
    [InlineData("9A1A", true)]
    [InlineData("KB2UKA", false)]
    [InlineData("W1AW", false)]
    public void HasDigitFirstPrefix_detects_digit_leading_prefix(string call, bool expected)
        => Assert.Equal(expected, ItuCallsign.HasDigitFirstPrefix(call));

    [Fact]
    public void IsValid_is_case_insensitive()
        => Assert.True(ItuCallsign.IsValid("kb2uka"));
}

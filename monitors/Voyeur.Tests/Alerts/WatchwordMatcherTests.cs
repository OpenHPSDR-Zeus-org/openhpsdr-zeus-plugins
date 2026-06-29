// SPDX-License-Identifier: GPL-2.0-or-later

using Zeus.Server.Voyeur.Alerts;
using Xunit;

namespace Openhpsdr.Zeus.Plugins.Voyeur.Tests.Alerts;

public class WatchwordMatcherTests
{
    private static Watchword Kw(string t) => new(t, WatchwordType.Keyword);
    private static Watchword Call(string t) => new(t, WatchwordType.Callsign);

    [Fact]
    public void Keyword_matches_on_word_boundary()
    {
        var m = WatchwordMatcher.Match("Thanks Doug for the report", null, new[] { Kw("Doug") });
        Assert.Single(m);
        Assert.Equal("Doug", m[0].MatchedText);
    }

    [Fact]
    public void Keyword_does_not_match_inside_a_larger_word()
    {
        // The canonical "Doug" must not fire on "doughnut".
        var m = WatchwordMatcher.Match("I really love doughnuts", null, new[] { Kw("Doug") });
        Assert.Empty(m);
    }

    [Fact]
    public void Keyword_is_case_insensitive()
    {
        var m = WatchwordMatcher.Match("DOUG here, good morning", null, new[] { Kw("doug") });
        Assert.Single(m);
    }

    [Fact]
    public void Keyword_matches_trailing_punctuation_boundary()
    {
        var m = WatchwordMatcher.Match("over to you, Doug.", null, new[] { Kw("Doug") });
        Assert.Single(m);
    }

    [Fact]
    public void Multi_word_keyword_matches_as_a_unit()
    {
        var m = WatchwordMatcher.Match("back to net control now", null, new[] { Kw("net control") });
        Assert.Single(m);
    }

    [Fact]
    public void Callsign_matches_extracted_set_case_insensitive()
    {
        var m = WatchwordMatcher.Match("worked him earlier", new[] { "KB2UKA" }, new[] { Call("kb2uka") });
        Assert.Single(m);
        Assert.Equal("KB2UKA", m[0].MatchedText);
    }

    [Fact]
    public void Callsign_literal_fallback_hits_transcript_when_not_extracted()
    {
        var m = WatchwordMatcher.Match("this is KB2UKA standing by", null, new[] { Call("KB2UKA") });
        Assert.Single(m);
    }

    [Fact]
    public void Callsign_literal_fallback_tolerates_separators()
    {
        var m = WatchwordMatcher.Match("calling KB2-UKA", null, new[] { Call("KB2UKA") });
        Assert.Single(m);
    }

    [Fact]
    public void Callsign_absent_does_not_match()
    {
        var m = WatchwordMatcher.Match("this is W1AW", new[] { "W1AW" }, new[] { Call("KB2UKA") });
        Assert.Empty(m);
    }

    [Fact]
    public void Disabled_watchword_never_fires()
    {
        var m = WatchwordMatcher.Match("Doug is here", null, new[] { new Watchword("Doug", WatchwordType.Keyword, Enabled: false) });
        Assert.Empty(m);
    }

    [Fact]
    public void Empty_or_null_inputs_are_safe()
    {
        Assert.Empty(WatchwordMatcher.Match(null, null, new[] { Kw("Doug") }));
        Assert.Empty(WatchwordMatcher.Match("anything", null, null));
        Assert.Empty(WatchwordMatcher.Match("anything", null, System.Array.Empty<Watchword>()));
    }

    [Fact]
    public void Duplicate_watchwords_match_once()
    {
        var m = WatchwordMatcher.Match("Doug Doug Doug", null, new[] { Kw("Doug"), Kw("doug") });
        Assert.Single(m);
    }
}

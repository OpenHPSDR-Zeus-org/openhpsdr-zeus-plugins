// SPDX-License-Identifier: GPL-2.0-or-later
//
// Tests for SherpaParakeetTranscriber stdout parsing + arg/hotwords building.
// Pure-logic only — NO process is ever spawned (the fake-runner contract is
// exercised by feeding ParseResult the exact stdout a runner would return).

using Xunit;
using Zeus.Server.Voyeur;

namespace Openhpsdr.Zeus.Plugins.Voyeur.Tests.Stt;

public sealed class SherpaParakeetParseTests
{
    // ---- ParseResult: the core "parse sherpa stdout JSON" path ----

    [Fact]
    public void ParseResult_reads_text_field()
    {
        const string stdout =
            "{\"text\": \"this is whiskey alpha two delta victor\", \"timestamps\": [0.0, 0.4]}";
        var r = SherpaParakeetTranscriber.ParseResult(0, stdout, "", available: true);

        Assert.True(r.EngineAvailable);
        Assert.True(r.HasText);
        Assert.Equal("this is whiskey alpha two delta victor", r.Text);
    }

    [Fact]
    public void ParseResult_tolerates_stderr_noise_around_json_on_stdout()
    {
        // sherpa prints progress lines too; ExtractFirstJsonObject must slice the
        // object out even with leading/trailing junk.
        const string stdout = "Loading model...\n{\"text\": \"hello world\"}\nDone in 0.3s\n";
        var r = SherpaParakeetTranscriber.ParseResult(0, stdout, "diagnostic chatter", available: true);
        Assert.Equal("hello world", r.Text);
    }

    [Fact]
    public void ParseResult_maps_tokens_with_unknown_confidence()
    {
        const string stdout = "{\"text\": \"hi\", \"tokens\": [\"hi\", \" there\"]}";
        var r = SherpaParakeetTranscriber.ParseResult(0, stdout, "", available: true);
        Assert.NotNull(r.Tokens);
        Assert.Equal(2, r.Tokens!.Count);
        Assert.Equal("hi", r.Tokens[0].Text);
        Assert.True(float.IsNaN(r.Tokens[0].Confidence));
    }

    [Fact]
    public void ParseResult_empty_text_is_no_speech_not_error()
    {
        var r = SherpaParakeetTranscriber.ParseResult(0, "{\"text\": \"\"}", "", available: true);
        Assert.True(r.EngineAvailable);
        Assert.False(r.HasText);
        Assert.Null(r.Text);
    }

    [Fact]
    public void ParseResult_strips_bracketed_nonspeech_tags()
    {
        var r = SherpaParakeetTranscriber.ParseResult(0, "{\"text\": \"[noise]\"}", "", available: true);
        Assert.False(r.HasText);
    }

    [Fact]
    public void ParseResult_malformed_json_is_no_speech()
    {
        var r = SherpaParakeetTranscriber.ParseResult(0, "{not valid json", "", available: true);
        Assert.True(r.EngineAvailable);
        Assert.False(r.HasText);
    }

    [Fact]
    public void ParseResult_no_json_at_all_is_no_speech()
    {
        var r = SherpaParakeetTranscriber.ParseResult(1, "segfault\n", "boom", available: true);
        Assert.True(r.EngineAvailable);
        Assert.False(r.HasText);
    }

    [Fact]
    public void ParseResult_unavailable_engine_reports_not_installed()
    {
        var r = SherpaParakeetTranscriber.ParseResult(0, "{\"text\":\"x\"}", "", available: false);
        Assert.False(r.EngineAvailable);
        Assert.Contains("not installed", r.Error);
    }

    // ---- ExtractFirstJsonObject: brace-aware slicing ----

    [Fact]
    public void ExtractFirstJsonObject_handles_braces_inside_strings()
    {
        const string s = "noise {\"text\": \"a } b { c\"} trailing";
        var json = SherpaParakeetTranscriber.ExtractFirstJsonObject(s);
        Assert.Equal("{\"text\": \"a } b { c\"}", json);
    }

    [Fact]
    public void ExtractFirstJsonObject_handles_nested_objects()
    {
        const string s = "{\"a\": {\"b\": 1}, \"text\": \"x\"}";
        var json = SherpaParakeetTranscriber.ExtractFirstJsonObject(s);
        Assert.Equal(s, json);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no braces here")]
    public void ExtractFirstJsonObject_returns_null_when_absent(string s)
    {
        Assert.Null(SherpaParakeetTranscriber.ExtractFirstJsonObject(s));
    }
}

// SPDX-License-Identifier: GPL-2.0-or-later
//
// Tests for the A/B bench harness — CSV escaping, truth matching, and a full
// RunAsync over FAKE engines + dummy wav files (no process spawn).

using Xunit;
using Zeus.Server.Voyeur;

namespace Openhpsdr.Zeus.Plugins.Voyeur.Tests.Stt;

public sealed class SttBenchRunnerTests
{
    // ---- CSV escaping ----

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("has,comma", "\"has,comma\"")]
    [InlineData("has\"quote", "\"has\"\"quote\"")]
    [InlineData("has\nnewline", "\"has\nnewline\"")]
    [InlineData(null, "")]
    public void Esc_quotes_when_needed(string? input, string expected)
    {
        Assert.Equal(expected, SttBenchRunner.Esc(input));
    }

    [Fact]
    public void ToCsv_has_header_and_one_row_per_entry()
    {
        var rows = new[]
        {
            new SttBenchRunner.BenchRow("a.wav", "Whisper", "hello", true, 12, true, true),
            new SttBenchRunner.BenchRow("a.wav", "Parakeet", "he, llo", false, 8, true, false),
            new SttBenchRunner.BenchRow("b.wav", "Whisper", "x", false, 3, false, null),
        };
        var csv = SttBenchRunner.ToCsv(rows);
        var lines = csv.TrimEnd('\n').Split('\n');

        Assert.Equal("file,engine,text,callsign_hit,ms,available,truth_match", lines[0]);
        Assert.Equal(4, lines.Length); // header + 3 rows
        Assert.Equal("a.wav,Whisper,hello,1,12,1,1", lines[1]);
        Assert.Equal("a.wav,Parakeet,\"he, llo\",0,8,1,0", lines[2]); // comma field quoted
        Assert.Equal("b.wav,Whisper,x,0,3,0,", lines[3]); // null truth_match → empty trailing field
    }

    // ---- truth matching ----

    [Fact]
    public void MatchesTruth_matches_extracted_callsign()
    {
        // truth is a clean callsign; transcript spells it phonetically.
        Assert.True(SttBenchRunner.MatchesTruth(
            "this is whiskey alpha two delta victor over", "WA2DV"));
    }

    [Fact]
    public void MatchesTruth_false_when_callsign_absent()
    {
        Assert.False(SttBenchRunner.MatchesTruth("good morning everyone", "WA2DV"));
    }

    [Fact]
    public void MatchesTruth_phrase_truth_falls_back_to_literal_contains()
    {
        Assert.True(SttBenchRunner.MatchesTruth("net control standing by", "standing by"));
    }

    [Fact]
    public void ExpectedCallsigns_prefers_callsign_tokens()
    {
        var got = SttBenchRunner.ExpectedCallsigns("WA2DV");
        Assert.Contains("WA2DV", got);
    }

    // ---- RunAsync over fakes ----

    [Fact]
    public async Task RunAsync_runs_every_engine_over_every_wav_and_reads_truth()
    {
        var dir = Path.Combine(Path.GetTempPath(), "zeus-bench-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Two dummy overs; the fake engines never read the bytes.
            File.WriteAllText(Path.Combine(dir, "over1.wav"), "dummy");
            File.WriteAllText(Path.Combine(dir, "over2.wav"), "dummy");
            // Ground truth for over1 only.
            File.WriteAllText(Path.Combine(dir, "over1.txt"), "WA2DV");

            var whisper = new FakeSttEngine(SttEngineKind.Whisper, available: true,
                respond: (_, _) => new SttResult("whiskey alpha two delta victor", true));
            var parakeet = new FakeSttEngine(SttEngineKind.Parakeet, available: false);

            var bench = new SttBenchRunner(whisper, parakeet);
            var rows = await bench.RunAsync(dir, SttOptions.Default);

            // 2 wavs x 2 engines = 4 rows.
            Assert.Equal(4, rows.Count);
            Assert.Equal(2, whisper.Calls);
            Assert.Equal(2, parakeet.Calls);

            var w1 = rows.Single(r => r.File == "over1.wav" && r.Engine == "Whisper");
            Assert.True(w1.CallsignHit);
            Assert.True(w1.Available);
            Assert.True(w1.TruthMatch!.Value); // truth recovered

            var w2 = rows.Single(r => r.File == "over2.wav" && r.Engine == "Whisper");
            Assert.Null(w2.TruthMatch); // no truth file for over2

            var p1 = rows.Single(r => r.File == "over1.wav" && r.Engine == "Parakeet");
            Assert.False(p1.Available); // not installed
            Assert.False(p1.CallsignHit);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task RunAsync_missing_folder_yields_no_rows()
    {
        var bench = new SttBenchRunner(new FakeSttEngine(SttEngineKind.Whisper, true));
        var rows = await bench.RunAsync("/no/such/folder/zeus-bench", SttOptions.Default);
        Assert.Empty(rows);
    }
}

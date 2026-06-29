// SPDX-License-Identifier: GPL-2.0-or-later
//
// Tests for SherpaParakeetTranscriber argument + hotwords-file building and
// model-dir completeness — all pure, no process spawn, no filesystem for args.

using Xunit;
using Zeus.Server.Voyeur;

namespace Openhpsdr.Zeus.Plugins.Voyeur.Tests.Stt;

public sealed class SherpaArgsHotwordsTests
{
    // ---- BuildArgs ----

    [Fact]
    public void BuildArgs_emits_nemo_transducer_greedy_with_cpu_floor()
    {
        var opt = new SttOptions(Array.Empty<string>(), Provider: "cpu", Threads: 4);
        var args = SherpaParakeetTranscriber.BuildArgs("/m", "/tmp/feed.wav", opt, hotwordsFile: null);

        Assert.Contains("--model-type=nemo_transducer", args);
        Assert.Contains("--decoding-method=greedy_search", args);
        Assert.Contains("--provider=cpu", args);
        Assert.Contains("--num-threads=4", args);
        // model files wired off the model dir
        Assert.Contains(args, a => a.StartsWith("--encoder=") && a.Contains("encoder.int8.onnx"));
        Assert.Contains(args, a => a.StartsWith("--decoder=") && a.Contains("decoder.int8.onnx"));
        Assert.Contains(args, a => a.StartsWith("--joiner=") && a.Contains("joiner.int8.onnx"));
        Assert.Contains(args, a => a.StartsWith("--tokens=") && a.Contains("tokens.txt"));
    }

    [Fact]
    public void BuildArgs_input_wav_is_last_positional()
    {
        var args = SherpaParakeetTranscriber.BuildArgs("/m", "/tmp/feed.wav", SttOptions.Default, null);
        Assert.Equal("/tmp/feed.wav", args[^1]);
        Assert.DoesNotContain("/tmp/feed.wav", args.GetRange(0, args.Count - 1));
    }

    [Fact]
    public void BuildArgs_passes_provider_through()
    {
        var opt = new SttOptions(Array.Empty<string>(), Provider: "cuda");
        var args = SherpaParakeetTranscriber.BuildArgs("/m", "f.wav", opt, null);
        Assert.Contains("--provider=cuda", args);
    }

    [Fact]
    public void BuildArgs_threads_clamped_to_at_least_one()
    {
        var opt = new SttOptions(Array.Empty<string>(), Threads: 0);
        var args = SherpaParakeetTranscriber.BuildArgs("/m", "f.wav", opt, null);
        Assert.Contains("--num-threads=1", args);
    }

    [Fact]
    public void BuildArgs_omits_hotwords_when_no_file()
    {
        var args = SherpaParakeetTranscriber.BuildArgs("/m", "f.wav", SttOptions.Default, hotwordsFile: null);
        Assert.DoesNotContain(args, a => a.StartsWith("--hotwords-file="));
        Assert.DoesNotContain(args, a => a.StartsWith("--hotwords-score="));
    }

    [Fact]
    public void BuildArgs_includes_hotwords_when_file_supplied()
    {
        var opt = new SttOptions(new[] { "KB2UKA" }, HotwordsScore: 3.5f);
        var args = SherpaParakeetTranscriber.BuildArgs("/m", "f.wav", opt, "/tmp/hw.txt");
        Assert.Contains("--hotwords-file=/tmp/hw.txt", args);
        Assert.Contains("--hotwords-score=3.5", args);
    }

    // ---- BuildHotwordsContent ----

    [Fact]
    public void BuildHotwordsContent_one_phrase_per_line_with_score()
    {
        var content = SherpaParakeetTranscriber.BuildHotwordsContent(new[] { "KB2UKA", "W1ABC" }, 3.5f);
        Assert.Equal("KB2UKA :3.5\nW1ABC :3.5", content);
    }

    [Fact]
    public void BuildHotwordsContent_without_score()
    {
        var content = SherpaParakeetTranscriber.BuildHotwordsContent(new[] { "KB2UKA" }, null);
        Assert.Equal("KB2UKA", content);
    }

    [Fact]
    public void BuildHotwordsContent_collapses_internal_whitespace_and_drops_blanks()
    {
        var content = SherpaParakeetTranscriber.BuildHotwordsContent(
            new[] { "  KILO   BRAVO  ", "", "   ", "WHISKEY ALPHA" }, null);
        Assert.Equal("KILO BRAVO\nWHISKEY ALPHA", content);
    }

    [Fact]
    public void BuildHotwordsContent_empty_list_is_empty_string()
    {
        Assert.Equal("", SherpaParakeetTranscriber.BuildHotwordsContent(Array.Empty<string>(), 2f));
    }

    // ---- IsCompleteModelDir ----

    [Fact]
    public void IsCompleteModelDir_true_only_when_all_four_files_present()
    {
        var dir = Path.Combine(Path.GetTempPath(), "zeus-parakeet-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.False(SherpaParakeetTranscriber.IsCompleteModelDir(dir));
            File.WriteAllText(Path.Combine(dir, "encoder.int8.onnx"), "x");
            File.WriteAllText(Path.Combine(dir, "decoder.int8.onnx"), "x");
            File.WriteAllText(Path.Combine(dir, "joiner.int8.onnx"), "x");
            Assert.False(SherpaParakeetTranscriber.IsCompleteModelDir(dir)); // tokens missing
            File.WriteAllText(Path.Combine(dir, "tokens.txt"), "x");
            Assert.True(SherpaParakeetTranscriber.IsCompleteModelDir(dir));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/nonexistent/path/zeus-parakeet")]
    public void IsCompleteModelDir_false_for_missing(string? dir)
    {
        Assert.False(SherpaParakeetTranscriber.IsCompleteModelDir(dir));
    }
}

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

using System.Buffers.Binary;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Zeus.Server.Voyeur;

namespace Openhpsdr.Zeus.Plugins.Voyeur.Tests.Corpus;

public sealed class VoyeurCorpusStoreTests : IDisposable
{
    private readonly string _tmp;
    private readonly string _corpusRoot;

    public VoyeurCorpusStoreTests()
    {
        _tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "voyeur-corpus-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
        _corpusRoot = System.IO.Path.Combine(_tmp, "corpus");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tmp)) Directory.Delete(_tmp, recursive: true); } catch { }
    }

    private VoyeurCorpusStore NewStore(bool retain = true, int maxClips = 5000) =>
        new(_corpusRoot, NullLogger.Instance,
            new CorpusSettings(RetainCorpus: retain, MaxClips: maxClips));

    // Write a minimal valid 16 kHz mono 16-bit PCM WAV so WhisperWav.Prepare
    // accepts it unchanged (no resample temp file needed).
    private string WriteWav(int samples = 1600)
    {
        var path = System.IO.Path.Combine(_tmp, "over-" + Guid.NewGuid().ToString("N") + ".wav");
        const int rate = 16000, channels = 1, bits = 16;
        int dataBytes = samples * 2;
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        bw.Write("RIFF"u8);
        bw.Write((uint)(36 + dataBytes));
        bw.Write("WAVE"u8);
        bw.Write("fmt "u8);
        bw.Write(16u);
        bw.Write((ushort)1);                       // PCM
        bw.Write((ushort)channels);
        bw.Write((uint)rate);
        bw.Write((uint)(rate * channels * (bits / 8)));
        bw.Write((ushort)(channels * (bits / 8)));
        bw.Write((ushort)bits);
        bw.Write("data"u8);
        bw.Write((uint)dataBytes);
        Span<byte> b = stackalloc byte[2];
        for (int i = 0; i < samples; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(b, (short)(i % 100));
            bw.Write(b);
        }
        return path;
    }

    private static List<JsonElement> ReadIndex(string indexPath)
    {
        Assert.True(File.Exists(indexPath));
        return File.ReadAllLines(indexPath)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonSerializer.Deserialize<JsonElement>(l))
            .ToList();
    }

    // ---- JSONL append shape -------------------------------------------------

    [Fact]
    public void Bank_writes_one_jsonl_line_with_expected_fields()
    {
        var store = NewStore();
        var wav = WriteWav();
        var utc = new DateTime(2026, 6, 29, 12, 34, 56, DateTimeKind.Utc);

        var rel = store.Bank(wav, "KB2UKA", "this is KB2UKA over", 14_250_000, "20m", "whisper", utc);

        Assert.NotNull(rel);
        var rows = ReadIndex(store.IndexPath);
        Assert.Single(rows);
        var e = rows[0];
        // camelCase keys exactly as the manifest contract specifies.
        Assert.Equal(rel, e.GetProperty("wavRelPath").GetString());
        Assert.Equal("KB2UKA", e.GetProperty("callsign").GetString());
        Assert.Equal("this is KB2UKA over", e.GetProperty("transcript").GetString());
        Assert.Equal(14_250_000, e.GetProperty("freqHz").GetInt64());
        Assert.Equal("20m", e.GetProperty("band").GetString());
        Assert.Equal("whisper", e.GetProperty("engine").GetString());
        Assert.True(e.TryGetProperty("utc", out _));
        // The clip really landed on disk inside the corpus root.
        Assert.True(File.Exists(System.IO.Path.Combine(store.CorpusRoot, rel!)));
    }

    [Fact]
    public void Bank_appends_each_clip_as_its_own_line()
    {
        var store = NewStore();
        for (int i = 0; i < 4; i++)
            store.Bank(WriteWav(), "W1ABC", "over " + i, 7_200_000, "40m", "parakeet", DateTime.UtcNow.AddSeconds(i));

        var rows = ReadIndex(store.IndexPath);
        Assert.Equal(4, rows.Count);
        Assert.Equal(4, Directory.GetFiles(store.CorpusRoot, "*.wav").Length);
    }

    [Fact]
    public void Bank_is_noop_when_retention_off()
    {
        var store = NewStore(retain: false);
        var rel = store.Bank(WriteWav(), "KB2UKA", "hi", 14_000_000, "20m", "whisper", DateTime.UtcNow);

        Assert.Null(rel);
        // Default-OFF must not create a single file or directory.
        Assert.False(Directory.Exists(store.CorpusRoot));
        Assert.False(File.Exists(store.IndexPath));
    }

    [Fact]
    public void Bank_is_noop_for_blank_callsign_or_missing_wav()
    {
        var store = NewStore();
        Assert.Null(store.Bank(WriteWav(), "   ", "x", 1, "20m", "whisper", DateTime.UtcNow));
        Assert.Null(store.Bank(System.IO.Path.Combine(_tmp, "does-not-exist.wav"),
            "KB2UKA", "x", 1, "20m", "whisper", DateTime.UtcNow));
        Assert.False(File.Exists(store.IndexPath));
    }

    // ---- prune at cap -------------------------------------------------------

    [Fact]
    public void Bank_prunes_oldest_when_over_cap()
    {
        var store = NewStore(maxClips: 3);
        var rels = new List<string>();
        for (int i = 0; i < 5; i++)
        {
            var rel = store.Bank(WriteWav(), "N0CALL", "over " + i, 14_000_000, "20m", "whisper",
                DateTime.UtcNow.AddSeconds(i));
            Assert.NotNull(rel);
            rels.Add(rel!);
        }

        // Only the cap's worth survive — index + on-disk WAVs both bounded.
        var rows = ReadIndex(store.IndexPath);
        Assert.Equal(3, rows.Count);
        Assert.Equal(3, Directory.GetFiles(store.CorpusRoot, "*.wav").Length);

        // The 3 NEWEST are kept; the 2 oldest clips are gone from disk.
        var survivors = rows.Select(r => r.GetProperty("wavRelPath").GetString()).ToList();
        Assert.Equal(rels.Skip(2), survivors);
        Assert.False(File.Exists(System.IO.Path.Combine(store.CorpusRoot, rels[0])));
        Assert.False(File.Exists(System.IO.Path.Combine(store.CorpusRoot, rels[1])));
        Assert.True(File.Exists(System.IO.Path.Combine(store.CorpusRoot, rels[4])));
    }

    [Fact]
    public void GetStats_reports_count_cap_and_path()
    {
        var store = NewStore(maxClips: 10);
        store.Bank(WriteWav(), "KB2UKA", "a", 14_000_000, "20m", "whisper", DateTime.UtcNow);
        store.Bank(WriteWav(), "W1ABC", "b", 14_000_000, "20m", "whisper", DateTime.UtcNow);

        var stats = store.GetStats();
        Assert.True(stats.RetainCorpus);
        Assert.Equal(2, stats.ClipCount);
        Assert.Equal(10, stats.MaxClips);
        Assert.True(stats.Bytes > 0);
        Assert.Equal(store.CorpusRoot, stats.Path);
    }

    [Fact]
    public void GetStats_is_empty_before_any_bank()
    {
        var store = NewStore();
        var stats = store.GetStats();
        Assert.Equal(0, stats.ClipCount);
        Assert.Equal(0, stats.Bytes);
    }

    // ---- path safety (no traversal) ----------------------------------------

    [Fact]
    public void Bank_callsign_with_traversal_stays_inside_corpus_root()
    {
        var store = NewStore();
        var sentinel = System.IO.Path.Combine(_tmp, "passwd"); // sibling of corpus root

        var rel = store.Bank(WriteWav(), "../../passwd", "x", 1, "20m", "whisper", DateTime.UtcNow);

        Assert.NotNull(rel);
        // No separators leaked into the relative path, and nothing escaped.
        Assert.DoesNotContain("..", rel!);
        Assert.DoesNotContain('/', rel!);
        Assert.DoesNotContain('\\', rel!);
        Assert.False(File.Exists(sentinel));
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(store.CorpusRoot, rel!));
        Assert.StartsWith(System.IO.Path.GetFullPath(store.CorpusRoot), full, StringComparison.Ordinal);
        Assert.True(File.Exists(full));
    }

    [Fact]
    public void Bank_callsign_of_only_punctuation_falls_back_safely()
    {
        var store = NewStore();
        var rel = store.Bank(WriteWav(), "////", "x", 1, "20m", "whisper", DateTime.UtcNow);
        // Blank-after-sanitize is allowed (callsign itself is non-whitespace);
        // the file name falls back to a safe token, still inside the root.
        Assert.NotNull(rel);
        Assert.DoesNotContain('/', rel!);
        Assert.True(File.Exists(System.IO.Path.Combine(store.CorpusRoot, rel!)));
    }
}

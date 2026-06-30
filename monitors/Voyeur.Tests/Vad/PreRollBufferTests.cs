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

using Zeus.Server.Voyeur;

namespace Openhpsdr.Zeus.Plugins.Voyeur.Tests.Vad;

public sealed class PreRollBufferTests
{
    private static float[] Ramp(int n, int from = 1)
    {
        var a = new float[n];
        for (int i = 0; i < n; i++) a[i] = from + i;
        return a;
    }

    [Fact]
    public void EmptyBuffer_SnapshotIsEmpty()
    {
        var b = new PreRollBuffer(8);
        Assert.Equal(0, b.Count);
        Assert.Equal(8, b.Capacity);
        Assert.Empty(b.Snapshot());
    }

    [Fact]
    public void CapacityFloor_IsAtLeastOne()
    {
        Assert.Equal(1, new PreRollBuffer(0).Capacity);
        Assert.Equal(1, new PreRollBuffer(-5).Capacity);
    }

    [Fact]
    public void WriteFewerThanCapacity_KeepsAllInOrder()
    {
        var b = new PreRollBuffer(8);
        b.Write(Ramp(3)); // 1,2,3
        Assert.Equal(3, b.Count);
        Assert.Equal(new float[] { 1, 2, 3 }, b.Snapshot());
    }

    [Fact]
    public void WriteExactlyCapacity_KeepsAll()
    {
        var b = new PreRollBuffer(4);
        b.Write(Ramp(4)); // 1,2,3,4
        Assert.Equal(4, b.Count);
        Assert.Equal(new float[] { 1, 2, 3, 4 }, b.Snapshot());
    }

    [Fact]
    public void SingleBlockLargerThanCapacity_KeepsTrailingTail()
    {
        var b = new PreRollBuffer(4);
        b.Write(Ramp(10)); // 1..10 → keep last 4: 7,8,9,10
        Assert.Equal(4, b.Count);
        Assert.Equal(new float[] { 7, 8, 9, 10 }, b.Snapshot());
    }

    [Fact]
    public void AccumulatingWrites_OverflowKeepsMostRecentChronological()
    {
        var b = new PreRollBuffer(5);
        b.Write(Ramp(3));            // 1,2,3
        b.Write(new float[] { 4, 5, 6, 7 }); // total 1..7 → keep last 5: 3,4,5,6,7
        Assert.Equal(5, b.Count);
        Assert.Equal(new float[] { 3, 4, 5, 6, 7 }, b.Snapshot());
    }

    [Fact]
    public void ManySmallWrites_WrapAroundStaysChronological()
    {
        var b = new PreRollBuffer(4);
        for (int i = 1; i <= 10; i++) b.Write(new float[] { i }); // 1..10
        // last 4 in order
        Assert.Equal(new float[] { 7, 8, 9, 10 }, b.Snapshot());
    }

    [Fact]
    public void WriteThatLandsExactlyOnWrapBoundary_IsCorrect()
    {
        var b = new PreRollBuffer(4);
        b.Write(Ramp(4));                       // fills, head wraps to 0
        b.Write(new float[] { 5, 6 });          // overwrites 1,2 → 3,4,5,6
        Assert.Equal(new float[] { 3, 4, 5, 6 }, b.Snapshot());
    }

    [Fact]
    public void CopyTo_RespectsSmallerDestinationAndReturnsCount()
    {
        var b = new PreRollBuffer(8);
        b.Write(Ramp(5)); // 1..5
        var dst = new float[3];
        int n = b.CopyTo(dst);
        Assert.Equal(3, n);
        Assert.Equal(new float[] { 1, 2, 3 }, dst);
    }

    [Fact]
    public void Snapshot_DoesNotMutate_SoSecondCallMatches()
    {
        var b = new PreRollBuffer(8);
        b.Write(Ramp(5));
        var first = b.Snapshot();
        var second = b.Snapshot();
        Assert.Equal(first, second);
        Assert.Equal(5, b.Count);
    }

    [Fact]
    public void Clear_DropsEverything()
    {
        var b = new PreRollBuffer(8);
        b.Write(Ramp(5));
        b.Clear();
        Assert.Equal(0, b.Count);
        Assert.Empty(b.Snapshot());
        // Reusable after clear.
        b.Write(new float[] { 9, 9 });
        Assert.Equal(new float[] { 9, 9 }, b.Snapshot());
    }

    [Fact]
    public void EmptyWrite_IsNoOp()
    {
        var b = new PreRollBuffer(4);
        b.Write(Ramp(2));
        b.Write(ReadOnlySpan<float>.Empty);
        Assert.Equal(new float[] { 1, 2 }, b.Snapshot());
    }

    [Fact]
    public void ForMs_SizesToSampleRate()
    {
        // 500 ms @ 48 kHz = 24000 samples.
        Assert.Equal(24000, PreRollBuffer.ForMs(500, 48000).Capacity);
        // Degenerate inputs never produce a zero-size buffer.
        Assert.Equal(1, PreRollBuffer.ForMs(0, 48000).Capacity);
    }

    [Fact]
    public void PrependScenario_PreRollThenSegmentAudio_IsContiguous()
    {
        // Simulates the drain loop: feed pre-speech audio while idle, then on
        // "Started" prepend the snapshot ahead of the opening block.
        var pre = new PreRollBuffer(4);
        pre.Write(new float[] { 10, 11 }); // pre-speech tail
        pre.Write(new float[] { 12, 13 }); // 10,11,12,13

        var snapshot = pre.Snapshot();
        var openingBlock = new float[] { 14, 15 };

        var saved = new List<float>();
        saved.AddRange(snapshot);
        saved.AddRange(openingBlock);
        pre.Clear();

        Assert.Equal(new float[] { 10, 11, 12, 13, 14, 15 }, saved.ToArray());
        Assert.Equal(0, pre.Count); // cleared so it isn't prepended twice
    }
}

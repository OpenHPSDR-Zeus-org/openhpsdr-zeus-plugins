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

namespace Openhpsdr.Zeus.Plugins.Voyeur.Tests;

/// <summary>
/// Behavioural tests for the energy segmenter's Phase-2 fixes: debounced open,
/// trailing-hang exclusion from the reported span, the MaxOverSeconds force-close,
/// and the sub-minimum blip returning Idle (so the drain loop drops the partial).
/// Pure DSP-shaped logic — no audio path, no process, no sockets.
/// </summary>
public class VoyeurSegmenterTests
{
    private const int Rate = 16_000;

    // A constant-amplitude mono block: RMS == amp, peak == amp.
    private static float[] Block(int samples, float amp)
    {
        var b = new float[samples];
        for (int i = 0; i < samples; i++) b[i] = amp;
        return b;
    }

    private const float Floor = 0.001f; // ambient
    private const float Loud = 0.1f;     // ~40 dB over the seeded floor

    [Fact]
    public void Debounce_open_requires_sustained_energy()
    {
        var seg = new VoyeurSegmenter(Rate); // defaults: 6 dB open, 60 ms debounce
        // 30 ms blocks (480 samples) — below the 60 ms (960-sample) debounce, so a
        // single loud block can never open an over.
        const int N = 480;

        // First block seeds the floor and never opens.
        Assert.Equal(VoyeurSegmenter.Transition.Idle, seg.Process(Block(N, Floor)).Transition);

        // One loud block: above-margin but debounce not yet satisfied.
        Assert.Equal(VoyeurSegmenter.Transition.Idle, seg.Process(Block(N, Loud)).Transition);
        // A quiet block resets the debounce run (energy was not sustained).
        Assert.Equal(VoyeurSegmenter.Transition.Idle, seg.Process(Block(N, Floor)).Transition);
        // A lone loud block again — still not enough on its own.
        Assert.Equal(VoyeurSegmenter.Transition.Idle, seg.Process(Block(N, Loud)).Transition);
        // Sustained: the second consecutive loud block crosses the debounce → open.
        Assert.Equal(VoyeurSegmenter.Transition.Started, seg.Process(Block(N, Loud)).Transition);
        Assert.True(seg.IsActive);
    }

    [Fact]
    public void Trailing_hang_is_excluded_from_reported_duration()
    {
        var seg = new VoyeurSegmenter(Rate); // hang 1.0 s
        const int N = 1600;                  // 0.1 s blocks; one loud block opens

        seg.Process(Block(N, Floor)); // seed
        Assert.Equal(VoyeurSegmenter.Transition.Started, seg.Process(Block(N, Loud)).Transition);
        // 8 more loud blocks → 9 * 1600 = 14_400 samples of speech (0.9 s).
        for (int i = 0; i < 8; i++)
        {
            var r = seg.Process(Block(N, Loud));
            Assert.Equal(VoyeurSegmenter.Transition.Continuing, r.Transition);
            Assert.False(seg.InHang);
        }

        // First quiet block enters the hang window but does not close yet.
        var hangBlock = seg.Process(Block(N, Floor));
        Assert.Equal(VoyeurSegmenter.Transition.Continuing, hangBlock.Transition);
        Assert.True(seg.InHang);

        // Remaining quiet blocks complete the 1.0 s hang (10 * 1600 = 16_000).
        VoyeurSegmenter.Result closed = default;
        for (int i = 0; i < 9; i++) closed = seg.Process(Block(N, Floor));

        Assert.Equal(VoyeurSegmenter.Transition.Ended, closed.Transition);
        // Reported duration is the SPEECH only (0.9 s) — the 1.0 s hang trimmed.
        Assert.Equal(900, closed.DurationMs);
        Assert.False(seg.IsActive);
    }

    [Fact]
    public void MaxOverSeconds_force_closes_a_stuck_carrier()
    {
        var seg = new VoyeurSegmenter(Rate, new SegSettings { MaxOverSeconds = 0.5 }); // 8000 samples
        const int N = 1600;

        seg.Process(Block(N, Floor)); // seed
        Assert.Equal(VoyeurSegmenter.Transition.Started, seg.Process(Block(N, Loud)).Transition);

        // Keep keying: activeSamples grows 1600/block; at 8000 it force-closes even
        // though the carrier never went quiet.
        VoyeurSegmenter.Result r = default;
        for (int i = 0; i < 10 && r.Transition != VoyeurSegmenter.Transition.Ended; i++)
            r = seg.Process(Block(N, Loud));

        Assert.Equal(VoyeurSegmenter.Transition.Ended, r.Transition);
        Assert.Equal(500, r.DurationMs); // 8000 samples / 16 kHz
        Assert.False(seg.IsActive);
    }

    [Fact]
    public void Sub_minimum_over_returns_Idle_not_Ended()
    {
        var seg = new VoyeurSegmenter(Rate); // minSegment 0.4 s = 6400 samples
        const int N = 480;                   // small blocks

        seg.Process(Block(N, Floor)); // seed
        // Open with the minimum sustained energy: two loud blocks (debounce), so
        // only ~480 samples count as speech once active.
        seg.Process(Block(N, Loud));  // debounce build
        Assert.Equal(VoyeurSegmenter.Transition.Started, seg.Process(Block(N, Loud)).Transition);

        // Now go quiet for the full hang. Speech (~480 samples) is far below the
        // 0.4 s minimum, so the close is a dropped blip → Idle, never Ended.
        var r = seg.Process(Block(N, Floor)); // first hang block → Continuing
        int guard = 0;
        while (r.Transition == VoyeurSegmenter.Transition.Continuing && guard++ < 200)
            r = seg.Process(Block(N, Floor));

        Assert.Equal(VoyeurSegmenter.Transition.Idle, r.Transition);
        Assert.False(seg.IsActive);
    }
}

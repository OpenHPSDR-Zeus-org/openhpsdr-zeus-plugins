// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus Digital plugin — extracted from Zeus core
// (Zeus.Server.Hosting/DigitalTxStreamer.cs). GPL-2.0-or-later.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// DigitalTxStreamer — shared block-pacing helper for the FT8/FT4 and WSPR TX
// keyers. Both render a 48 kHz mono float waveform, then must feed it to the
// host playback sink in exactly 960-sample blocks at the 20 ms mic cadence.
// Real-time pacing is Stopwatch-corrected, not a bare PeriodicTimer, so block
// delivery tracks elapsed wall time and the TxIqRing neither underflows nor
// overflows — identical parity with the live mic path on every platform.
//
// Extraction delta: the in-core version emitted f32le BYTE blocks because
// TxAudioIngest consumed raw bytes; IAudioPlaybackSink.PlayOnAir consumes
// float samples directly, so the sink is float-typed here. Block size, block
// cadence, lead-silence handling, and the stop conditions are unchanged.

using System.Diagnostics;

namespace Openhpsdr.Zeus.Plugins.Digital;

internal static class DigitalTxStreamer
{
    /// <summary>TX waveform sample rate (the host 48 kHz mic-block contract).</summary>
    public const int SampleRate = 48_000;
    /// <summary>Samples per block (20 ms @ 48 kHz mono).</summary>
    public const int BlockSamples = 960;
    /// <summary>Block cadence in milliseconds.</summary>
    public const int BlockMs = 20;

    /// <summary>
    /// Stream <paramref name="leadBlocks"/> silence blocks followed by the
    /// <paramref name="audio"/> waveform as 960-sample float blocks into
    /// <paramref name="sink"/>, paced one block every 20 ms of elapsed wall time.
    /// The lead silence lets the T/R relay settle before real audio appears
    /// (a digital MOX source has no UI pre-key mute window). Stops early — leaving
    /// a clean block boundary — if <paramref name="ct"/> cancels or
    /// <paramref name="stillArmed"/> returns false (operator Halt / disarm
    /// mid-slot). <paramref name="delay"/> is injectable so tests run instantly.
    /// </summary>
    public static async Task StreamAsync(
        float[] audio,
        int leadBlocks,
        Action<ReadOnlyMemory<float>> sink,
        Func<int, CancellationToken, Task> delay,
        Func<bool> stillArmed,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(sink);

        var block = new float[BlockSamples];
        var sw = Stopwatch.StartNew();
        int blocksSent = 0;

        // Lead-in silence (T/R settle). The block buffer starts all-zero.
        for (int i = 0; i < leadBlocks; i++)
        {
            if (ct.IsCancellationRequested || !stillArmed()) return;
            sink(block);
            blocksSent++;
            await PaceAsync(sw, blocksSent, delay, ct).ConfigureAwait(false);
        }

        // Real audio, padded to a whole final block with trailing zeros.
        for (int offset = 0; offset < audio.Length; offset += BlockSamples)
        {
            if (ct.IsCancellationRequested || !stillArmed()) return;
            int count = Math.Min(BlockSamples, audio.Length - offset);
            for (int s = 0; s < BlockSamples; s++)
                block[s] = s < count ? audio[offset + s] : 0f;
            sink(block);
            blocksSent++;
            await PaceAsync(sw, blocksSent, delay, ct).ConfigureAwait(false);
        }
    }

    // Wait until the wall clock reaches the deadline for the next block, so the
    // average rate is exactly one block per 20 ms regardless of per-iteration
    // jitter. Never waits negative time (a late block fires immediately).
    private static Task PaceAsync(
        Stopwatch sw, int blocksSent, Func<int, CancellationToken, Task> delay, CancellationToken ct)
    {
        long deadlineMs = (long)blocksSent * BlockMs;
        long remaining = deadlineMs - sw.ElapsedMilliseconds;
        return remaining > 0 ? delay((int)remaining, ct) : Task.CompletedTask;
    }
}

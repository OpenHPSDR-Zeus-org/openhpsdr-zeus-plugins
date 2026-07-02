// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus Digital plugin — ported from the in-core tests/Zeus.Server.Tests/
// Ft8TxServiceTests.cs, adapted to the plugin seams (float audio sink, DTO
// broadcast, injected moxAlreadyOn). GPL-2.0-or-later.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Ft8TxServiceTests — drive the keyer directly through its internal test
// constructor (no native, no real-time waits, an injected clock + no-op delay).
// The safety invariants are the point: never key disarmed, never key without a
// fresh matching-parity stage, watchdog auto-disarm, halt drops MOX + clears
// stage, every audio block is exactly 960 samples (20 ms @ 48 kHz), and the
// full keying discipline: the pre-key gate SKIPS the slot when another source
// already holds MOX, and MOX is released ONLY when we keyed it.

using Microsoft.Extensions.Logging.Abstractions;
using Openhpsdr.Zeus.Plugins.Digital;
using Openhpsdr.Zeus.Plugins.Digital.Dsp;

namespace Openhpsdr.Zeus.Plugins.Digital.Tests;

public class Ft8TxServiceTests
{
    // Records keying + audio so the tests can assert against the wire effect.
    private sealed class Recorder
    {
        public int KeyUps;
        public int KeyDowns;
        public bool Keyed;
        public int Blocks;
        public int LastBlockSamples = -1;
        public int StatusBroadcasts;

        public bool Key(bool on, out string? error)
        {
            error = null;
            if (on) { KeyUps++; Keyed = true; } else { KeyDowns++; Keyed = false; }
            return true;
        }

        public void Audio(ReadOnlyMemory<float> block)
        {
            Blocks++;
            LastBlockSamples = block.Length;
        }

        public void Broadcast(Ft8TxStatusDto _) => StatusBroadcasts++;
    }

    // 3 960-sample blocks of synthetic "audio" so the streamer has something to
    // pace without needing the native encoder.
    private static float[] FakeAudio() => new float[960 * 3];

    private static Ft8TxService NewService(Recorder rec, DateTime now,
        Func<string, Ft8Protocol, int, float[]?>? renderer = null,
        Func<bool>? moxAlreadyOn = null)
    {
        return new Ft8TxService(
            rec.Key,
            rec.Audio,
            rec.Broadcast,
            renderer ?? ((_, _, _) => FakeAudio()),
            () => now,
            static (_, _) => Task.CompletedTask,   // no-op delay
            moxAlreadyOn ?? (static () => false),
            NullLogger.Instance);
    }

    // SlotIndexOf — the same epoch math the service uses, for picking even/odd.
    private static long SlotIndexOf(DateTime utc, double slotSeconds)
        => (long)Math.Floor((utc - DateTime.UnixEpoch).TotalSeconds / slotSeconds);

    private static DateTime EvenSlotInstant()
    {
        // Pick a UTC instant whose 15 s slot index is even.
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        if (SlotIndexOf(t, 15.0) % 2 != 0) t = t.AddSeconds(15);
        return t;
    }

    [Fact]
    public void Disarmed_NeverKeys()
    {
        var rec = new Recorder();
        var now = EvenSlotInstant();
        var svc = NewService(rec, now);
        svc.Stage("CQ KB2UKA FN12", 1500, "even", "FT8");   // staged but NOT armed
        Assert.False(svc.ShouldKeyForSlot(SlotIndexOf(now, 15.0), now));
    }

    [Fact]
    public void Armed_NoStage_NeverKeys()
    {
        var rec = new Recorder();
        var now = EvenSlotInstant();
        var svc = NewService(rec, now);
        svc.SetArmed(true);
        Assert.False(svc.ShouldKeyForSlot(SlotIndexOf(now, 15.0), now));
    }

    [Fact]
    public void Armed_FreshStage_MatchingParity_Keys()
    {
        var rec = new Recorder();
        var now = EvenSlotInstant();
        var svc = NewService(rec, now);
        svc.SetArmed(true);
        svc.Stage("CQ KB2UKA FN12", 1500, "even", "FT8");
        long evenSlot = SlotIndexOf(now, 15.0);                  // even index
        Assert.True(svc.ShouldKeyForSlot(evenSlot, now));
        Assert.False(svc.ShouldKeyForSlot(evenSlot + 1, now));   // odd slot — no key
    }

    [Fact]
    public void StagedButStale_NeverKeys()
    {
        var rec = new Recorder();
        var staged = EvenSlotInstant();
        var clock = staged;
        var svc = new Ft8TxService(
            rec.Key, rec.Audio, rec.Broadcast, (_, _, _) => FakeAudio(),
            () => clock, static (_, _) => Task.CompletedTask, static () => false, NullLogger.Instance);
        svc.SetArmed(true);
        svc.Stage("CQ KB2UKA FN12", 1500, "even", "FT8");
        // Advance two full cycles to the SAME-parity slot → age >= 2*slot → stale.
        // (The freshness window is one full TX cycle, so this exercises staleness,
        // not parity: the slot below is even, matching the staged even parity.)
        clock = staged.AddSeconds(2 * 15.0);
        long sameParity = SlotIndexOf(clock, 15.0);
        Assert.Equal(0, ((sameParity % 2) + 2) % 2);            // confirm same (even) parity
        Assert.False(svc.ShouldKeyForSlot(sameParity, clock));  // age == 2*slot → rejected
    }

    [Fact]
    public void FreshStage_SurvivesToNextMatchingBoundary()
    {
        // A decode-driven reply staged in the "far" half of a cycle must still key
        // at its NEXT matching-parity boundary, two slots later. The boundary check
        // fires ~LeadMs before that boundary, so age is just under 2*slot.
        var rec = new Recorder();
        var staged = EvenSlotInstant();
        var clock = staged;
        var svc = new Ft8TxService(
            rec.Key, rec.Audio, rec.Broadcast, (_, _, _) => FakeAudio(),
            () => clock, static (_, _) => Task.CompletedTask, static () => false, NullLogger.Instance);
        svc.SetArmed(true);
        svc.Stage("KB2UKA K1ABC -12", 1500, "even", "FT8");
        long evenSlot = SlotIndexOf(staged, 15.0);

        var atLead = staged.AddSeconds(2 * 15.0).AddMilliseconds(-120); // just before S+2 boundary
        Assert.True(svc.ShouldKeyForSlot(evenSlot + 2, atLead));
    }

    [Fact]
    public void LateStart_FreshReplyInCurrentSlot_Keys()
    {
        // Hear a CQ in the even slot, settle ~2 s into the (matching even) reply
        // slot, stage the reply: the late-start gate keys it THIS slot rather than a
        // full cycle later. This is the path the boundary-only keyer silently missed.
        var rec = new Recorder();
        var slotStart = EvenSlotInstant();
        var clock = slotStart;
        var svc = new Ft8TxService(
            rec.Key, rec.Audio, rec.Broadcast, (_, _, _) => FakeAudio(),
            () => clock, static (_, _) => Task.CompletedTask, static () => false, NullLogger.Instance);
        svc.SetArmed(true);
        clock = slotStart.AddSeconds(2.0);
        svc.Stage("KB2UKA K1ABC FN12", 1500, "even", "FT8");
        Assert.True(svc.ShouldLateStartCurrentSlot(clock));
    }

    [Fact]
    public void LateStart_PastWindow_DefersToNextBoundary()
    {
        var rec = new Recorder();
        var slotStart = EvenSlotInstant();
        var clock = slotStart;
        var svc = new Ft8TxService(
            rec.Key, rec.Audio, rec.Broadcast, (_, _, _) => FakeAudio(),
            () => clock, static (_, _) => Task.CompletedTask, static () => false, NullLogger.Instance);
        svc.SetArmed(true);
        clock = slotStart.AddSeconds(5.0);                  // 5 s in > 2.5 s FT8 window
        svc.Stage("KB2UKA K1ABC FN12", 1500, "even", "FT8");
        Assert.False(svc.ShouldLateStartCurrentSlot(clock));
    }

    [Fact]
    public void LateStart_WithinLeadWindow_DefersToBoundaryPath()
    {
        var rec = new Recorder();
        var slotStart = EvenSlotInstant();
        var clock = slotStart;
        var svc = new Ft8TxService(
            rec.Key, rec.Audio, rec.Broadcast, (_, _, _) => FakeAudio(),
            () => clock, static (_, _) => Task.CompletedTask, static () => false, NullLogger.Instance);
        svc.SetArmed(true);
        clock = slotStart.AddMilliseconds(50);              // inside the boundary lead window
        svc.Stage("CQ KB2UKA FN12", 1500, "even", "FT8");
        Assert.False(svc.ShouldLateStartCurrentSlot(clock));
    }

    [Fact]
    public async Task HaltDuringRender_NeverKeys()
    {
        // The panic path must win even if Halt() fires while the synth is running.
        // Render happens BEFORE keying, so a mid-render Halt makes the post-render
        // re-check bail and MOX is never keyed (no keyed-without-IQ carrier).
        var rec = new Recorder();
        var now = EvenSlotInstant();
        Ft8TxService? svc = null;
        Func<string, Ft8Protocol, int, float[]?> renderer = (_, _, _) =>
        {
            svc!.Halt();          // operator panic mid-render
            return FakeAudio();
        };
        svc = NewService(rec, now, renderer);
        svc.SetArmed(true);
        svc.Stage("CQ KB2UKA FN12", 1500, "even", "FT8");

        await svc.TransmitCurrentStageAsync(CancellationToken.None);

        Assert.Equal(0, rec.KeyUps);   // never keyed after the panic
        Assert.Equal(0, rec.Blocks);
        Assert.False(rec.Keyed);
    }

    [Fact]
    public async Task Transmit_KeysThenUnkeys_AndEmits960SampleBlocks()
    {
        var rec = new Recorder();
        var now = EvenSlotInstant();
        var svc = NewService(rec, now);
        svc.SetArmed(true);
        svc.Stage("CQ KB2UKA FN12", 1500, "even", "FT8");

        await svc.TransmitCurrentStageAsync(CancellationToken.None);

        Assert.Equal(1, rec.KeyUps);
        Assert.Equal(1, rec.KeyDowns);
        Assert.False(rec.Keyed);                   // ended un-keyed
        Assert.True(rec.Blocks > 0);
        Assert.Equal(960, rec.LastBlockSamples);   // exactly one 20 ms 48 kHz block
        Assert.False(svc.Transmitting);
        // One stage = one transmission: the stage is cleared after sending.
        Assert.Null(svc.Status().Message);
    }

    [Fact]
    public async Task RenderFailure_DoesNotKey()
    {
        var rec = new Recorder();
        var now = EvenSlotInstant();
        var svc = NewService(rec, now, renderer: (_, _, _) => null);  // encode/synth failed
        svc.SetArmed(true);
        svc.Stage("garbage", 1500, "even", "FT8");

        await svc.TransmitCurrentStageAsync(CancellationToken.None);

        Assert.Equal(0, rec.KeyUps);
        Assert.Equal(0, rec.Blocks);
    }

    [Fact]
    public void Watchdog_DisarmsAfterWindow()
    {
        var rec = new Recorder();
        var armed = EvenSlotInstant();
        var clock = armed;
        var svc = new Ft8TxService(
            rec.Key, rec.Audio, rec.Broadcast, (_, _, _) => FakeAudio(),
            () => clock, static (_, _) => Task.CompletedTask, static () => false, NullLogger.Instance);
        svc.SetArmed(true);
        Assert.True(svc.Armed);

        clock = armed.AddMinutes(Ft8TxService.WatchdogMinutes + 1);
        svc.EnforceWatchdog(clock);
        Assert.False(svc.Armed);
    }

    [Fact]
    public void Halt_Disarms_AndClearsStage()
    {
        var rec = new Recorder();
        var now = EvenSlotInstant();
        var svc = NewService(rec, now);
        svc.SetArmed(true);
        svc.Stage("CQ KB2UKA FN12", 1500, "even", "FT8");
        Assert.True(svc.Armed);

        svc.Halt();

        Assert.False(svc.Armed);
        Assert.Null(svc.Status().Message);
        Assert.False(svc.ShouldKeyForSlot(SlotIndexOf(now, 15.0), now));
    }

    [Fact]
    public async Task DisarmMidStream_TruncatesAndUnkeys()
    {
        // With a delay that disarms partway through, the streamer's stillArmed
        // check stops feeding and the finally unkeys — a clean mid-slot abort.
        var rec = new Recorder();
        var now = EvenSlotInstant();
        Ft8TxService? svc = null;
        int ticks = 0;
        Func<int, CancellationToken, Task> delay = (_, _) =>
        {
            if (++ticks == 2) svc!.SetArmed(false);   // disarm after the 2nd block
            return Task.CompletedTask;
        };
        svc = new Ft8TxService(
            rec.Key, rec.Audio, rec.Broadcast, (_, _, _) => new float[960 * 10],
            () => now, delay, static () => false, NullLogger.Instance);
        svc.SetArmed(true);
        svc.Stage("CQ KB2UKA FN12", 1500, "even", "FT8");

        await svc.TransmitCurrentStageAsync(CancellationToken.None);

        Assert.True(rec.Blocks < 13);   // did NOT stream the full 6 lead + 10 audio blocks
        Assert.False(rec.Keyed);        // unkeyed on truncation
        Assert.Equal(1, rec.KeyDowns);  // released exactly once — never double-released
    }

    [Fact]
    public void Stage_RejectsBadSlotAndMode()
    {
        var rec = new Recorder();
        var svc = NewService(rec, EvenSlotInstant());
        Assert.NotNull(svc.Stage("CQ K1ABC", 1500, "sideways", "FT8"));
        Assert.NotNull(svc.Stage("CQ K1ABC", 1500, "even", "RTTY"));
        Assert.NotNull(svc.Stage("   ", 1500, "even", "FT8"));
        Assert.Null(svc.Stage("CQ K1ABC", 1500, "odd", "FT4"));
    }

    [Fact]
    public void Status_ReportsArmAndStage()
    {
        var rec = new Recorder();
        var now = EvenSlotInstant();
        var svc = NewService(rec, now);
        var s0 = svc.Status();
        Assert.False(s0.Armed);
        Assert.False(s0.Transmitting);

        svc.SetArmed(true);
        svc.Stage("CQ KB2UKA FN12", 1200, "odd", "FT4");
        var s1 = svc.Status();
        Assert.True(s1.Armed);
        Assert.Equal("FT4", s1.Mode);
        Assert.Equal("odd", s1.Slot);
        Assert.Equal(1200, s1.AudioHz);
        Assert.Equal("CQ KB2UKA FN12", s1.Message);
        Assert.True(s1.WatchdogSecsRemaining > 0);
    }

    // ---- keying discipline: pre-key gate + release-only-what-we-keyed --------

    [Fact]
    public async Task PreKeyGate_MoxAlreadyOn_SkipsSlot_WithoutKeying()
    {
        // Another source (operator, another plugin) holds MOX when the slot
        // fires: the keyer must SKIP the slot entirely — no key-up, no audio, no
        // slot-end release that would cut the other holder's key — and count it.
        var rec = new Recorder();
        var now = EvenSlotInstant();
        var svc = NewService(rec, now, moxAlreadyOn: static () => true);
        svc.SetArmed(true);
        svc.Stage("CQ KB2UKA FN12", 1500, "even", "FT8");

        await svc.TransmitCurrentStageAsync(CancellationToken.None);

        Assert.Equal(0, rec.KeyUps);
        Assert.Equal(0, rec.KeyDowns);            // never touched the foreign key
        Assert.Equal(0, rec.Blocks);
        Assert.Equal(1, svc.Status().SkippedSlots);
        // The stage is left in place — the freshness window expires it naturally.
        Assert.Equal("CQ KB2UKA FN12", svc.Status().Message);
    }

    [Fact]
    public void Halt_WithoutOurKey_NeverReleasesMox()
    {
        // weKeyed=false ⇒ halt must NOT call the keyer at all: MOX may be held by
        // the operator or another plugin, and dropping it is not ours to do.
        var rec = new Recorder();
        var svc = NewService(rec, EvenSlotInstant());
        svc.SetArmed(true);
        svc.Stage("CQ KB2UKA FN12", 1500, "even", "FT8");

        svc.Halt();

        Assert.Equal(0, rec.KeyUps);
        Assert.Equal(0, rec.KeyDowns);
    }

    [Fact]
    public void Disarm_WithoutOurKey_NeverReleasesMox()
    {
        var rec = new Recorder();
        var svc = NewService(rec, EvenSlotInstant());
        svc.SetArmed(true);

        svc.SetArmed(false);

        Assert.Equal(0, rec.KeyDowns);
    }

    [Fact]
    public void ForceDisarm_WithoutOurKey_NeverReleasesMox()
    {
        var rec = new Recorder();
        var svc = NewService(rec, EvenSlotInstant());
        svc.SetArmed(true);

        svc.ForceDisarm("test");

        Assert.False(svc.Armed);
        Assert.Equal(0, rec.KeyDowns);
    }

    [Fact]
    public void Watchdog_WithoutOurKey_NeverReleasesMox()
    {
        var rec = new Recorder();
        var armed = EvenSlotInstant();
        var clock = armed;
        var svc = new Ft8TxService(
            rec.Key, rec.Audio, rec.Broadcast, (_, _, _) => FakeAudio(),
            () => clock, static (_, _) => Task.CompletedTask, static () => false, NullLogger.Instance);
        svc.SetArmed(true);

        clock = armed.AddMinutes(Ft8TxService.WatchdogMinutes + 1);
        svc.EnforceWatchdog(clock);

        Assert.False(svc.Armed);
        Assert.Equal(0, rec.KeyDowns);
    }

    [Fact]
    public async Task HaltMidStream_ReleasesOurKey_ExactlyOnce()
    {
        // weKeyed=true ⇒ the halt path drops MOX, and the streamer's finally must
        // NOT release it a second time (halt already cleared _weKeyedMox).
        var rec = new Recorder();
        var now = EvenSlotInstant();
        Ft8TxService? svc = null;
        int ticks = 0;
        Func<int, CancellationToken, Task> delay = (_, _) =>
        {
            if (++ticks == 2) svc!.Halt();   // panic mid-stream, after our key-up
            return Task.CompletedTask;
        };
        svc = new Ft8TxService(
            rec.Key, rec.Audio, rec.Broadcast, (_, _, _) => new float[960 * 10],
            () => now, delay, static () => false, NullLogger.Instance);
        svc.SetArmed(true);
        svc.Stage("CQ KB2UKA FN12", 1500, "even", "FT8");

        await svc.TransmitCurrentStageAsync(CancellationToken.None);

        Assert.Equal(1, rec.KeyUps);
        Assert.Equal(1, rec.KeyDowns);   // halt released; the finally did not double-release
        Assert.False(rec.Keyed);
    }

    [Fact]
    public async Task StopAsync_WithoutOurKey_NeverReleasesMox()
    {
        var rec = new Recorder();
        var svc = NewService(rec, EvenSlotInstant());
        svc.SetArmed(true);

        await svc.StopAsync();

        Assert.False(svc.Armed);
        Assert.Equal(0, rec.KeyDowns);
    }
}

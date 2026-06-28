// SPDX-License-Identifier: GPL-2.0-or-later
//
// Load-bearing TX-safety coverage for the over-air playback state machine,
// driven entirely by injected SDK doubles — NEVER a live RadioService/TxService
// and NEVER any real socket/UDP/TCP I/O (that crashes the Windows CI host).
//
// The IRadioController stub models the host's swallow-the-bool behaviour:
// SetMoxAsync flips the IAudioPlaybackSink stub's IsMoxOn (or, in refuse mode,
// leaves it false) and returns a completed task — exactly like the real
// RadioController.SetMoxAsync(→ TxService.TrySetMox). This is the surface the
// ported plugin's post-key IsMoxOn confirm-gate guards against.

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Audio;
using Zeus.Server.Wav;

namespace Openhpsdr.Zeus.Plugins.Recorder.Tests;

public sealed class RecorderOverAirTests
{
    // ---- SDK doubles --------------------------------------------------------

    private sealed class StubSink : IAudioPlaybackSink
    {
        private int _moxOn;
        public bool RefuseKey;
        public int PlayOnAirCount;
        public int PlayLocalCount;
        public int BeginLocalMonitorCount;

        public bool IsMoxOn => Volatile.Read(ref _moxOn) != 0;
        public void SetMox(bool on) => Volatile.Write(ref _moxOn, on ? 1 : 0);

        public IDisposable BeginLocalMonitor()
        {
            Interlocked.Increment(ref BeginLocalMonitorCount);
            return new Noop();
        }
        public bool PlayLocal(ReadOnlySpan<float> samples, int sampleRate)
        {
            Interlocked.Increment(ref PlayLocalCount);
            return true;
        }
        public long LocalMonitorBacklog => 0;
        public void PlayOnAir(ReadOnlySpan<float> samples, int sampleRate)
            => Interlocked.Increment(ref PlayOnAirCount);

        private sealed class Noop : IDisposable { public void Dispose() { } }
    }

    private sealed class StubController : IRadioController
    {
        private readonly StubSink _sink;
        public int KeyOnCount;
        public int KeyOffCount;
        public StubController(StubSink sink) => _sink = sink;

        public Task SetFrequencyAsync(long hz, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetModeAsync(string mode, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetMoxAsync(bool keyed, CancellationToken ct = default)
        {
            if (keyed)
            {
                Interlocked.Increment(ref KeyOnCount);
                // Host TrySetMox runs synchronously; a refusal leaves MOX off and
                // the swallowed bool is invisible to the caller — the plugin must
                // detect it by re-reading IsMoxOn.
                if (!_sink.RefuseKey) _sink.SetMox(true);
            }
            else
            {
                Interlocked.Increment(ref KeyOffCount);
                _sink.SetMox(false);
            }
            return Task.CompletedTask;
        }
    }

    // ---- Harness ------------------------------------------------------------

    private static string NewRoot(out string sandbox)
    {
        sandbox = Path.Combine(Path.GetTempPath(), "zeus-recorder-air", Guid.NewGuid().ToString("N"));
        string root = Path.Combine(sandbox, WavLibrary.ManagedFolderName);
        Directory.CreateDirectory(root);
        return root;
    }

    private static string WriteClip(string root, string name, int samples)
    {
        string path = Path.Combine(root, name);
        using var w = new WavWriter(path, 48_000);
        var buf = new float[samples];
        Array.Fill(buf, 0.1f);
        w.Append(buf);
        return name; // root-relative
    }

    private static WavRecorderService NewSvc(
        StubSink sink, IRadioController? ctrl, string root)
        => new(sink, ctrl, NullLogger.Instance, root, migrate: false, moxPreKeyDelayMs: 0);

    private static void WaitForIdle(WavRecorderService svc, int timeoutMs = 5_000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (svc.GetStatus().State != "idle" && sw.ElapsedMilliseconds < timeoutMs)
            Thread.Sleep(10);
    }

    // ---- (a) weKeyed path ---------------------------------------------------

    [Fact]
    public void Air_OnUnkeyedRig_KeysOnce_ThenDropsOnceAtFinish()
    {
        string root = NewRoot(out var sandbox);
        try
        {
            var sink = new StubSink();
            var ctrl = new StubController(sink);
            using var svc = NewSvc(sink, ctrl, root);
            string rel = WriteClip(root, "zeus-tx-air.wav", 4_800); // 0.1 s

            Assert.False(sink.IsMoxOn);
            svc.Play(rel, WavPlayDest.Air);
            Assert.True(sink.IsMoxOn, "must key MOX when the rig is unkeyed");
            Assert.Equal(1, ctrl.KeyOnCount);

            WaitForIdle(svc);
            Assert.Equal("idle", svc.GetStatus().State);
            Assert.Equal(1, ctrl.KeyOffCount);
            Assert.False(sink.IsMoxOn, "the key we raised must be released at finish");
            Assert.True(sink.PlayOnAirCount > 0, "audio must have been injected on air");
        }
        finally { try { Directory.Delete(sandbox, true); } catch { } }
    }

    // ---- (b) ride the operator's key ---------------------------------------

    [Fact]
    public void Air_OnAlreadyKeyedRig_NeverKeys_NeverDrops()
    {
        string root = NewRoot(out var sandbox);
        try
        {
            var sink = new StubSink();
            var ctrl = new StubController(sink);
            using var svc = NewSvc(sink, ctrl, root);
            string rel = WriteClip(root, "zeus-tx-ride.wav", 4_800);

            sink.SetMox(true); // operator already keyed
            svc.Play(rel, WavPlayDest.Air);
            Assert.Equal(0, ctrl.KeyOnCount);

            WaitForIdle(svc);
            Assert.Equal(0, ctrl.KeyOffCount);
            Assert.True(sink.IsMoxOn, "must NOT drop a key the operator owns");
        }
        finally { try { Directory.Delete(sandbox, true); } catch { } }
    }

    // ---- (c) refused key — the regression the confirm-gate fixes ------------

    [Fact]
    public void Air_RefusedKey_ThrowsBeforePump_NoStrandedKey()
    {
        string root = NewRoot(out var sandbox);
        try
        {
            var sink = new StubSink { RefuseKey = true };
            var ctrl = new StubController(sink);
            using var svc = NewSvc(sink, ctrl, root);
            string rel = WriteClip(root, "zeus-tx-refused.wav", 4_800);

            Assert.Throws<InvalidOperationException>(() => svc.Play(rel, WavPlayDest.Air));

            // We attempted exactly one key, it was refused (MOX stayed off), and
            // we never raised a drop on a key we don't own. Pump never launched.
            Assert.Equal(1, ctrl.KeyOnCount);
            Assert.Equal(0, ctrl.KeyOffCount);
            Assert.False(sink.IsMoxOn);
            Assert.Equal("idle", svc.GetStatus().State);
            Assert.Equal(0, sink.PlayOnAirCount);
        }
        finally { try { Directory.Delete(sandbox, true); } catch { } }
    }

    // ---- (d) operator unkeys mid-clip --------------------------------------

    [Fact]
    public void Air_OperatorUnkeyMidClip_StopsPump_SingleDrop()
    {
        string root = NewRoot(out var sandbox);
        try
        {
            var sink = new StubSink();
            var ctrl = new StubController(sink);
            using var svc = NewSvc(sink, ctrl, root);
            string rel = WriteClip(root, "zeus-tx-long.wav", 96_000); // 2 s

            svc.Play(rel, WavPlayDest.Air);
            Assert.Equal(1, ctrl.KeyOnCount);

            // Let the settle + a few blocks elapse, then the operator unkeys.
            Thread.Sleep(150);
            sink.SetMox(false);

            WaitForIdle(svc);
            Assert.Equal("idle", svc.GetStatus().State);
            // Exactly one drop (the key we raised) — never a second/foreign drop.
            Assert.Equal(1, ctrl.KeyOffCount);
        }
        finally { try { Directory.Delete(sandbox, true); } catch { } }
    }

    // ---- (f) local playback never touches keying ---------------------------

    [Fact]
    public void Local_NeverKeys_UsesLocalMonitorSink()
    {
        string root = NewRoot(out var sandbox);
        try
        {
            var sink = new StubSink();
            var ctrl = new StubController(sink);
            using var svc = NewSvc(sink, ctrl, root);
            string rel = WriteClip(root, "zeus-rx-local.wav", 4_800);

            svc.Play(rel, WavPlayDest.Local);
            WaitForIdle(svc);

            Assert.Equal(0, ctrl.KeyOnCount);
            Assert.Equal(0, ctrl.KeyOffCount);
            Assert.Equal(0, sink.PlayOnAirCount);
            Assert.True(sink.PlayLocalCount > 0, "local audio must reach the monitor sink");
            Assert.Equal(1, sink.BeginLocalMonitorCount);
        }
        finally { try { Directory.Delete(sandbox, true); } catch { } }
    }

    // ---- (g) ControlRadio denied → degrade, never throw, never key ----------

    [Fact]
    public void Air_NoController_Degrades_NeverThrows_NeverKeys()
    {
        string root = NewRoot(out var sandbox);
        try
        {
            var sink = new StubSink();
            using var svc = NewSvc(sink, ctrl: null, root); // ControlRadio not granted
            string rel = WriteClip(root, "zeus-tx-degraded.wav", 4_800);

            // Must not throw; with no key and no controller the pump rides only an
            // existing operator key — here there is none, so nothing goes on air.
            var ex = Record.Exception(() => svc.Play(rel, WavPlayDest.Air));
            Assert.Null(ex);

            WaitForIdle(svc);
            Assert.Equal("idle", svc.GetStatus().State);
            Assert.Equal(0, sink.PlayOnAirCount);
            Assert.False(sink.IsMoxOn);
        }
        finally { try { Directory.Delete(sandbox, true); } catch { } }
    }

    // ---- Settings seam: SetRecordingsRoot persists via the callback --------

    [Fact]
    public void SetRecordingsRoot_InvokesPersistCallback_AndResetsToDefault()
    {
        string root = NewRoot(out var sandbox);
        try
        {
            var sink = new StubSink();
            var persisted = new List<string?>();
            using var svc = new WavRecorderService(
                sink, null, NullLogger.Instance, root, migrate: false,
                persistRoot: persisted.Add, moxPreKeyDelayMs: 0);

            string chosen = Path.Combine(sandbox, "Relocated");
            var (newRoot, isDefault) = svc.SetRecordingsRoot(chosen);
            Assert.Equal(Path.GetFullPath(chosen), newRoot);
            Assert.False(isDefault);
            Assert.Equal(Path.GetFullPath(chosen), persisted[^1]);

            // Null resets to the platform default and clears the persisted value.
            var (resetRoot, resetDefault) = svc.SetRecordingsRoot(null);
            Assert.Equal(WavLibrary.DefaultRoot(), resetRoot);
            Assert.True(resetDefault);
            Assert.Null(persisted[^1]);
        }
        finally { try { Directory.Delete(sandbox, true); } catch { } }
    }

    // ---- Capture: RX tap writes a recording --------------------------------

    [Fact]
    public void Record_Rx_CapturesFedAudio_FinalizesFile()
    {
        string root = NewRoot(out var sandbox);
        try
        {
            var sink = new StubSink();
            using var svc = NewSvc(sink, new StubController(sink), root);

            string rel = svc.StartRecording(WavRecordSource.Rx);
            var block = new float[960];
            Array.Fill(block, 0.2f);
            for (int i = 0; i < 5; i++) svc.FeedRxAudio(block, 48_000);
            // A TX-source block while recording RX must be ignored.
            svc.FeedTxMicAudio(block, 48_000);
            var stop = svc.StopRecording();

            Assert.NotNull(stop);
            Assert.Equal(5 * 960, stop!.Value.Samples);
            Assert.Equal(rel, stop.Value.RelPath);
            Assert.True(File.Exists(Path.Combine(root, rel)));
            Assert.Contains(svc.ListRecordings(), r => r.RelPath == rel);
        }
        finally { try { Directory.Delete(sandbox, true); } catch { } }
    }
}

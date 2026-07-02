// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus Digital plugin — extracted from Zeus core
// (Zeus.Server.Hosting/Ft8TxService.cs). GPL-2.0-or-later.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Ft8TxService — the FT8/FT4 ARMED auto-sequence keyer (the TX half; the RX
// decode pipeline lives in Ft8Service and is untouched). It owns the UTC slot
// clock, keys MOX through the host (IRadioController.SetMoxAsync — the host
// tags it MoxSource.Plugin), and streams the synthesized tone audio into the
// host TX chain via IAudioPlaybackSink.PlayOnAir. The frontend QSO state
// machine decides WHAT to say each window and STAGES it; this service decides
// WHEN to key and does the keying.
//
// SAFETY (KB2UKA standing orders for digital auto-seq):
//   * NEVER auto-arm. `_armed` defaults false and is only set by an explicit
//     operator ENABLE-TX action (POST ft8/tx/arm). No arm on startup,
//     connect, or workspace entry.
//   * NEVER transmit without a FRESH staged message whose slot parity matches
//     the slot being entered. "No message staged ⇒ no transmission" is the
//     structural guarantee — see ShouldKeyForSlot.
//   * A backend WATCHDOG auto-disarms after WatchdogMinutes — a HARD MAXIMUM
//     ARMED DURATION measured from the operator's arm action, independent of (and
//     additional to) the frontend Halt / no-reply-stop / disable-after-73, which
//     all arrive as arm/halt POSTs. It is intentionally a duration cap, not an
//     idle detector: it always trips N minutes after arming so an abandoned rig
//     can never beacon forever. (A future operator-activity keep-alive that
//     refreshes the cap for an attended pileup run is tracked as a follow-up.)
//   * KEYING DISCIPLINE (Recorder's full pattern — every plugin keys as the
//     shared MoxSource.Plugin, so the host cannot referee between plugins):
//     (a) PRE-KEY GATE — if MOX is already on before our own key-on, SKIP the
//         slot entirely (never ride an existing key; logged + counted in
//         status as a skipped slot).
//     (b) `_weKeyedMox` — release MOX ONLY if we keyed it, on EVERY drop path
//         (end-of-over, halt, disarm, watchdog, force-disarm, shutdown).
//     (c) POST-KEY CONFIRM — the host swallows a key-up refusal, so re-read
//         IsMoxOn after SetMoxAsync(true); still-false means refused → abort
//         the slot without streaming.
//   * PureSignal is never read or written here. Drive/power defaults are never
//     touched — the keyer only keys MOX and feeds audio.

using Microsoft.Extensions.Logging;
using Openhpsdr.Zeus.Plugins.Digital.Dsp;
using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Audio;

namespace Openhpsdr.Zeus.Plugins.Digital;

public sealed class Ft8TxService : IDisposable, IDigitalTxKeyer
{
    /// <summary>Keying delegate (production wraps <c>IRadioController.SetMoxAsync</c>
    /// with the post-key <c>IsMoxOn</c> confirm-gate).</summary>
    internal delegate bool MoxKeyer(bool on, out string? error);

    // Lead the slot boundary by this much (ms) so MOX/T-R settles before audio.
    private const int LeadMs = 120;
    // Hard maximum armed duration (minutes) measured from the arm action.
    internal const int WatchdogMinutes = 10;

    private readonly MoxKeyer _keyer;
    private readonly Action<ReadOnlyMemory<float>> _audioSink;
    private readonly Action<Ft8TxStatusDto> _broadcast;
    private readonly Func<string, Ft8Protocol, int, float[]?> _renderer;
    private readonly Func<DateTime> _clock;
    private readonly Func<int, CancellationToken, Task> _delay;
    private readonly Func<bool> _moxAlreadyOn;
    private readonly ILogger _log;
    private DigitalTxArbiter? _arbiter;        // single-owner interlock vs WSPR

    private readonly object _lock = new();
    private bool _armed;                       // ENABLE-TX master — defaults false
    private Ft8Protocol _mode = Ft8Protocol.Ft8;
    private string? _stagedMessage;
    private int _stagedAudioHz = 1500;
    private int _stagedParity;                 // 0 = even, 1 = odd
    private DateTime _stagedAtUtc;
    private DateTime _armedSinceUtc;
    private bool _transmitting;
    private bool _weKeyedMox;                  // we raised MOX; only we may drop it
    private int _skippedSlots;                 // pre-key gate refusals (MOX already on)
    private long? _lastTxSlotMs;

    private CancellationTokenSource? _cts;
    private Task? _clockLoop;

    /// <summary>Production constructor — wires the host keying / audio / broadcast seams.
    /// <paramref name="radioController"/> is null when ControlRadio was not granted:
    /// the keyer then refuses every key-up (armed TX is structurally impossible).</summary>
    public Ft8TxService(
        IRadioController? radioController, IAudioPlaybackSink sink,
        Action<Ft8TxStatusDto> broadcast, DigitalTxArbiter arbiter, ILogger logger)
        : this(
            MakeKeyer(radioController, sink),
            block => sink.PlayOnAir(block.Span, DigitalTxStreamer.SampleRate),
            broadcast,
            DefaultRenderer,
            static () => DateTime.UtcNow,
            static (ms, ct) => Task.Delay(ms, ct),
            () => sink.IsMoxOn,
            logger)
    {
        SetArbiter(arbiter);
    }

    /// <summary>Test constructor — injects keying / audio / clock / pacing as
    /// delegates so the scheduler can be driven deterministically with no native
    /// dependency and no real-time waits.</summary>
    internal Ft8TxService(
        MoxKeyer keyer,
        Action<ReadOnlyMemory<float>> audioSink,
        Action<Ft8TxStatusDto> broadcast,
        Func<string, Ft8Protocol, int, float[]?> renderer,
        Func<DateTime> clock,
        Func<int, CancellationToken, Task> delay,
        Func<bool> moxAlreadyOn,
        ILogger logger)
    {
        _keyer = keyer;
        _audioSink = audioSink;
        _broadcast = broadcast;
        _renderer = renderer;
        _clock = clock;
        _delay = delay;
        _moxAlreadyOn = moxAlreadyOn;
        _log = logger;
    }

    // The host RadioController.SetMoxAsync runs the key change synchronously,
    // returns a completed task, and SWALLOWS a refusal — so the keyer CONFIRMS a
    // key-up actually took by re-reading IsMoxOn after the await (a band guard /
    // not-connected race / interlock refusal leaves it false). Without
    // ControlRadio the controller is null → every key-up is refused, so the
    // armed keyer can never put RF on the air.
    private static MoxKeyer MakeKeyer(IRadioController? radioController, IAudioPlaybackSink sink) =>
        (bool on, out string? error) =>
        {
            error = null;
            if (radioController is null)
            {
                error = "ControlRadio capability not granted";
                return false;
            }
            radioController.SetMoxAsync(on).GetAwaiter().GetResult();
            if (on && !sink.IsMoxOn)
            {
                error = "MOX key-up refused by the host";
                return false;
            }
            return true;
        };

    /// <summary>Attach (and register with) the cross-service arm interlock. Called
    /// from the production ctor; tests opt in explicitly when exercising it.</summary>
    internal void SetArbiter(DigitalTxArbiter arbiter)
    {
        _arbiter = arbiter;
        arbiter.Register(this);
    }

    /// <summary>True if the FT8/FT4 encode + synth path is usable on this platform.</summary>
    public bool NativeAvailable => Ft8Decoder.IsAvailable;

    internal bool Armed { get { lock (_lock) return _armed; } }
    internal bool Transmitting { get { lock (_lock) return _transmitting; } }

    /// <summary>Start the UTC slot clock loop. Called once from plugin init.</summary>
    public void Start()
    {
        _cts = new CancellationTokenSource();
        _clockLoop = Task.Run(() => ClockLoopAsync(_cts.Token));
    }

    /// <summary>Stop the clock loop, disarm, and release any MOX we raised.</summary>
    public async Task StopAsync()
    {
        _cts?.Cancel();
        bool drop;
        lock (_lock) { _armed = false; drop = _weKeyedMox; _weKeyedMox = false; }
        if (drop) _keyer(false, out _);
        if (_clockLoop is not null)
        {
            try { await _clockLoop.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* normal shutdown */ }
        }
    }

    // ---- operator control surface (called by the plugin endpoints) -----------

    /// <summary>The ENABLE-TX master. The ONLY way TX turns on. Stamps the
    /// watchdog clock on a fresh arm; disarm clears any staged message.</summary>
    public void SetArmed(bool enabled)
    {
        bool drop = false;
        lock (_lock)
        {
            if (enabled)
            {
                if (!_armed) _armedSinceUtc = _clock();
                _armed = true;
            }
            else
            {
                _armed = false;
                _stagedMessage = null;
                drop = _weKeyedMox;
                _weKeyedMox = false;
            }
        }
        // Single-owner interlock: arming FT8/FT4 force-disarms a running WSPR
        // beacon (and vice-versa) so only one digital keyer holds the MOX source.
        if (enabled) _arbiter?.Claim(this);
        if (drop) _keyer(false, out _);
        BroadcastStatus();
    }

    /// <summary>Arbiter-driven force-disarm (a sibling digital keyer armed). Like
    /// <see cref="Halt"/> but does not re-enter the arbiter.</summary>
    public void ForceDisarm(string reason)
    {
        bool drop;
        lock (_lock)
        {
            if (!_armed) return;
            _armed = false;
            _stagedMessage = null;
            drop = _weKeyedMox;
            _weKeyedMox = false;
        }
        if (drop) _keyer(false, out _);
        _log.LogInformation("ft8.tx force-disarmed: {Reason}", reason);
        BroadcastStatus();
    }

    /// <summary>Stage the next transmission (replaces any pending stage). Returns
    /// an error string on bad input, or null on success.</summary>
    public string? Stage(string message, int audioHz, string slot, string mode)
    {
        if (string.IsNullOrWhiteSpace(message)) return "message is required";
        Ft8Protocol proto;
        switch (mode?.Trim().ToUpperInvariant())
        {
            case null:
            case "":
            case "FT8": proto = Ft8Protocol.Ft8; break;
            case "FT4": proto = Ft8Protocol.Ft4; break;
            default: return "mode must be 'FT8' or 'FT4'";
        }
        int parity = slot?.Trim().ToLowerInvariant() switch
        {
            "even" => 0,
            "odd" => 1,
            _ => -1,
        };
        if (parity < 0) return "slot must be 'even' or 'odd'";

        lock (_lock)
        {
            _mode = proto;
            _stagedMessage = message.Trim().ToUpperInvariant();
            _stagedAudioHz = Math.Clamp(audioHz, 0, 2500);
            _stagedParity = parity;
            _stagedAtUtc = _clock();
        }
        BroadcastStatus();
        return null;
    }

    /// <summary>Panic / abort: clear the stage, disarm, and drop MOX (if WE keyed it) mid-slot.</summary>
    public void Halt()
    {
        bool drop;
        lock (_lock)
        {
            _armed = false;
            _stagedMessage = null;
            drop = _weKeyedMox;
            _weKeyedMox = false;
        }
        if (drop) _keyer(false, out _);
        _log.LogInformation("ft8.tx halt");
        BroadcastStatus();
    }

    /// <summary>Current keyer status DTO.</summary>
    public Ft8TxStatusDto Status()
    {
        lock (_lock)
        {
            int watchdog = 0;
            if (_armed)
            {
                double remaining = WatchdogMinutes * 60.0 - (_clock() - _armedSinceUtc).TotalSeconds;
                watchdog = (int)Math.Max(0, Math.Ceiling(remaining));
            }
            return new Ft8TxStatusDto(
                _armed,
                _transmitting,
                _mode == Ft8Protocol.Ft4 ? "FT4" : "FT8",
                _stagedMessage,
                _stagedAudioHz,
                _stagedParity == 1 ? "odd" : "even",
                watchdog,
                _lastTxSlotMs,
                NativeAvailable,
                _skippedSlots);
        }
    }

    // ---- scheduler internals (also the test surface) -------------------------

    private static double SlotSecondsFor(Ft8Protocol p) => p == Ft8Protocol.Ft4 ? 7.5 : 15.0;

    // How late into a slot a fresh same-parity reply may still key it (positive DT,
    // well inside the decoder's DT tolerance: FT8 ≈ ±2.5 s, FT4 ≈ ±1 s). The runner
    // stages decode-driven replies a couple seconds into the slot, so without this
    // the reply could only fire a FULL cycle later (next matching boundary). G2
    // bench-tune.
    private static double MaxLateStartSecondsFor(Ft8Protocol p) => p == Ft8Protocol.Ft4 ? 1.0 : 2.5;

    private double SlotSeconds { get { lock (_lock) return SlotSecondsFor(_mode); } }

    /// <summary>The only place RF is keyed. True iff the keyer should transmit in
    /// the slot whose index is <paramref name="slotIndex"/>: armed, a message is
    /// staged, the slot parity matches, the stage is fresh, and the watchdog has
    /// not expired. The freshness window is ONE FULL TX CYCLE (two slots): a reply
    /// staged in the "far" half of a cycle must survive to its next matching-parity
    /// boundary, which is two slots after the slot it was staged in. Double-fire is
    /// impossible because <see cref="_stagedMessage"/> is nulled after each
    /// transmission and at most one matching-parity boundary falls inside the
    /// window. No fresh matching stage ⇒ no transmission.</summary>
    internal bool ShouldKeyForSlot(long slotIndex, DateTime nowUtc)
    {
        lock (_lock)
        {
            if (!_armed || _stagedMessage is null) return false;
            if ((nowUtc - _armedSinceUtc).TotalMinutes >= WatchdogMinutes) return false;
            long parity = ((slotIndex % 2) + 2) % 2;
            if (parity != _stagedParity) return false;
            double age = (nowUtc - _stagedAtUtc).TotalSeconds;
            return age >= 0 && age < 2.0 * SlotSecondsFor(_mode);
        }
    }

    /// <summary>Enforce the hard max-armed-duration cap: disarm (and drop MOX if
    /// WE keyed it) once the arm has been held longer than <see cref="WatchdogMinutes"/>.</summary>
    internal void EnforceWatchdog(DateTime nowUtc)
    {
        bool fired = false, drop = false;
        lock (_lock)
        {
            if (_armed && (nowUtc - _armedSinceUtc).TotalMinutes >= WatchdogMinutes)
            {
                _armed = false;
                _stagedMessage = null;
                fired = true;
                drop = _weKeyedMox;
                _weKeyedMox = false;
            }
        }
        if (!fired) return;
        if (drop) _keyer(false, out _);
        _log.LogWarning("ft8.tx watchdog disarmed after {Min} min max armed duration", WatchdogMinutes);
        BroadcastStatus();
    }

    /// <summary>Key, render the staged message, stream it as 20 ms blocks, then
    /// unkey and clear the stage (one stage = one transmission). Truncates cleanly
    /// if disarmed / halted mid-slot; skips the slot if MOX is already keyed by
    /// another source (never rides an existing key).</summary>
    internal async Task TransmitCurrentStageAsync(CancellationToken ct)
    {
        string message;
        Ft8Protocol proto;
        int audioHz;
        lock (_lock)
        {
            if (!_armed || _stagedMessage is null) return;
            message = _stagedMessage;
            proto = _mode;
            audioHz = _stagedAudioHz;
        }

        // Render BEFORE claiming the transmit and keying MOX. The synth of ~607k
        // samples takes ~10-15 ms; if Halt() / SetArmed(false) / the watchdog fires
        // during that window, the re-check below observes _armed == false and bails
        // WITHOUT keying. Keying first (the old order) left MOX up after a panic with
        // zero IQ fed → the documented "keyed-without-IQ → bare carrier" G2 footgun.
        float[]? audio = _renderer(message, proto, audioHz);
        if (audio is null || audio.Length == 0)
        {
            _log.LogWarning("ft8.tx render failed for '{Msg}' ({Proto})", message, proto);
            lock (_lock) { if (ReferenceEquals(_stagedMessage, message)) _stagedMessage = null; }
            return;
        }

        // Pre-key gate: MOX already on means another source (operator, another
        // plugin) holds the key. Riding it would mix two TX audio feeds on the
        // air and our slot-end release would cut the other holder's key, so the
        // slot is SKIPPED entirely. The stage is left in place; the freshness
        // window guarantees it expires before a second matching boundary.
        if (_moxAlreadyOn())
        {
            int n;
            lock (_lock) n = ++_skippedSlots;
            _log.LogWarning(
                "ft8.tx slot skipped: MOX already keyed by another source (skipped #{N})", n);
            BroadcastStatus();
            return;
        }

        lock (_lock)
        {
            if (!_armed || _stagedMessage is null) return; // halted/disarmed during render
            _transmitting = true;
        }

        try
        {
            if (!_keyer(true, out string? err))
            {
                _log.LogWarning("ft8.tx key-up refused: {Err}", err);
                return;
            }
            lock (_lock) _weKeyedMox = true;
            BroadcastStatus();

            int leadBlocks = Math.Max(1, LeadMs / DigitalTxStreamer.BlockMs);
            await DigitalTxStreamer.StreamAsync(audio, leadBlocks, _audioSink, _delay, StillArmed, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            // Release ONLY what we keyed: a halt/disarm/watchdog path that already
            // dropped MOX also cleared _weKeyedMox, so this never double-releases
            // and never touches a key some other source holds.
            bool drop;
            lock (_lock)
            {
                drop = _weKeyedMox;
                _weKeyedMox = false;
                _transmitting = false;
                _stagedMessage = null; // one stage = one transmission
            }
            if (drop) _keyer(false, out _);
            BroadcastStatus();
        }
    }

    private bool StillArmed() { lock (_lock) return _armed; }

    private async Task ClockLoopAsync(CancellationToken ct)
    {
        long lastBoundarySlot = long.MinValue;   // boundary check already done for this nextSlot
        long lastLaunchedSlot = long.MinValue;    // a transmission was launched for this slot
        while (!ct.IsCancellationRequested)
        {
            DateTime now = _clock();
            EnforceWatchdog(now);

            double slotSec = SlotSeconds;
            double totalSec = (now - DateTime.UnixEpoch).TotalSeconds;
            long curSlot = (long)Math.Floor(totalSec / slotSec);
            long nextSlot = curSlot + 1;
            double msToBoundary = (nextSlot * slotSec - totalSec) * 1000.0;

            if (msToBoundary <= LeadMs && nextSlot != lastBoundarySlot)
            {
                // Preferred path: key the UPCOMING slot at its boundary (audio at ~+0).
                lastBoundarySlot = nextSlot;
                if (!Transmitting && lastLaunchedSlot != nextSlot && ShouldKeyForSlot(nextSlot, now))
                {
                    lastLaunchedSlot = nextSlot;
                    LaunchTransmit(nextSlot, slotSec, ct);
                }
            }
            else if (!Transmitting && lastLaunchedSlot != curSlot && ShouldLateStartCurrentSlot(now))
            {
                // Late-start path: a decode-driven reply was staged a couple seconds
                // into the CURRENT slot whose parity matches — key NOW (positive DT,
                // inside decoder tolerance) instead of waiting a full cycle.
                lastLaunchedSlot = curSlot;
                LaunchTransmit(curSlot, slotSec, ct);
            }

            // Cap every sleep at 250 ms so arm/disarm/halt are honoured promptly
            // and Windows' ~15 ms timer granularity is absorbed.
            double waitMs = msToBoundary - LeadMs;
            if (waitMs <= 0) waitMs = msToBoundary + 5; // settle just past the boundary
            int sleep = (int)Math.Clamp(waitMs, 5, 250);
            try { await _delay(sleep, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>True iff the CURRENT slot should be keyed late: a fresh same-parity
    /// stage exists, we are past the boundary lead window (which the boundary path
    /// owns) but still within <see cref="MaxLateStartSecondsFor"/> of the slot start
    /// (so the resulting positive DT stays inside the decoder's tolerance).</summary>
    internal bool ShouldLateStartCurrentSlot(DateTime nowUtc)
    {
        Ft8Protocol mode;
        lock (_lock) mode = _mode;
        double slotSec = SlotSecondsFor(mode);
        double totalSec = (nowUtc - DateTime.UnixEpoch).TotalSeconds;
        long curSlot = (long)Math.Floor(totalSec / slotSec);
        double secsIntoCur = totalSec - curSlot * slotSec;
        if (secsIntoCur * 1000.0 <= LeadMs) return false;            // boundary path owns the lead window
        if (secsIntoCur > MaxLateStartSecondsFor(mode)) return false; // too late — DT would exceed tolerance
        return ShouldKeyForSlot(curSlot, nowUtc);
    }

    private void LaunchTransmit(long targetSlot, double slotSec, CancellationToken ct)
    {
        long slotStartMs = (long)(targetSlot * slotSec * 1000.0);
        lock (_lock) _lastTxSlotMs = slotStartMs;
        _ = Task.Run(() => SafeTransmitAsync(ct), ct);
    }

    private async Task SafeTransmitAsync(CancellationToken ct)
    {
        try { await TransmitCurrentStageAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* shutdown / halt */ }
        catch (Exception ex) { _log.LogError(ex, "ft8.tx slot transmit failed"); }
    }

    private void BroadcastStatus()
    {
        try { _broadcast(Status()); }
        catch (Exception ex) { _log.LogDebug(ex, "ft8.tx status broadcast failed"); }
    }

    private static float[]? DefaultRenderer(string message, Ft8Protocol proto, int audioHz)
    {
        byte[]? tones = Ft8Decoder.Encode(message, proto);
        if (tones is null) return null;
        return Ft8Synth.Synth(tones, proto, audioHz, Ft8Synth.TxSampleRate);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}

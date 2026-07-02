// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus Digital plugin — extracted from Zeus core
// (Zeus.Server.Hosting/WsprTxService.cs). GPL-2.0-or-later.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// WsprTxService — the WSPR beacon keyer (the TX half; WSPR RX spotting lives in
// WsprService and is untouched). WSPR is a beacon mode with no QSO sequencing,
// so this is simpler than Ft8TxService: a 120 s UTC slot clock that, on each
// even-minute boundary, keys MOX through the host (IRadioController — tagged
// MoxSource.Plugin) and streams the K1JT/K9AN-synthesized beacon waveform,
// gated by a configurable transmit fraction (txPercent, WSJT-X "fraction of
// slots") so two-minute beacons don't run back-to-back unless asked.
//
// SAFETY: same standing orders as Ft8TxService — `_armed` defaults false and is
// only set by an explicit POST wspr/tx/arm; a backend watchdog auto-disarms
// after WatchdogMinutes (a HARD MAXIMUM ARMED DURATION measured from the arm
// action, not an idle detector); the full keying discipline (pre-key skip gate,
// `_weKeyedMox` release-only-what-we-keyed, post-key IsMoxOn confirm — see
// Ft8TxService header) applies verbatim; PureSignal is never touched; drive/
// power defaults are never touched. Audio is the pure-native WSPR synth fed
// through IAudioPlaybackSink.PlayOnAir — no new native code. A shared
// DigitalTxArbiter ensures WSPR and FT8/FT4 are never armed at the same time
// (they share the plugin MOX source).

using Microsoft.Extensions.Logging;
using Openhpsdr.Zeus.Plugins.Digital.Dsp;
using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Audio;

namespace Openhpsdr.Zeus.Plugins.Digital;

public sealed class WsprTxService : IDisposable, IDigitalTxKeyer
{
    internal delegate bool MoxKeyer(bool on, out string? error);

    private const double SlotSeconds = 120.0;   // WSPR 2-minute slots
    private const int LeadMs = 120;             // key this far before the boundary (relay settle)
    private const int WsprStartMs = 1000;       // WSPR audio starts ~1 s into the slot
    // Hard maximum armed duration (minutes); beacons run longer so this is wider.
    internal const int WatchdogMinutes = 30;

    /// <summary>Canonical WSPR power-encoding steps (dBm). The protocol only encodes
    /// values whose units digit is 0, 3 or 7; anything else makes WsprDecoder.Encode
    /// return null and the beacon silently never transmits.</summary>
    private static readonly int[] WsprDbmSteps =
        { 0, 3, 7, 10, 13, 17, 20, 23, 27, 30, 33, 37, 40, 43, 47, 50, 53, 57, 60 };

    private readonly MoxKeyer _keyer;
    private readonly Action<ReadOnlyMemory<float>> _audioSink;
    private readonly Action<Ft8TxStatusDto> _broadcast;
    private readonly Func<string, int, float[]?> _renderer;
    private readonly Func<DateTime> _clock;
    private readonly Func<int, CancellationToken, Task> _delay;
    private readonly Func<double> _random;
    private readonly Func<bool> _moxAlreadyOn;
    private readonly ILogger _log;
    private DigitalTxArbiter? _arbiter;          // single-owner interlock vs FT8/FT4

    private readonly object _lock = new();
    private bool _armed;                         // ENABLE-TX master — defaults false
    private string _call = "";
    private string _grid4 = "";
    private int _dBm = 30;
    private int _audioHz = 1500;
    private double _txPercent = 0.2;             // conservative default
    private DateTime _armedSinceUtc;
    private bool _transmitting;
    private bool _weKeyedMox;                    // we raised MOX; only we may drop it
    private int _skippedSlots;                   // pre-key gate refusals (MOX already on)
    private long? _lastTxSlotMs;

    private CancellationTokenSource? _cts;
    private Task? _clockLoop;

    /// <summary>Production constructor — wires the host keying / audio / broadcast seams.
    /// <paramref name="radioController"/> is null when ControlRadio was not granted:
    /// the keyer then refuses every key-up (armed TX is structurally impossible).</summary>
    public WsprTxService(
        IRadioController? radioController, IAudioPlaybackSink sink,
        Action<Ft8TxStatusDto> broadcast, DigitalTxArbiter arbiter, ILogger logger)
        : this(
            MakeKeyer(radioController, sink),
            block => sink.PlayOnAir(block.Span, DigitalTxStreamer.SampleRate),
            broadcast,
            DefaultRenderer,
            static () => DateTime.UtcNow,
            static (ms, ct) => Task.Delay(ms, ct),
            static () => Random.Shared.NextDouble(),
            () => sink.IsMoxOn,
            logger)
    {
        SetArbiter(arbiter);
    }

    /// <summary>Test constructor — injects keying / audio / clock / pacing / RNG as
    /// delegates so the scheduler is deterministic with no native dependency.</summary>
    internal WsprTxService(
        MoxKeyer keyer,
        Action<ReadOnlyMemory<float>> audioSink,
        Action<Ft8TxStatusDto> broadcast,
        Func<string, int, float[]?> renderer,
        Func<DateTime> clock,
        Func<int, CancellationToken, Task> delay,
        Func<double> random,
        Func<bool> moxAlreadyOn,
        ILogger logger)
    {
        _keyer = keyer;
        _audioSink = audioSink;
        _broadcast = broadcast;
        _renderer = renderer;
        _clock = clock;
        _delay = delay;
        _random = random;
        _moxAlreadyOn = moxAlreadyOn;
        _log = logger;
    }

    // See Ft8TxService.MakeKeyer — same host semantics (swallowed refusal, so the
    // post-key IsMoxOn re-read is the confirm-gate).
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

    /// <summary>True if the WSPR encode + synth path is usable on this platform.</summary>
    public bool NativeAvailable => WsprDecoder.IsAvailable;

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

    /// <summary>The ENABLE-TX master. The ONLY way the beacon turns on.</summary>
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
                drop = _weKeyedMox;
                _weKeyedMox = false;
            }
        }
        // Single-owner interlock: arming WSPR force-disarms a running FT8/FT4 keyer
        // (and vice-versa) so only one digital keyer holds the MOX source.
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
            drop = _weKeyedMox;
            _weKeyedMox = false;
        }
        if (drop) _keyer(false, out _);
        _log.LogInformation("wspr.tx force-disarmed: {Reason}", reason);
        BroadcastStatus();
    }

    /// <summary>Update the beacon content / cadence. Returns an error string on
    /// bad input, or null on success.</summary>
    public string? SetSettings(string call, string grid4, int dBm, int audioHz, double txPercent)
    {
        if (string.IsNullOrWhiteSpace(call)) return "call is required";
        if (string.IsNullOrWhiteSpace(grid4)) return "grid4 is required";
        lock (_lock)
        {
            _call = call.Trim().ToUpperInvariant();
            _grid4 = grid4.Trim().ToUpperInvariant();
            _dBm = SnapWsprDbm(dBm);   // snap to a canonical step the encoder accepts
            _audioHz = Math.Clamp(audioHz, 0, 2500);
            _txPercent = Math.Clamp(txPercent, 0.0, 1.0);
        }
        BroadcastStatus();
        return null;
    }

    /// <summary>Clamp to 0..60 dBm and snap to the nearest canonical WSPR power step
    /// so the beacon never carries a value WsprDecoder.Encode would reject (which
    /// would make it silently never transmit).</summary>
    internal static int SnapWsprDbm(int dBm)
    {
        int v = Math.Clamp(dBm, WsprDbmSteps[0], WsprDbmSteps[^1]);
        int best = WsprDbmSteps[0];
        foreach (int step in WsprDbmSteps)
            if (Math.Abs(step - v) < Math.Abs(best - v)) best = step;
        return best;
    }

    /// <summary>Panic / abort: disarm and drop MOX (if WE keyed it) mid-slot.</summary>
    public void Halt()
    {
        bool drop;
        lock (_lock) { _armed = false; drop = _weKeyedMox; _weKeyedMox = false; }
        if (drop) _keyer(false, out _);
        _log.LogInformation("wspr.tx halt");
        BroadcastStatus();
    }

    /// <summary>Current keyer status DTO (Mode = "WSPR", Slot = "").</summary>
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
            string? message = _call.Length == 0 ? null : BeaconMessage(_call, _grid4, _dBm);
            return new Ft8TxStatusDto(
                _armed, _transmitting, "WSPR", message, _audioHz, "", watchdog, _lastTxSlotMs,
                NativeAvailable, _skippedSlots);
        }
    }

    // ---- scheduler internals (also the test surface) -------------------------

    /// <summary>True iff the beacon should key this 120 s slot: armed, configured,
    /// watchdog not expired, and the probabilistic txPercent gate passes. Draws
    /// the RNG once (so a test can seed the sequence).</summary>
    internal bool ShouldKeyForSlot(DateTime nowUtc)
    {
        double pct;
        lock (_lock)
        {
            if (!_armed || _call.Length == 0) return false;
            if ((nowUtc - _armedSinceUtc).TotalMinutes >= WatchdogMinutes) return false;
            pct = _txPercent;
        }
        if (pct <= 0) return false;
        if (pct >= 1) return true;
        return _random() < pct;
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
                fired = true;
                drop = _weKeyedMox;
                _weKeyedMox = false;
            }
        }
        if (!fired) return;
        if (drop) _keyer(false, out _);
        _log.LogWarning("wspr.tx watchdog disarmed after {Min} min max armed duration", WatchdogMinutes);
        BroadcastStatus();
    }

    /// <summary>Key, render the beacon, stream it (1 s lead silence + ~110.6 s of
    /// audio) as 20 ms blocks, then unkey. Truncates cleanly if disarmed/halted;
    /// skips the slot if MOX is already keyed by another source.</summary>
    internal async Task TransmitBeaconAsync(CancellationToken ct)
    {
        string message;
        int audioHz;
        lock (_lock)
        {
            if (!_armed || _call.Length == 0) return;
            message = BeaconMessage(_call, _grid4, _dBm);
            audioHz = _audioHz;
        }

        // Render BEFORE claiming the transmit and keying MOX: if Halt/disarm/the
        // watchdog fires during render the re-check below bails without keying, so
        // MOX is never left up with no IQ feeding it.
        float[]? audio = _renderer(message, audioHz);
        if (audio is null || audio.Length == 0)
        {
            _log.LogWarning("wspr.tx render failed for '{Msg}'", message);
            return;
        }

        // Pre-key gate: never ride a key some other source holds (see Ft8TxService).
        if (_moxAlreadyOn())
        {
            int n;
            lock (_lock) n = ++_skippedSlots;
            _log.LogWarning(
                "wspr.tx slot skipped: MOX already keyed by another source (skipped #{N})", n);
            BroadcastStatus();
            return;
        }

        lock (_lock)
        {
            if (!_armed || _call.Length == 0) return; // halted/disarmed during render
            _transmitting = true;
        }

        try
        {
            if (!_keyer(true, out string? err))
            {
                _log.LogWarning("wspr.tx key-up refused: {Err}", err);
                return;
            }
            lock (_lock) _weKeyedMox = true;
            BroadcastStatus();

            int leadBlocks = Math.Max(1, WsprStartMs / DigitalTxStreamer.BlockMs);
            await DigitalTxStreamer.StreamAsync(audio, leadBlocks, _audioSink, _delay, StillArmed, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            // Release ONLY what we keyed (see Ft8TxService — a halt/disarm path
            // that already dropped MOX also cleared _weKeyedMox).
            bool drop;
            lock (_lock)
            {
                drop = _weKeyedMox;
                _weKeyedMox = false;
                _transmitting = false;
            }
            if (drop) _keyer(false, out _);
            BroadcastStatus();
        }
    }

    private bool StillArmed() { lock (_lock) return _armed; }

    private async Task ClockLoopAsync(CancellationToken ct)
    {
        long lastHandledSlot = long.MinValue;
        while (!ct.IsCancellationRequested)
        {
            DateTime now = _clock();
            EnforceWatchdog(now);

            double totalSec = (now - DateTime.UnixEpoch).TotalSeconds;
            long curSlot = (long)Math.Floor(totalSec / SlotSeconds);
            long nextSlot = curSlot + 1;
            double msToBoundary = (nextSlot * SlotSeconds - totalSec) * 1000.0;

            if (msToBoundary <= LeadMs && nextSlot != lastHandledSlot)
            {
                lastHandledSlot = nextSlot;
                if (!Transmitting && ShouldKeyForSlot(now))
                {
                    long slotStartMs = (long)(nextSlot * SlotSeconds * 1000.0);
                    lock (_lock) _lastTxSlotMs = slotStartMs;
                    _ = Task.Run(() => SafeTransmitAsync(ct), ct);
                }
            }

            double waitMs = msToBoundary - LeadMs;
            if (waitMs <= 0) waitMs = msToBoundary + 5;
            int sleep = (int)Math.Clamp(waitMs, 5, 250);
            try { await _delay(sleep, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task SafeTransmitAsync(CancellationToken ct)
    {
        try { await TransmitBeaconAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* shutdown / halt */ }
        catch (Exception ex) { _log.LogError(ex, "wspr.tx slot transmit failed"); }
    }

    private void BroadcastStatus()
    {
        try { _broadcast(Status()); }
        catch (Exception ex) { _log.LogDebug(ex, "wspr.tx status broadcast failed"); }
    }

    private static string BeaconMessage(string call, string grid4, int dBm) => $"{call} {grid4} {dBm}";

    private static float[]? DefaultRenderer(string message, int audioHz)
    {
        byte[]? symbols = WsprDecoder.Encode(message);
        if (symbols is null) return null;
        return WsprDecoder.Synth(symbols, audioHz, 48000);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}

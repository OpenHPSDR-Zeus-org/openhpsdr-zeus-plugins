// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus Digital plugin — extracted from Zeus core
// (Zeus.Server.Hosting/DigitalTxArbiter.cs). GPL-2.0-or-later.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// DigitalTxArbiter — a single-owner interlock for the digital-mode keyers.
// Ft8TxService and WsprTxService are BOTH always-running and BOTH key the radio
// through the SAME host MOX source (MoxSource.Plugin via
// IRadioController.SetMoxAsync). Nothing in the wire protocol distinguishes
// them, so if both were armed at once one keyer's slot-end key-off would drop
// MOX out from under the other (same-source release) and both would feed audio
// into the TX chain concurrently. The fix is a process invariant enforced here:
// only ONE of {FT8/FT4, WSPR} may be armed at a time. When a keyer arms, it
// Claim()s the arbiter, which force-disarms every sibling.

namespace Openhpsdr.Zeus.Plugins.Digital;

/// <summary>A digital keyer that the arbiter can force off when a sibling arms.</summary>
public interface IDigitalTxKeyer
{
    /// <summary>Disarm immediately (and drop MOX if mid-slot). MUST NOT call back
    /// into the arbiter — it is invoked from inside <see cref="DigitalTxArbiter.Claim"/>.</summary>
    void ForceDisarm(string reason);
}

/// <summary>Process-wide single-owner gate for the digital keyers (one per plugin instance).</summary>
public sealed class DigitalTxArbiter
{
    private readonly object _lock = new();
    private readonly List<IDigitalTxKeyer> _keyers = new();

    /// <summary>Register a keyer so a sibling's <see cref="Claim"/> can disarm it.</summary>
    public void Register(IDigitalTxKeyer keyer)
    {
        ArgumentNullException.ThrowIfNull(keyer);
        lock (_lock)
        {
            if (!_keyers.Contains(keyer)) _keyers.Add(keyer);
        }
    }

    /// <summary>Called when <paramref name="claimant"/> arms: force-disarms every
    /// other registered keyer so only the claimant holds the digital MOX source.</summary>
    public void Claim(IDigitalTxKeyer claimant)
    {
        ArgumentNullException.ThrowIfNull(claimant);
        IDigitalTxKeyer[] others;
        lock (_lock)
        {
            others = _keyers.Where(k => !ReferenceEquals(k, claimant)).ToArray();
        }
        foreach (var k in others) k.ForceDisarm("another digital keyer armed");
    }
}

// SPDX-License-Identifier: GPL-2.0-or-later

using Zeus.Server.Voyeur.Alerts;
using Xunit;

namespace Openhpsdr.Zeus.Plugins.Voyeur.Tests.Alerts;

public class AlertGateTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void First_fire_is_allowed()
    {
        var g = new AlertGate();
        Assert.True(g.TryFire("k", T0, cooldownSeconds: 300, globalCapPer10Min: 100));
    }

    [Fact]
    public void Cooldown_suppresses_repeat_within_window()
    {
        var g = new AlertGate();
        Assert.True(g.TryFire("k", T0, 300, 100));
        Assert.False(g.TryFire("k", T0.AddSeconds(299), 300, 100));   // still cooling
        Assert.True(g.TryFire("k", T0.AddSeconds(301), 300, 100));    // cooldown elapsed
    }

    [Fact]
    public void Cooldown_is_per_key()
    {
        var g = new AlertGate();
        Assert.True(g.TryFire("a", T0, 300, 100));
        Assert.True(g.TryFire("b", T0, 300, 100));   // different key, independent
    }

    [Fact]
    public void Zero_cooldown_allows_back_to_back()
    {
        var g = new AlertGate();
        Assert.True(g.TryFire("k", T0, 0, 100));
        Assert.True(g.TryFire("k", T0, 0, 100));
    }

    [Fact]
    public void Global_cap_suppresses_beyond_limit_across_keys()
    {
        var g = new AlertGate();
        Assert.True(g.TryFire("a", T0, 0, globalCapPer10Min: 2));
        Assert.True(g.TryFire("b", T0, 0, 2));
        Assert.False(g.TryFire("c", T0, 0, 2));   // 3rd in window → capped
    }

    [Fact]
    public void Global_window_slides_and_frees_capacity()
    {
        var g = new AlertGate();
        Assert.True(g.TryFire("a", T0, 0, 1));
        Assert.False(g.TryFire("b", T0.AddMinutes(5), 0, 1));  // within 10 min → capped
        Assert.True(g.TryFire("c", T0.AddMinutes(11), 0, 1));  // first fire aged out
    }

    [Fact]
    public void Zero_cap_disables_global_limit()
    {
        var g = new AlertGate();
        for (int i = 0; i < 50; i++)
            Assert.True(g.TryFire("k" + i, T0, 0, globalCapPer10Min: 0));
    }

    [Fact]
    public void Suppressed_fire_does_not_consume_global_budget()
    {
        var g = new AlertGate();
        Assert.True(g.TryFire("k", T0, cooldownSeconds: 300, globalCapPer10Min: 1));
        // Same key during cooldown is suppressed; it must not have consumed the
        // (already-full) global budget twice nor recorded a new fire.
        Assert.False(g.TryFire("k", T0.AddSeconds(1), 300, 1));
        // A different key is still capped by the one genuine fire.
        Assert.False(g.TryFire("other", T0.AddSeconds(1), 300, 1));
    }
}

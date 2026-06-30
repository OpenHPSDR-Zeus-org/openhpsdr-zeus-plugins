// SPDX-License-Identifier: GPL-2.0-or-later
//
// Tests for EngineSettings provider resolution, the engine selector's
// graceful-fallback policy, and the Whisper hotword-prompt composer.

using Xunit;
using Zeus.Server.Voyeur;

namespace Openhpsdr.Zeus.Plugins.Voyeur.Tests.Stt;

public sealed class EngineSelectionTests
{
    // ---- EngineSettings defaults + provider resolution ----

    [Fact]
    public void Default_is_whisper_cpu_no_gpu()
    {
        var s = EngineSettings.Default;
        Assert.Equal(SttEngineKind.Whisper, s.Engine);
        Assert.False(s.GpuEnabled);
        Assert.Equal("cpu", s.ResolveProvider());
    }

    [Fact]
    public void ResolveProvider_gpu_off_always_cpu_even_with_explicit_provider()
    {
        var s = new EngineSettings(SttEngineKind.Parakeet, Provider: "cuda", GpuEnabled: false);
        Assert.Equal("cpu", s.ResolveProvider());
    }

    [Fact]
    public void ResolveProvider_gpu_on_explicit_provider_wins()
    {
        var s = new EngineSettings(SttEngineKind.Parakeet, Provider: "cuda", GpuEnabled: true);
        Assert.Equal("cuda", s.ResolveProvider());
    }

    [Fact]
    public void ResolveProvider_gpu_on_blank_provider_picks_os_default()
    {
        var s = new EngineSettings(SttEngineKind.Parakeet, Provider: "", GpuEnabled: true);
        var expected = OperatingSystem.IsMacOS() ? "coreml"
            : OperatingSystem.IsWindows() ? "directml"
            : "cuda";
        Assert.Equal(expected, s.ResolveProvider());
    }

    [Fact]
    public void ResolveProvider_gpu_on_cpu_provider_treated_as_blank_picks_os_default()
    {
        var s = new EngineSettings(SttEngineKind.Parakeet, Provider: "cpu", GpuEnabled: true);
        Assert.NotEqual("cpu", s.ResolveProvider());
    }

    // ---- SttEngineSelector ----

    [Fact]
    public void Selector_picks_whisper_by_default()
    {
        var whisper = new FakeSttEngine(SttEngineKind.Whisper, available: true);
        var parakeet = new FakeSttEngine(SttEngineKind.Parakeet, available: true);
        var picked = SttEngineSelector.Pick(EngineSettings.Default, whisper, parakeet);
        Assert.Same(whisper, picked);
    }

    [Fact]
    public void Selector_picks_parakeet_when_selected_and_available()
    {
        var whisper = new FakeSttEngine(SttEngineKind.Whisper, available: true);
        var parakeet = new FakeSttEngine(SttEngineKind.Parakeet, available: true);
        var picked = SttEngineSelector.Pick(
            new EngineSettings(SttEngineKind.Parakeet), whisper, parakeet);
        Assert.Same(parakeet, picked);
    }

    [Fact]
    public void Selector_falls_back_to_whisper_when_parakeet_not_installed()
    {
        var whisper = new FakeSttEngine(SttEngineKind.Whisper, available: true);
        var parakeet = new FakeSttEngine(SttEngineKind.Parakeet, available: false);
        var picked = SttEngineSelector.Pick(
            new EngineSettings(SttEngineKind.Parakeet), whisper, parakeet);
        Assert.Same(whisper, picked);
    }

    [Fact]
    public void Selector_falls_back_to_parakeet_when_whisper_not_installed()
    {
        var whisper = new FakeSttEngine(SttEngineKind.Whisper, available: false);
        var parakeet = new FakeSttEngine(SttEngineKind.Parakeet, available: true);
        var picked = SttEngineSelector.Pick(EngineSettings.Default, whisper, parakeet);
        Assert.Same(parakeet, picked);
    }

    [Fact]
    public void Selector_returns_chosen_when_neither_installed()
    {
        var whisper = new FakeSttEngine(SttEngineKind.Whisper, available: false);
        var parakeet = new FakeSttEngine(SttEngineKind.Parakeet, available: false);
        var picked = SttEngineSelector.Pick(
            new EngineSettings(SttEngineKind.Parakeet), whisper, parakeet);
        Assert.Same(parakeet, picked); // chosen, so caller surfaces "Parakeet not installed"
    }

    // ---- Whisper prompt composer ----

    [Fact]
    public void ComposePrompt_empty_is_empty()
    {
        Assert.Equal("", WhisperSttEngine.ComposePrompt(Array.Empty<string>()));
    }

    [Fact]
    public void ComposePrompt_lists_hotwords()
    {
        var p = WhisperSttEngine.ComposePrompt(new[] { "KB2UKA", "W1ABC" });
        Assert.Contains("KB2UKA", p);
        Assert.Contains("W1ABC", p);
    }

    [Fact]
    public void ComposePrompt_dedupes_case_insensitively_preserving_order()
    {
        var p = WhisperSttEngine.ComposePrompt(new[] { "KB2UKA", "kb2uka", "  ", "W1ABC" });
        Assert.Equal("Likely callsigns and terms: KB2UKA, W1ABC.", p);
    }
}

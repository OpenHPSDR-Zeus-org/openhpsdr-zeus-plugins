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

namespace Zeus.Server.Voyeur;

/// <summary>
/// Operator-selectable STT engine configuration, persisted via the host's
/// <c>IPluginSettings</c> under the key <see cref="SettingsKey"/>. Lives in its
/// OWN record (per the module-isolation rule) so it can't collide with other
/// Voyeur settings groups (VAD, alerts, …).
///
/// LOCKED DEFAULT: <see cref="Engine"/> = Whisper. The safe, proven default is
/// never changed implicitly — the operator opts into Parakeet, and opts into GPU
/// on top of the mandatory CPU floor.
/// </summary>
/// <param name="Engine">Which backend transcribes overs. Default Whisper.</param>
/// <param name="Provider">Explicit sherpa execution provider override
/// (<c>cpu</c>/<c>cuda</c>/<c>coreml</c>/<c>directml</c>). When blank, the
/// provider is derived from <see cref="GpuEnabled"/> + the host OS. Whisper
/// ignores this.</param>
/// <param name="GpuEnabled">Opt-in GPU acceleration for Parakeet. Default false
/// (CPU floor — required for Raspberry Pi, which has no GPU provider).</param>
public sealed record EngineSettings(
    SttEngineKind Engine = SttEngineKind.Whisper,
    string Provider = "cpu",
    bool GpuEnabled = false)
{
    /// <summary>Settings store key (host <c>IPluginSettings.GetAsync/SetAsync</c>).</summary>
    public const string SettingsKey = "stt.engine";

    /// <summary>Locked default — Whisper, CPU, no GPU.</summary>
    public static EngineSettings Default { get; } = new();

    /// <summary>
    /// Resolve the concrete sherpa-onnx <c>--provider</c> string for the
    /// Parakeet engine. Rules:
    ///   • GPU off  → always <c>cpu</c> (the mandatory floor).
    ///   • GPU on + an explicit non-cpu <see cref="Provider"/> → that value.
    ///   • GPU on + blank/cpu <see cref="Provider"/> → best provider for the OS
    ///     (macOS → coreml, Windows → directml, else cuda). These are best-effort
    ///     accelerators; sherpa-onnx itself falls back to cpu at runtime if the
    ///     execution provider isn't available in the installed build.
    /// There is intentionally NO vulkan — sherpa-onnx has no vulkan provider.
    /// </summary>
    public string ResolveProvider()
    {
        if (!GpuEnabled) return "cpu";

        var p = (Provider ?? "").Trim().ToLowerInvariant();
        if (p.Length > 0 && p != "cpu") return p;

        if (OperatingSystem.IsMacOS()) return "coreml";
        if (OperatingSystem.IsWindows()) return "directml";
        return "cuda";
    }
}

/// <summary>
/// Pure engine-selection policy. Picks the active <see cref="ISttEngine"/> from
/// the operator's <see cref="EngineSettings"/>, degrading gracefully: if the
/// chosen engine isn't installed, fall back to the other engine when IT is
/// available, otherwise return the chosen engine anyway (so the caller still
/// gets a soft "not installed" result rather than a null). Whisper is the
/// preferred fallback because it's the locked default.
/// </summary>
public static class SttEngineSelector
{
    public static ISttEngine Pick(EngineSettings settings, ISttEngine whisper, ISttEngine parakeet)
    {
        var chosen = settings.Engine == SttEngineKind.Parakeet ? parakeet : whisper;
        if (chosen.Available) return chosen;

        // Chosen engine not installed → fall back to the other if it's ready.
        var other = ReferenceEquals(chosen, whisper) ? parakeet : whisper;
        if (other.Available) return other;

        // Neither installed — return the chosen one so the caller surfaces a
        // coherent "<engine> not installed" SttResult.
        return chosen;
    }
}

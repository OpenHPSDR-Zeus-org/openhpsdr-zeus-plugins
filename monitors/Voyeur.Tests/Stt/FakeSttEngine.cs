// SPDX-License-Identifier: GPL-2.0-or-later
//
// Test double for ISttEngine — returns canned results, spawns NO process.
// This is the "fake runner" the STT tests inject in place of a real engine.

using Zeus.Server.Voyeur;

namespace Openhpsdr.Zeus.Plugins.Voyeur.Tests.Stt;

internal sealed class FakeSttEngine : ISttEngine
{
    private readonly Func<string, SttOptions, SttResult> _respond;

    public FakeSttEngine(SttEngineKind kind, bool available, Func<string, SttOptions, SttResult>? respond = null)
    {
        Kind = kind;
        Available = available;
        _respond = respond ?? ((_, _) => SttResult.NoSpeech());
    }

    public SttEngineKind Kind { get; }
    public bool Available { get; }
    public int Calls { get; private set; }

    public Task<SttResult> TranscribeAsync(string wavPath16k, SttOptions opt, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(
            Available ? _respond(wavPath16k, opt) : SttResult.NotInstalled(Kind.ToString()));
    }
}

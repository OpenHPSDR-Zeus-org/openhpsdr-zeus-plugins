# WAV Recorder · Tape Deck (`com.kb2uka.recorder`)

The RX/TX tape deck, extracted from Zeus core into an installable plugin. It
records demodulated RX audio or pre-processing TX mic audio to float32 WAVs
under `<Downloads>/Zeus Recordings`, browses / renames / moves / deletes them in
folders, and plays a recording back **locally** (mixed into the operator's
monitor) or **over the air** (injected into the TX chain).

License: **GPL-2.0-or-later** — an in-process managed plugin, exactly like every
other plugin in this repo. The DSP/file logic is the project owner's own GPL
code moved out of `Zeus.Server.Hosting/Wav/*`; the GPL headers are preserved.

## TX safety (read before bench testing)

Over-air playback is the one sharp edge. It self-keys MOX through the SDK and
injects realtime audio into the live TX chain, so it drives a **real HF PA**.
The ported state machine preserves every in-core RF-safety invariant:

- **Never auto-keys.** A key is raised only on an explicit operator *TX*
  (over-air) playback action.
- **Key only when unkeyed; confirm the key took.** The host
  `RadioController.SetMoxAsync` swallows the `TxService.TrySetMox` refusal bool
  (returns a completed task either way), so after `await SetMoxAsync(true)` the
  plugin **re-reads `IAudioPlaybackSink.IsMoxOn`** to confirm. A refused key
  (band guard / not-connected race / SWR-latched / interlock) aborts playback
  *before* the pump launches and `weKeyed` stays false — no stranded key.
- **Rides an operator-held key** (does not key, does not drop it at the end).
- **Drops only what it raised**, in the finally path.
- **Stops the instant the operator unkeys mid-clip** (per-block `IsMoxOn` gate).
- **Crash-unwind** releases a key we raised if setup throws after the raise.
- **PureSignal is never touched** anywhere in the path.
- If `ControlRadio` is **not** granted, `RadioController` is null → it degrades
  to *ride-an-existing-operator-key only*, and never throws / never keys.

> **Bench-gated on the ANAN-G2 only.** This change is untested on non-G2 boards.
> The G2 soak set MUST include the **refused-key** case (Air-play while
> band-guarded / not-connected / SWR-latched → confirm no key is committed and
> the operator sees the error) in addition to clean T/R, ride-the-operator-key,
> operator-unkey-mid-clip, and crash-unwind.

## Layout

| Path | What it is |
|---|---|
| `RecorderPlugin.cs` | Entry: `IZeusPlugin` + `IBackendPlugin` + `IRxAudioTapPlugin` + `ITxAudioTapPlugin`. Owns lifetime, the 15 endpoints, capture taps, and settings. |
| `WavRecorderService.cs` | Orchestration (record/play state machine). Keying rewired from core `TxService` to the SDK with the post-key `IsMoxOn` confirm-gate. |
| `WavLibrary.cs` / `WavMeter.cs` | Verbatim from core (no Zeus.* deps). |
| `WavFile.cs` / `WavWriter.cs` | Ported; the core `DspPipelineService.SanitizeAudioSample` call is repointed to the vendored `AudioSanitize`. |
| `AudioSanitize.cs` | Vendored clamp/zero-non-finite helper (same pattern as Voyeur vendoring its own `WavWriter`). |
| `ui/` | Vite lib-mode UI. `WavRecorderPanel.tsx` / `WavRecorderPanel.css` / `DirectoryPickerModal.tsx` / `wavRecorder.format.ts` are **faithful verbatim ports** — the only change is transport (`fetch('/api/wav/*')` → host `callBackend`). `useDialogFocusTrap.ts` is a vendored copy of the shared host hook. |
| `Recorder.Tests/` | xUnit: ported library/meter/file tests + the stub-driven over-air keying assertions (no socket/UDP/TCP I/O). |

The single recordings-root preference persists via `IPluginContext.Settings`
(no LiteDB — sidesteps the Windows shared-lock crash, #682).

## Build prerequisite — sibling `OPENHPSDR-Zeus`

`Recorder.csproj` and `Recorder.Tests` reference `Zeus.Plugins.Contracts` via
`..\..\..\OPENHPSDR-Zeus\Zeus.Plugins.Contracts`. Clone (or symlink) the Zeus
repo as a sibling of this plugins repo first:

```bash
# from the parent dir that holds openhpsdr-zeus-plugins/
ln -s /path/to/openhpsdr-zeus OPENHPSDR-Zeus   # or: git clone … OPENHPSDR-Zeus
```

## Build, test, package

```bash
cd monitors/Recorder

# UI module → ui/wav-recorder-panel.es.js (react/react-dom are host externals)
npm install
npm run typecheck
npm run test          # vitest — pure formatting helpers, no network
npm run build

# Backend plugin assembly
dotnet build Recorder.csproj -c Release
dotnet test  Recorder.Tests    # 41 tests; over-air keying via injected SDK doubles

# Package the release zip (plugin.json + dll + ui/*.es.js at the zip root)
#   then publish as the GitHub Release asset at tag recorder-v1.0.0 and refresh
#   the registry.json sha256 with `sha256sum` of the PUBLISHED asset.
```

## Releasing / registry

`registry.json` (repo root) carries the catalog entry. `validate.yml`
(`checksums` + `manifest` jobs) **downloads the release URL**, verifies the
sha256, unzips `plugin.json`, schema-validates it, and asserts the embedded
`id`/`version` match the registry. Therefore:

1. Build + zip as above.
2. Publish the zip as the Release asset at `recorder-v1.0.0`.
3. Set `registry.json` → `com.kb2uka.recorder` → `sha256` to the **published
   asset's** `sha256sum`. (Zip archives embed timestamps and are not byte-
   reproducible, so the placeholder sha committed in the draft MUST be refreshed
   against the actual published asset, or the `checksums` job fails.)

### Branch-target / CI drift — confirm with Doug before opening

`CONTRIBUTING.md` says branch off / PR to `develop`, but `validate.yml` triggers
on `main` and this work was cut from `main` (branch `wav-recorder-plugin`).
Confirm the correct base. Until the `recorder-v1.0.0` release asset exists, the
`checksums` and `manifest` validate jobs will fail on the download step — expected
for a draft.

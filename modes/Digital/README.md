# Zeus Digital · FT8/FT4 (`com.kb2uka.digital`)

The native FT8/FT4/WSPR digital-mode engine, extracted from Zeus core into an
installable **backend-only** plugin. It owns decode (multi-pass, over the
vendored [kgoba/ft8_lib](https://github.com/kgoba/ft8_lib)), TX encode +
auto-sequencing, PSK Reporter / WSPRnet spotting, and the WSJT-X-compatible
UDP live-decode egress. The **pop-out UI shell stays in Zeus core** — there is
deliberately no `ui` block in `plugin.json`, so nothing appears in the
*+ Add Panel* list; installing the plugin lights up the FT8/FT4 buttons in the
core mode menu instead. WSPR is staged in the engine but stays greyed in the
UI (unfinished, untouched policy).

License: **GPL-2.0-or-later** for the first-party code; the vendored decoder
cores are MIT (ft8_lib) and GPL-3 (wsprd) — see
[`ATTRIBUTIONS.md`](ATTRIBUTIONS.md) for the full provenance chain.

## TX safety

The plugin drives a **real HF PA** during FT8/FT4 transmit. It never
auto-keys: TX must be armed by the operator, every session; the decode
watchdog, disable-after-73, and halt paths all drop any key the plugin raised
(and only what it raised). PureSignal is never touched anywhere in this
plugin.

## Layout

| Path | What it is |
|---|---|
| `DigitalPlugin.cs` | Entry: `IZeusPlugin`. Owns lifetime, and (as the port lands) the plugin endpoints, RX audio tap, and TX keyer. |
| `Dsp/` | The extracted `Zeus.Dsp.Ft8` managed layer, verbatim apart from the namespace: decoder, WSPR decoder, TX synth, 48 kHz→12 kHz resampler, slot accumulator, message sender-parse, country resolver, and `Ft8NativeLoader` (per-assembly `SetDllImportResolver` probing `runtimes/<rid>/native` next to this assembly). |
| `runtimes/<rid>/native/` | Prebuilt `zeus_ft8` (all 5 RIDs) and `zeus_wspr` (POSIX RIDs only — the Windows WSPR decode path has never shipped, and WSPR is greyed in the UI regardless). Ships inside the plugin zip. |
| `native/ft8/`, `native/wspr/` | Full native source trees (shim + CMake + self-tests + vendored ft8_lib / wsprd), moved from Zeus core so this repo is licence-self-contained and the binaries are rebuildable from here. |
| `Digital.Tests/` | xUnit tests (ported with the service code; no socket I/O). |

## Native binaries — provenance and rebuild

The committed `runtimes/**` binaries are byte-identical copies of what Zeus
core shipped under `Zeus.Dsp/runtimes/**` — historically built by the Zeus
repo's `.github/workflows/build-native-libs.yml` (Windows: MSVC `-T ClangCL`
for the C99 VLAs in ft8_lib, x64 + ARM64; Linux: gcc x64 + aarch64 cross;
macOS arm64 dylibs built on Apple Silicon). To rebuild from this repo:

```bash
# FT8/FT4 core → libzeus_ft8.{dylib,so} / zeus_ft8.dll
cmake -S native/ft8 -B build-ft8 -DCMAKE_BUILD_TYPE=Release
cmake --build build-ft8 --parallel
( cd build-ft8 && ctest --output-on-failure )   # decodes the bundled WAV corpus

# WSPR core → libzeus_wspr.{dylib,so} (POSIX; encoder-only on Windows)
cmake -S native/wspr -B build-wspr -DCMAKE_BUILD_TYPE=Release
cmake --build build-wspr --parallel
( cd build-wspr && ctest --output-on-failure )  # encode self-test + decode round-trip
```

Stage the artifacts under `runtimes/<rid>/native/` (rids: `osx-arm64`,
`linux-x64`, `linux-arm64`, `win-x64`, `win-arm64`). Per-platform notes,
decode-quality numbers, and the re-vendoring recipes live in
[`native/ft8/README.md`](native/ft8/README.md) and
[`native/wspr/README.md`](native/wspr/README.md).

## Build prerequisite — sibling `OPENHPSDR-Zeus`

`Digital.csproj` references `Zeus.Plugins.Contracts` via
`..\..\..\OPENHPSDR-Zeus\Zeus.Plugins.Contracts`. Clone (or symlink) the Zeus
repo as a sibling of this plugins repo first:

```bash
# from the parent dir that holds openhpsdr-zeus-plugins/
ln -s /path/to/openhpsdr-zeus OPENHPSDR-Zeus   # or: git clone … OPENHPSDR-Zeus
```

## Build, test, package

```bash
cd modes/Digital

dotnet build Digital.csproj -c Release
dotnet test  Digital.Tests     # no socket/UDP/TCP I/O in unit tests

# Package the release zip — plugin.json + Zeus.Digital.Plugin.dll + runtimes/
# at the ZIP ROOT (a wrapping folder makes the installer 400 silently):
cd bin/Release/net10.0
zip -r ../../../com.kb2uka.digital-1.0.0.zip plugin.json Zeus.Digital.Plugin.dll runtimes
```

One fat zip carries every platform's natives; `Ft8NativeLoader` picks the
current RID's `runtimes/<rid>/native/` at runtime, so the registry entry uses
`platforms: ["any"]`. Publish the zip as the GitHub Release asset at tag
`digital-v1.0.0`, then set the `registry.json` `sha256` from the **published**
asset (zip archives embed timestamps and are not byte-reproducible).

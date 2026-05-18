# 10-Band Parametric EQ — Changelog

## v0.2.0 — Phase 3 lead plugin (2026-05-18)

The audio-chain-v2 plumbing release for EQ. Establishes the I/O gain
widget pattern that all 5 Phase 3 plugins (this, Comp, Exciter, Bass,
Reverb) will share.

### New
- **Input + Output gain stages.** Two new dB knobs (−24 dB … +24 dB,
  default 0 dB) flanking the EQ curve. Each knob sits above a
  vertical IN/OUT meter showing post-gain level. Defaults to 0 dB
  so a v0.1.0 chain upgrades with bit-identical behaviour.
- **Live FFT spectrum analyser.** 2048-point Hann-windowed FFT
  computed every 1024 samples (50 % overlap) on both the pre-cascade
  input AND the post-cascade output. Server log-bins to 256 bins
  over 20 Hz – 20 kHz; UI polls at 30 Hz with client-side peak-hold
  + 12 dB/sec decay so the trace doesn't flicker on transients.
  The visualisation overlays both spectra behind the EQ curve: the
  gap between input fill and output outline shows the operator
  exactly what the EQ is doing to their voice.
- New REST endpoint: `GET /api/plugins/com.openhpsdr.zeus.samples.eq/spectrum`
  returning `{ inputBinsDb: float[], outputBinsDb: float[], binCount,
  fMinHz, fMaxHz, dbFloor }`.

### Behaviour
- **Bypass still applies I/O gain.** When the cascade is bypassed,
  Input + Output gain are still applied so the plugin can be used
  as a pure gain stage. Operators using bypass A/B will hear the
  gain-staging effect of the plugin independent of the EQ shape.
- **Realtime contract preserved.** No allocations, no locks, no IO
  inside Process. The FFT runs on the audio thread but only every
  1024 samples (~21 ms blocks) and costs ~50 µs on M-series silicon
  — well under 1 % CPU.

### Settings migration
- v0.1.0 saved settings (no `input_gain_db` / `output_gain_db` keys)
  load with both gains at 0 dB. No operator action required.

### Wire-format additions
- `EqParamsDto` gains `inputGainDb`, `outputGainDb` (both optional
  in POST `/params` — only changed values need to be sent).

## v0.1.0 — initial release

- 10 cascaded biquad peaking filters per the RBJ Audio EQ Cookbook.
- Per-band frequency / gain / Q controls + master bypass.
- Combined transfer-function curve visualisation.
- REST: `GET /params`, `POST /params`, `GET /meters`.

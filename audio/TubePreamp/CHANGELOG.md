# Tube Preamp — changelog

## 0.1.0 — 2026-05-18

First cut of the triode-style tube preamp / soft-saturator for the Zeus voice
audio chain.

### Algorithm

- Asymmetric `tanh(drive·x + bias) − tanh(bias)` saturator. The bias term
  shifts the operating point along the tanh S-curve, so positive bias clips
  the upper half-cycle more aggressively than the lower — that is what gives
  a vacuum-tube preamp its characteristic 2nd-harmonic richness. Subtracting
  `tanh(bias)` removes the static DC pedestal so the block doesn't leak DC
  into the rest of the chain.
- 1-pole DC-blocker at ~5 Hz cleans up any residual DC from the asymmetric
  clipping.
- 1-pole low-pass "Warmth" filter (4-20 kHz corner) rolls off the brightest
  harmonics — sweepable between vintage smoothness and modern presence.
- Output trim (-24..+12 dB) compensates for the saturation changing the
  perceived loudness.

### Controls

- **Drive** (0..36 dB, default 12 dB) — input gain into the saturator
- **Bias** (-1..+1, default +0.30) — asymmetry / 2nd-harmonic emphasis
- **Warmth** (4..20 kHz, default 12 kHz) — post-saturation LP corner
- **Output** (-24..+12 dB, default -6 dB) — output trim
- **Bypass** — chain-plugin convention; clean pass-through, filter state
  preserved across re-engage

### Realtime

- All-managed C#, no native dependency.
- `Process()` does not allocate, lock, or perform IO.
- Params snapshot once per block — knob moves don't shimmer the curve.

### UI

- Live SVG transfer-curve viz draws the bias-shifted tanh against the unity
  reference. As you move the Bias knob, the curve's asymmetry is directly
  visible.
- Four rotary knobs (Drive / Bias / Warmth / Output) in a 2×2 grid.
- Brass-plate header with IN / DRIVE / OUT peak-dBFS readout and the
  shared chain-plugin bypass switch (red when bypassed, audio-suite
  convention).

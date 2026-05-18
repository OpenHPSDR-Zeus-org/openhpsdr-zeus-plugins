# Noise Gate — changelog

## 0.1.0 — 2026-05-18

First cut of the voice noise gate for the Zeus audio chain.

### Algorithm

- **Peak-envelope detector** with instantaneous attack and a fixed ~10 ms
  exponential decay. Tracks voice transients without per-cycle chatter.
- **State machine with hysteresis.** Opens at the user-set Threshold;
  closes at Threshold − 3 dB. The 3 dB gap is the single most important
  detail in a usable voice gate — without it the gate chatters when
  speech hovers right at the threshold.
- **Hold timer.** Once open, stays open for `Hold` ms after the envelope
  drops below the close threshold. Prevents the gate from clamping shut
  on natural silences inside a word.
- **Asymmetric gain slew.** Separate one-pole attack and release time
  constants drive the applied gain toward its target. Attack typ. 1-10 ms
  (fast enough to not chop consonants); release typ. 50-300 ms (slow
  release sounds natural).
- **Output gain trim** applied after the gate for level-matching the
  next chain block.

### Controls

- **Threshold** (-80..0 dBFS, default -35) — open threshold; close is 3 dB below
- **Attack** (0.1..50 ms, default 2 ms, log) — how fast the gate opens
- **Hold** (0..500 ms, default 80 ms) — sustain after signal drops below close
- **Release** (5..1000 ms, default 150 ms, log) — how slowly the gate closes
- **Range** (-80..0 dB, default -60 dB) — attenuation depth when closed
- **Output** (-24..+12 dB, default 0) — post-gate gain trim
- **Bypass** — chain-plugin convention; clean pass-through, state preserved

### Realtime

- All-managed C#, no native dependency.
- `Process()` does not allocate, lock, or perform IO.
- Params snapshot per block; filter + state-machine state preserved across
  bypass re-engage so it doesn't pop.

### UI

- Vertical IN and OUT peak meters (LED-style gradient, peak-hold tick that
  decays at ~40 dB/s) flank the centerpiece — same as the audio-chain
  visual direction calls for.
- Threshold rail: input-peak needle moves along a -60..0 dBFS scale; the
  open and close thresholds are drawn as vertical markers with the
  hysteresis band shaded in accent blue.
- State pill: glowing **OPEN** (accent blue) / **HOLD** (power yellow) /
  **CLOSED** (dim) — tells the operator at a glance which side of the
  threshold the gate is on.
- Horizontal GR strip fills from the right as the gate closes.
- Brass-plate header with IN / GR / OUT readouts and the shared
  chain-plugin bypass switch (red-active per the audio-chain bypass
  convention).

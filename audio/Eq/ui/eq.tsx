// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus voice audio chain v2 — 10-Band Parametric EQ UI module.
// Copyright (C) 2025-2026 KB2UKA and contributors.
//
// v0.2.0 layout:
//
//   ┌─────────────────────────────────────────────────────────────────┐
//   │  brass-plate header: title │ reset · bypass                     │
//   ├──────────┬───────────────────────────────────────────┬──────────┤
//   │  IN gain │  combined EQ curve over live spectrum    │ OUT gain │
//   │  ───┴─── │  (input spectrum fill, output outline)   │ ───┴───  │
//   │   IN m   │                                          │  OUT m   │
//   ├──────────┴───────────────────────────────────────────┴──────────┤
//   │  10 band columns: freq · gain · Q knobs                         │
//   └─────────────────────────────────────────────────────────────────┘
//
// Tokens only (no raw hex). Live spectrum polls /spectrum at 30 Hz,
// applies peak-hold + 12 dB/sec falloff client-side so the trace
// doesn't flicker.

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';

// ---------------------------------------------------------------
interface ZeusPluginApi {
    registerPanel(spec: { id: string; component: React.ComponentType }): void;
    callBackend(method: string, path: string, body?: unknown): Promise<Response>;
}

// ---------------------------------------------------------------
interface EqBand {
    freqHz: number;
    gainDb: number;
    q: number;
}

interface EqParams {
    bands: EqBand[];
    bypass: boolean;
    inputGainDb: number;
    outputGainDb: number;
}

interface EqMeters {
    inputPeakDb: number;
    outputPeakDb: number;
}

interface EqSpectrum {
    inputBinsDb: number[];
    outputBinsDb: number[];
    binCount: number;
    fMinHz: number;
    fMaxHz: number;
    dbFloor: number;
}

const BAND_COUNT = 10;
const SPECTRUM_BIN_COUNT = 256;

const DEFAULT_FREQS = [80, 150, 300, 500, 800, 1200, 1800, 2700, 4000, 6000];

const DEFAULT_PARAMS: EqParams = {
    bands: DEFAULT_FREQS.map((f) => ({ freqHz: f, gainDb: 0, q: 1.0 })),
    bypass: false,
    inputGainDb: 0,
    outputGainDb: 0,
};

const DEFAULT_METERS: EqMeters = { inputPeakDb: -200, outputPeakDb: -200 };

const SPECTRUM_DB_FLOOR = -120;
const SPECTRUM_DB_CEIL = 0;

// Spectrum poll cadence + decay. 33 ms = ~30 Hz; per-tick falloff of
// 12 dB/sec works out to ~0.4 dB per tick so a peak takes ~3 seconds
// to fall from 0 to -120 dB — slow enough that the eye reads it as
// smooth, fast enough that level changes feel responsive.
const SPECTRUM_POLL_MS = 33;
const SPECTRUM_FALLOFF_DB_PER_SEC = 12;
const SPECTRUM_FALLOFF_PER_TICK = SPECTRUM_FALLOFF_DB_PER_SEC * (SPECTRUM_POLL_MS / 1000);

// ---------------------------------------------------------------
// Knob — SVG rotary with drag-to-rotate. Shared shape across all the
// Zeus chain plugins.
// ---------------------------------------------------------------
interface KnobProps {
    label: string;
    value: number;
    min: number;
    max: number;
    defaultValue: number;
    unit: string;
    formatValue?: (v: number) => string;
    logScale?: boolean;
    onChange: (v: number) => void;
    size?: number;
}

function Knob({ label, value, min, max, defaultValue, unit, formatValue, logScale, onChange, size = 44 }: KnobProps) {
    const dragStartRef = useRef<{ y: number; startValue: number; fine: boolean } | null>(null);
    const knobRef = useRef<SVGSVGElement | null>(null);

    const valueToNorm = useCallback((v: number): number => {
        if (logScale) {
            const lnLo = Math.log(min);
            const lnHi = Math.log(max);
            return (Math.log(Math.max(min, v)) - lnLo) / (lnHi - lnLo);
        }
        return (v - min) / (max - min);
    }, [logScale, min, max]);

    const normToValue = useCallback((n: number): number => {
        const clamped = Math.max(0, Math.min(1, n));
        if (logScale) {
            return Math.exp(Math.log(min) + clamped * (Math.log(max) - Math.log(min)));
        }
        return min + clamped * (max - min);
    }, [logScale, min, max]);

    const norm = valueToNorm(value);
    const angleDeg = -135 + norm * 270;

    const onPointerDown = useCallback((e: React.PointerEvent<SVGSVGElement>) => {
        e.preventDefault();
        knobRef.current?.setPointerCapture(e.pointerId);
        dragStartRef.current = { y: e.clientY, startValue: value, fine: e.shiftKey };
    }, [value]);

    const onPointerMove = useCallback((e: React.PointerEvent<SVGSVGElement>) => {
        if (!dragStartRef.current) return;
        const dy = dragStartRef.current.y - e.clientY;
        const sensitivity = dragStartRef.current.fine ? 0.0005 : 0.005;
        const startNorm = valueToNorm(dragStartRef.current.startValue);
        const next = normToValue(startNorm + dy * sensitivity);
        onChange(next);
    }, [valueToNorm, normToValue, onChange]);

    const onPointerUp = useCallback((e: React.PointerEvent<SVGSVGElement>) => {
        knobRef.current?.releasePointerCapture(e.pointerId);
        dragStartRef.current = null;
    }, []);

    const onDoubleClick = useCallback(() => onChange(defaultValue), [defaultValue, onChange]);

    const onWheel = useCallback((e: React.WheelEvent<SVGSVGElement>) => {
        e.preventDefault();
        const fine = e.shiftKey;
        const step = fine ? 0.001 : 0.01;
        const dir = e.deltaY > 0 ? -1 : 1;
        const startNorm = valueToNorm(value);
        const next = normToValue(startNorm + dir * step);
        onChange(next);
    }, [value, valueToNorm, normToValue, onChange]);

    const display = formatValue ? formatValue(value) : value.toFixed(2);

    const ticks: number[] = [];
    for (let i = 0; i <= 9; i++) {
        const t = i / 9;
        ticks.push(-135 + t * 270);
    }

    const half = size / 2;
    const R = half - 6;
    const TICK_OUTER = R + 2;
    const TICK_INNER = R + 4;
    const POINTER_LEN = R - 5;

    return (
        <div style={{
            display: 'flex',
            flexDirection: 'column',
            alignItems: 'center',
            gap: 2,
            minWidth: size + 8,
            userSelect: 'none',
        }}>
            <span style={{
                fontFamily: 'var(--font-sans, Inter, system-ui, sans-serif)',
                fontSize: 9,
                letterSpacing: 0.6,
                textTransform: 'uppercase',
                color: 'var(--fg-2, #b8bcc3)',
                fontWeight: 500,
            }}>{label}</span>

            <svg
                ref={knobRef}
                viewBox={`-${half} -${half} ${size} ${size}`}
                width={size}
                height={size}
                onPointerDown={onPointerDown}
                onPointerMove={onPointerMove}
                onPointerUp={onPointerUp}
                onPointerCancel={onPointerUp}
                onDoubleClick={onDoubleClick}
                onWheel={onWheel}
                style={{
                    cursor: 'ns-resize',
                    touchAction: 'none',
                    filter: 'drop-shadow(0 1px 2px rgba(0,0,0,0.4))',
                }}
                aria-label={`${label} ${display} ${unit}`}
                role="slider"
                aria-valuenow={value}
                aria-valuemin={min}
                aria-valuemax={max}
            >
                {ticks.map((deg, i) => {
                    const rad = (deg - 90) * Math.PI / 180;
                    const x1 = TICK_INNER * Math.cos(rad);
                    const y1 = TICK_INNER * Math.sin(rad);
                    const x2 = TICK_OUTER * Math.cos(rad);
                    const y2 = TICK_OUTER * Math.sin(rad);
                    const isActive = deg <= angleDeg + 0.5;
                    return (
                        <line
                            key={i}
                            x1={x1} y1={y1} x2={x2} y2={y2}
                            stroke={isActive ? 'var(--accent, #4a9eff)' : 'var(--line-1, #2a2c30)'}
                            strokeWidth={1.2}
                            strokeLinecap="round"
                        />
                    );
                })}

                <defs>
                    <radialGradient id={`dial-eq-${label}`} cx="0.35" cy="0.35" r="0.75">
                        <stop offset="0%" stopColor="var(--bg-2, #1f2226)" />
                        <stop offset="100%" stopColor="var(--bg-0, #0e1014)" />
                    </radialGradient>
                </defs>
                <circle cx={0} cy={0} r={R} fill={`url(#dial-eq-${label})`} stroke="var(--line-2, #3a3d42)" strokeWidth={1} />

                {(() => {
                    const rad = (angleDeg - 90) * Math.PI / 180;
                    const x = POINTER_LEN * Math.cos(rad);
                    const y = POINTER_LEN * Math.sin(rad);
                    return (
                        <>
                            <line x1={0} y1={0} x2={x} y2={y} stroke="var(--accent, #4a9eff)" strokeWidth={2} strokeLinecap="round" />
                            <circle cx={x} cy={y} r={2} fill="var(--accent, #4a9eff)" />
                        </>
                    );
                })()}
            </svg>

            <span style={{
                fontFamily: 'var(--font-mono, JetBrains Mono, ui-monospace, monospace)',
                fontSize: 9,
                color: 'var(--fg-0, #e8eaed)',
                fontVariantNumeric: 'tabular-nums',
                lineHeight: 1,
            }}>{display}{unit ? ` ${unit}` : ''}</span>
        </div>
    );
}

// ---------------------------------------------------------------
// VerticalMeter — slim vertical bar, dB scale -60 to 0. Gradient
// from --accent (low) → --power (mid) → --tx (high). dB readout
// below. Used for IN and OUT in the flanking columns.
// ---------------------------------------------------------------
function VerticalMeter({ levelDb, label, height = 110 }: { levelDb: number; label: string; height?: number }) {
    const dbMin = -60;
    const dbMax = 0;
    const norm = Math.max(0, Math.min(1, (levelDb - dbMin) / (dbMax - dbMin)));
    const filledHeight = height * norm;
    const display = levelDb > -150 ? levelDb.toFixed(0) : '—';
    return (
        <div style={{
            display: 'flex',
            flexDirection: 'column',
            alignItems: 'center',
            gap: 4,
        }}>
            <div style={{
                position: 'relative',
                width: 12,
                height,
                background: 'var(--bg-0, #0e1014)',
                border: '1px solid var(--line-1, #2a2c30)',
                borderRadius: 2,
                overflow: 'hidden',
            }}>
                <div style={{
                    position: 'absolute',
                    left: 0,
                    right: 0,
                    bottom: 0,
                    height: filledHeight,
                    background: 'linear-gradient(0deg, var(--accent, #4a9eff) 0%, var(--accent, #4a9eff) 50%, var(--power, #ffc93a) 75%, var(--tx, #e63a2b) 100%)',
                    transition: 'height 50ms linear',
                }} />
            </div>
            <span style={{
                fontFamily: 'var(--font-mono, JetBrains Mono, ui-monospace, monospace)',
                fontSize: 9,
                color: 'var(--fg-1, #d6d8dc)',
                fontVariantNumeric: 'tabular-nums',
                lineHeight: 1,
            }}>{display}</span>
            <span style={{
                fontFamily: 'var(--font-sans, Inter, system-ui, sans-serif)',
                fontSize: 8,
                letterSpacing: 0.6,
                textTransform: 'uppercase',
                color: 'var(--fg-3, #5a5e66)',
                fontWeight: 500,
            }}>{label}</span>
        </div>
    );
}

// ---------------------------------------------------------------
// EQ curve viz — combined magnitude of all 10 cascaded biquad
// peaking filters, log-frequency on X (20 Hz to 20 kHz) vs gain dB
// on Y (-24 to +24). v0.2.0 overlays the live input + output
// spectrum behind the EQ curve so the operator can SEE what the
// EQ is doing to their voice.
// ---------------------------------------------------------------

const SAMPLE_RATE = 48000;
const F_MIN = 20;
const F_MAX = 20000;

function biquadMagnitude(f: number, band: EqBand): number {
    const A = Math.pow(10, band.gainDb / 40);
    const w0 = 2 * Math.PI * band.freqHz / SAMPLE_RATE;
    const cosW0 = Math.cos(w0);
    const alpha = Math.sin(w0) / (2 * Math.max(0.1, band.q));

    const b0 = 1 + alpha * A;
    const b1 = -2 * cosW0;
    const b2 = 1 - alpha * A;
    const a0 = 1 + alpha / A;
    const a1 = -2 * cosW0;
    const a2 = 1 - alpha / A;

    const w = 2 * Math.PI * f / SAMPLE_RATE;
    const cosW = Math.cos(w);
    const cos2W = Math.cos(2 * w);
    const sinW = Math.sin(w);
    const sin2W = Math.sin(2 * w);

    const numRe = b0 + b1 * cosW + b2 * cos2W;
    const numIm = -b1 * sinW - b2 * sin2W;
    const denRe = a0 + a1 * cosW + a2 * cos2W;
    const denIm = -a1 * sinW - a2 * sin2W;

    const numMagSq = numRe * numRe + numIm * numIm;
    const denMagSq = denRe * denRe + denIm * denIm;

    return Math.sqrt(numMagSq / Math.max(1e-30, denMagSq));
}

function combinedGainDb(f: number, bands: EqBand[]): number {
    let totalMag = 1;
    for (const b of bands) totalMag *= biquadMagnitude(f, b);
    if (totalMag <= 1e-10) return -200;
    return 20 * Math.log10(totalMag);
}

interface EqCurveProps {
    params: EqParams;
    inputSpec: Float32Array | null;
    outputSpec: Float32Array | null;
}

function EqCurve({ params, inputSpec, outputSpec }: EqCurveProps) {
    const W = 720;
    const H = 220;
    const gainMin = -24;
    const gainMax = 24;
    const SAMPLES = 240;

    // Log freq mapping for the EQ curve.
    const lnLo = Math.log(F_MIN);
    const lnHi = Math.log(F_MAX);
    const xOf = (f: number) => ((Math.log(Math.max(F_MIN, Math.min(F_MAX, f))) - lnLo) / (lnHi - lnLo)) * W;
    const yOf = (g: number) => H * (1 - (Math.max(gainMin, Math.min(gainMax, g)) - gainMin) / (gainMax - gainMin));

    // Spectrum dB → vertical position uses a SEPARATE axis -80 dBFS …
    // 0 dBFS so the spectrum fills the panel without colliding with
    // the EQ curve's gain axis. We map dBFS so 0 dBFS is at the same
    // pixel as 0 dB gain (centre) and -80 dBFS sits at the bottom.
    const specDbMin = -80;
    const specDbMax = 0;
    const ySpec = (db: number) => H * (1 - (Math.max(specDbMin, Math.min(specDbMax, db)) - specDbMin) / (specDbMax - specDbMin));

    const path = useMemo(() => {
        const cmds: string[] = [];
        for (let i = 0; i <= SAMPLES; i++) {
            const t = i / SAMPLES;
            const f = Math.exp(lnLo + t * (lnHi - lnLo));
            const g = combinedGainDb(f, params.bands);
            cmds.push((i === 0 ? 'M' : 'L') + ' ' + xOf(f).toFixed(1) + ' ' + yOf(g).toFixed(1));
        }
        return cmds.join(' ');
    }, [params.bands, lnLo, lnHi]);

    const fillPath = useMemo(() => {
        const zeroY = yOf(0);
        return `${path} L ${W} ${zeroY} L 0 ${zeroY} Z`;
    }, [path]);

    // Spectrum paths: walk SPECTRUM_BIN_COUNT log-spaced bins. The
    // server has already log-binned the FFT output for us so the X-
    // mapping is uniform across bins (each bin → 1/N of the X axis
    // in log-frequency).
    const inputSpecPath = useMemo(() => {
        if (!inputSpec || inputSpec.length === 0) return null;
        const cmds: string[] = [];
        const baseY = H;
        for (let k = 0; k < inputSpec.length; k++) {
            const t = (k + 0.5) / inputSpec.length;
            const f = Math.exp(lnLo + t * (lnHi - lnLo));
            const x = xOf(f);
            const y = ySpec(inputSpec[k]);
            cmds.push((k === 0 ? `M ${x.toFixed(1)} ${baseY}` : '') + ` L ${x.toFixed(1)} ${y.toFixed(1)}`);
        }
        cmds.push(` L ${W} ${baseY} Z`);
        return cmds.join('');
    }, [inputSpec, lnLo, lnHi]);

    const outputSpecPath = useMemo(() => {
        if (!outputSpec || outputSpec.length === 0) return null;
        const cmds: string[] = [];
        for (let k = 0; k < outputSpec.length; k++) {
            const t = (k + 0.5) / outputSpec.length;
            const f = Math.exp(lnLo + t * (lnHi - lnLo));
            const x = xOf(f);
            const y = ySpec(outputSpec[k]);
            cmds.push((k === 0 ? 'M' : 'L') + ' ' + x.toFixed(1) + ' ' + y.toFixed(1));
        }
        return cmds.join(' ');
    }, [outputSpec, lnLo, lnHi]);

    const freqGrid = [50, 100, 200, 500, 1000, 2000, 5000, 10000];
    const gainGrid = [-18, -12, -6, 6, 12, 18];

    return (
        <svg
            viewBox={`0 0 ${W} ${H}`}
            preserveAspectRatio="none"
            style={{
                width: '100%',
                height: 'auto',
                maxHeight: 280,
                background: 'var(--bg-inset, #0e1014)',
                borderRadius: 4,
                border: '1px solid var(--line-1, #2a2c30)',
            }}
            role="img"
            aria-label="EQ frequency response with live input/output spectrum"
        >
            <defs>
                <linearGradient id="eq-fill" x1="0" y1="0" x2="0" y2={H} gradientUnits="userSpaceOnUse">
                    <stop offset="0%"   stopColor="var(--accent, #4a9eff)" stopOpacity={0.20} />
                    <stop offset="50%"  stopColor="var(--accent, #4a9eff)" stopOpacity={0.05} />
                    <stop offset="100%" stopColor="var(--accent, #4a9eff)" stopOpacity={0.20} />
                </linearGradient>
                <filter id="eq-curve-glow" x="-2%" y="-2%" width="104%" height="104%">
                    <feGaussianBlur stdDeviation="1" result="blur" />
                    <feMerge>
                        <feMergeNode in="blur" />
                        <feMergeNode in="SourceGraphic" />
                    </feMerge>
                </filter>
            </defs>

            {/* Frequency vertical grid + labels */}
            {freqGrid.map((f) => (
                <g key={`f-${f}`}>
                    <line x1={xOf(f)} y1={0} x2={xOf(f)} y2={H} stroke="var(--line-1, #2a2c30)" strokeDasharray="2 3" opacity={0.5} />
                    <text x={xOf(f) + 2} y={H - 4} fill="var(--fg-3, #5a5e66)" fontSize={8} fontFamily="var(--font-mono, JetBrains Mono, ui-monospace, monospace)">
                        {f >= 1000 ? `${f / 1000}k` : f}
                    </text>
                </g>
            ))}

            {/* Gain horizontal grid + labels */}
            {gainGrid.map((g) => (
                <g key={`g-${g}`}>
                    <line x1={0} y1={yOf(g)} x2={W} y2={yOf(g)} stroke="var(--line-1, #2a2c30)" strokeDasharray="2 3" opacity={0.5} />
                    <text x={W - 22} y={yOf(g) - 2} fill="var(--fg-3, #5a5e66)" fontSize={8} fontFamily="var(--font-mono, JetBrains Mono, ui-monospace, monospace)">
                        {g > 0 ? '+' : ''}{g}
                    </text>
                </g>
            ))}

            {/* Live INPUT spectrum — semi-transparent fill BEHIND the
                output trace. This is the "before EQ" view. */}
            {inputSpecPath && (
                <path d={inputSpecPath} fill="var(--accent, #4a9eff)" fillOpacity={0.18} stroke="none" />
            )}

            {/* Live OUTPUT spectrum — bright outline OVER the input fill.
                The gap between input fill and output outline visualises
                what the EQ is doing to the signal in real time. */}
            {outputSpecPath && (
                <path d={outputSpecPath} stroke="var(--accent, #4a9eff)" strokeWidth={1.3} strokeOpacity={0.85} fill="none" />
            )}

            {/* Unity (0 dB) reference line */}
            <line x1={0} y1={yOf(0)} x2={W} y2={yOf(0)} stroke="var(--fg-3, #5a5e66)" strokeWidth={1} opacity={0.6} />

            {/* Per-band position markers */}
            {params.bands.map((band, i) => {
                const x = xOf(band.freqHz);
                const y = yOf(band.gainDb);
                return (
                    <g key={`band-${i}`}>
                        <line x1={x} y1={0} x2={x} y2={H} stroke="var(--power, #ffc93a)" strokeWidth={0.5} opacity={0.25} />
                        <circle cx={x} cy={y} r={3.5} fill="var(--power, #ffc93a)" opacity={0.85} stroke="var(--bg-0, #0e1014)" strokeWidth={1} />
                        <text x={x + 4} y={y - 6} fill="var(--power, #ffc93a)" fontSize={9} fontFamily="var(--font-mono, JetBrains Mono, ui-monospace, monospace)" opacity={0.8}>
                            {i + 1}
                        </text>
                    </g>
                );
            })}

            {/* Combined EQ response fill (over spectrum, under curve) */}
            <path d={fillPath} fill="url(#eq-fill)" opacity={0.6} />

            {/* Combined EQ response curve (top layer) */}
            <path d={path} stroke="var(--power, #ffc93a)" strokeWidth={2.2} fill="none" filter="url(#eq-curve-glow)" />
        </svg>
    );
}

// ---------------------------------------------------------------
// EqPanel — v0.2.0 layout. Header + I/O column · curve · I/O column
// + 10 band columns below.
// ---------------------------------------------------------------
function EqPanel({ api }: { api: ZeusPluginApi }) {
    const [params, setParams] = useState<EqParams>(DEFAULT_PARAMS);
    const [meters, setMeters] = useState<EqMeters>(DEFAULT_METERS);
    const lastPostedRef = useRef<EqParams>(DEFAULT_PARAMS);

    // Spectrum data + peak-hold buffers. We hold the spectrum as
    // Float32Arrays for cheap re-renders and to avoid garbage churn.
    const [inputSpec, setInputSpec] = useState<Float32Array | null>(null);
    const [outputSpec, setOutputSpec] = useState<Float32Array | null>(null);
    const inputPeakRef  = useRef<Float32Array | null>(null);
    const outputPeakRef = useRef<Float32Array | null>(null);

    useEffect(() => {
        let active = true;
        (async () => {
            try {
                const res = await api.callBackend('GET', '/params');
                if (active && res.ok) {
                    const p = (await res.json()) as EqParams;
                    if (p && p.bands && p.bands.length === BAND_COUNT) {
                        // Defensive defaults for v0.1.0-saved settings
                        // that don't have inputGainDb / outputGainDb.
                        const normalised: EqParams = {
                            ...p,
                            inputGainDb:  p.inputGainDb  ?? 0,
                            outputGainDb: p.outputGainDb ?? 0,
                        };
                        setParams(normalised);
                        lastPostedRef.current = normalised;
                    }
                }
            } catch { /* swallow */ }
        })();
        return () => { active = false; };
    }, [api]);

    useEffect(() => {
        let active = true;
        const tick = async () => {
            try {
                const res = await api.callBackend('GET', '/meters');
                if (active && res.ok) {
                    setMeters((await res.json()) as EqMeters);
                }
            } catch { /* swallow */ }
        };
        const t = window.setInterval(tick, 66);
        tick();
        return () => { active = false; window.clearInterval(t); };
    }, [api]);

    // Spectrum poll loop — fetch new bins from /spectrum, blend with
    // a peak-hold + falloff buffer so the visible trace doesn't
    // flicker on transients. Falloff per tick is computed from
    // SPECTRUM_FALLOFF_DB_PER_SEC so reducing the poll interval
    // automatically keeps the felt-decay constant.
    useEffect(() => {
        let active = true;
        const tick = async () => {
            try {
                const res = await api.callBackend('GET', '/spectrum');
                if (!active || !res.ok) return;
                const j = (await res.json()) as EqSpectrum;
                if (!j || !Array.isArray(j.inputBinsDb) || !Array.isArray(j.outputBinsDb)) return;
                const N = j.binCount || j.inputBinsDb.length;

                // Initialise peak buffers on first tick.
                if (inputPeakRef.current === null || inputPeakRef.current.length !== N) {
                    inputPeakRef.current  = new Float32Array(N).fill(SPECTRUM_DB_FLOOR);
                    outputPeakRef.current = new Float32Array(N).fill(SPECTRUM_DB_FLOOR);
                }

                const ip = inputPeakRef.current!;
                const op = outputPeakRef.current!;
                for (let k = 0; k < N; k++) {
                    const inBin  = Math.max(SPECTRUM_DB_FLOOR, Math.min(SPECTRUM_DB_CEIL, j.inputBinsDb[k]));
                    const outBin = Math.max(SPECTRUM_DB_FLOOR, Math.min(SPECTRUM_DB_CEIL, j.outputBinsDb[k]));
                    // Peak attacks instantly (max of current + new),
                    // releases linearly at SPECTRUM_FALLOFF_PER_TICK
                    // dB per tick.
                    ip[k] = inBin > ip[k] ? inBin : Math.max(SPECTRUM_DB_FLOOR, ip[k] - SPECTRUM_FALLOFF_PER_TICK);
                    op[k] = outBin > op[k] ? outBin : Math.max(SPECTRUM_DB_FLOOR, op[k] - SPECTRUM_FALLOFF_PER_TICK);
                }
                // Hand the buffers to React; we slice() to a fresh
                // Float32Array so the state update sees a NEW
                // reference and re-renders (otherwise React would
                // bail because the ref didn't change).
                setInputSpec(new Float32Array(ip));
                setOutputSpec(new Float32Array(op));
            } catch { /* swallow */ }
        };
        const t = window.setInterval(tick, SPECTRUM_POLL_MS);
        tick();
        return () => { active = false; window.clearInterval(t); };
    }, [api]);

    const postDebounceRef = useRef<number | null>(null);
    const schedulePost = useCallback((next: EqParams) => {
        setParams(next);
        if (postDebounceRef.current != null) window.clearTimeout(postDebounceRef.current);
        postDebounceRef.current = window.setTimeout(() => {
            const last = lastPostedRef.current;
            const patch: Partial<EqParams> = {};
            // Whole-array semantics for bands — the wire format expects all 10.
            const bandsChanged = next.bands.some((b, i) =>
                b.freqHz !== last.bands[i]?.freqHz ||
                b.gainDb !== last.bands[i]?.gainDb ||
                b.q      !== last.bands[i]?.q
            );
            if (bandsChanged) patch.bands = next.bands;
            if (next.bypass !== last.bypass) patch.bypass = next.bypass;
            if (next.inputGainDb !== last.inputGainDb) patch.inputGainDb = next.inputGainDb;
            if (next.outputGainDb !== last.outputGainDb) patch.outputGainDb = next.outputGainDb;
            if (Object.keys(patch).length === 0) return;
            void api.callBackend('POST', '/params', patch).then(async (res) => {
                if (res.ok) {
                    const echoed = (await res.json()) as EqParams;
                    if (echoed && echoed.bands && echoed.bands.length === BAND_COUNT) {
                        const normalised: EqParams = {
                            ...echoed,
                            inputGainDb:  echoed.inputGainDb  ?? 0,
                            outputGainDb: echoed.outputGainDb ?? 0,
                        };
                        lastPostedRef.current = normalised;
                    }
                }
            });
        }, 120);
    }, [api]);

    const setBand = useCallback((index: number, patch: Partial<EqBand>) => {
        const nextBands = params.bands.map((b, i) => i === index ? { ...b, ...patch } : b);
        schedulePost({ ...params, bands: nextBands });
    }, [params, schedulePost]);

    const resetAll = useCallback(() => {
        const next: EqParams = {
            ...params,
            bands: DEFAULT_FREQS.map((f) => ({ freqHz: f, gainDb: 0, q: 1.0 })),
            inputGainDb: 0,
            outputGainDb: 0,
        };
        schedulePost(next);
    }, [params, schedulePost]);

    return (
        <section style={{
            display: 'flex',
            flexDirection: 'column',
            gap: 12,
            padding: 12,
            background: 'linear-gradient(180deg, var(--panel-top, #14161a), var(--panel-bot, #0e1014))',
            border: '1px solid var(--line-1, #2a2c30)',
            borderRadius: 8,
            color: 'var(--fg-0, #e8eaed)',
            fontFamily: 'var(--font-sans, Inter, system-ui, sans-serif)',
            boxShadow: '0 1px 0 rgba(255,255,255,0.04) inset, 0 4px 12px rgba(0,0,0,0.25)',
        }}>
            {/* Brass-plate header — title + reset + bypass. IN/OUT
                meter readouts moved into the flanking columns below. */}
            <header style={{
                position: 'relative',
                padding: '8px 6px 10px',
                borderBottom: '1px solid var(--line-1, #2a2c30)',
                boxShadow: 'inset 0 2px 0 var(--power, #ffc93a), inset 0 3px 8px rgba(255, 201, 58, 0.12)',
                display: 'flex',
                alignItems: 'center',
                justifyContent: 'space-between',
                gap: 12,
            }}>
                <h3 style={{
                    margin: 0,
                    fontFamily: 'var(--font-sans, Inter, system-ui, sans-serif)',
                    fontSize: 13,
                    fontWeight: 600,
                    letterSpacing: 2,
                    textTransform: 'uppercase',
                    color: params.bypass ? 'var(--fg-2, #b8bcc3)' : 'var(--fg-0, #e8eaed)',
                    textShadow: params.bypass ? 'none' : '0 0 8px rgba(255, 201, 58, 0.18)',
                    transition: 'color 120ms linear',
                }}>10-Band Parametric EQ</h3>

                <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                    <button
                        type="button"
                        onClick={resetAll}
                        title="Reset all 10 bands + I/O gain to defaults"
                        style={{
                            padding: '4px 10px',
                            fontFamily: 'var(--font-sans, Inter, system-ui, sans-serif)',
                            fontSize: 10,
                            fontWeight: 600,
                            letterSpacing: 1.2,
                            textTransform: 'uppercase',
                            color: 'var(--fg-1, #d6d8dc)',
                            background: 'var(--bg-2, #1f2226)',
                            border: '1px solid var(--line-2, #3a3d42)',
                            borderRadius: 3,
                            cursor: 'pointer',
                            transition: 'all 120ms ease-out',
                        }}
                    >Reset</button>

                    <button
                        type="button"
                        onClick={() => schedulePost({ ...params, bypass: !params.bypass })}
                        aria-pressed={params.bypass}
                        title={params.bypass ? 'Currently bypassed — click to re-engage. I/O gain still applies in bypass.' : 'Click to bypass — audio flows through with I/O gain only.'}
                        style={{
                            padding: '4px 12px',
                            fontFamily: 'var(--font-sans, Inter, system-ui, sans-serif)',
                            fontSize: 10,
                            fontWeight: 600,
                            letterSpacing: 1.5,
                            textTransform: 'uppercase',
                            color: params.bypass ? '#fff' : 'var(--fg-1, #d6d8dc)',
                            background: params.bypass ? 'var(--tx, #e63a2b)' : 'var(--bg-2, #1f2226)',
                            border: '1px solid ' + (params.bypass ? 'var(--tx, #e63a2b)' : 'var(--line-2, #3a3d42)'),
                            borderRadius: 3,
                            cursor: 'pointer',
                            boxShadow: params.bypass
                                ? '0 0 8px rgba(230, 58, 43, 0.5), inset 0 1px 0 rgba(255, 255, 255, 0.15)'
                                : 'inset 0 1px 0 rgba(255, 255, 255, 0.04)',
                            transition: 'all 120ms ease-out',
                        }}
                    >{params.bypass ? 'Bypassed' : 'Bypass'}</button>
                </div>
            </header>

            {/* Main viz row — IN column · spectrum/curve · OUT column.
                The I/O columns stay full-opacity during bypass since
                their gain stage IS the only thing the operator is
                using when the cascade is off. */}
            <div style={{
                display: 'grid',
                gridTemplateColumns: 'auto 1fr auto',
                gap: 12,
                alignItems: 'stretch',
            }}>
                {/* Input column: gain knob ABOVE meter */}
                <div style={{
                    display: 'flex',
                    flexDirection: 'column',
                    alignItems: 'center',
                    gap: 8,
                    padding: '4px 6px',
                    background: 'var(--bg-1, #14161a)',
                    border: '1px solid var(--line-1, #2a2c30)',
                    borderRadius: 4,
                }}>
                    <Knob
                        label="In Gain"
                        value={params.inputGainDb}
                        min={-24}
                        max={24}
                        defaultValue={0}
                        unit="dB"
                        formatValue={(v) => (v > 0 ? '+' : '') + v.toFixed(1)}
                        onChange={(v) => schedulePost({ ...params, inputGainDb: v })}
                        size={40}
                    />
                    <VerticalMeter levelDb={meters.inputPeakDb} label="In" />
                </div>

                {/* Centre: live spectrum behind the EQ curve. Dims when
                    bypassed because the cascade isn't shaping anything. */}
                <div style={{
                    display: 'flex',
                    flexDirection: 'column',
                    gap: 6,
                    opacity: params.bypass ? 0.55 : 1,
                    transition: 'opacity 160ms ease-out',
                }}>
                    <EqCurve params={params} inputSpec={inputSpec} outputSpec={outputSpec} />
                </div>

                {/* Output column: gain knob ABOVE meter */}
                <div style={{
                    display: 'flex',
                    flexDirection: 'column',
                    alignItems: 'center',
                    gap: 8,
                    padding: '4px 6px',
                    background: 'var(--bg-1, #14161a)',
                    border: '1px solid var(--line-1, #2a2c30)',
                    borderRadius: 4,
                }}>
                    <Knob
                        label="Out Gain"
                        value={params.outputGainDb}
                        min={-24}
                        max={24}
                        defaultValue={0}
                        unit="dB"
                        formatValue={(v) => (v > 0 ? '+' : '') + v.toFixed(1)}
                        onChange={(v) => schedulePost({ ...params, outputGainDb: v })}
                        size={40}
                    />
                    <VerticalMeter levelDb={meters.outputPeakDb} label="Out" />
                </div>
            </div>

            {/* 10 band columns — disabled when bypassed since the
                cascade isn't running. I/O gain stays live above. */}
            <div style={{
                opacity: params.bypass ? 0.35 : 1,
                pointerEvents: params.bypass ? 'none' : 'auto',
                transition: 'opacity 160ms ease-out',
            }}>
                <div style={{
                    display: 'grid',
                    gridTemplateColumns: 'repeat(10, minmax(0, 1fr))',
                    gap: 6,
                }}>
                    {params.bands.map((band, i) => (
                        <div
                            key={`band-${i}`}
                            style={{
                                display: 'flex',
                                flexDirection: 'column',
                                alignItems: 'center',
                                gap: 6,
                                padding: '6px 2px 4px',
                                background: 'var(--bg-1, #14161a)',
                                border: '1px solid var(--line-1, #2a2c30)',
                                borderRadius: 4,
                            }}
                        >
                            <span style={{
                                fontFamily: 'var(--font-mono, JetBrains Mono, ui-monospace, monospace)',
                                fontSize: 10,
                                color: 'var(--power, #ffc93a)',
                                fontWeight: 600,
                            }}>{i + 1}</span>

                            <Knob
                                label="Freq"
                                value={band.freqHz}
                                min={20}
                                max={20000}
                                defaultValue={DEFAULT_FREQS[i]}
                                unit={band.freqHz >= 1000 ? 'kHz' : 'Hz'}
                                formatValue={(v) => v >= 1000 ? (v / 1000).toFixed(2) : v.toFixed(0)}
                                logScale
                                onChange={(v) => setBand(i, { freqHz: v })}
                            />

                            <Knob
                                label="Gain"
                                value={band.gainDb}
                                min={-24}
                                max={24}
                                defaultValue={0}
                                unit="dB"
                                formatValue={(v) => (v > 0 ? '+' : '') + v.toFixed(1)}
                                onChange={(v) => setBand(i, { gainDb: v })}
                            />

                            <Knob
                                label="Q"
                                value={band.q}
                                min={0.1}
                                max={10}
                                defaultValue={1.0}
                                unit=""
                                formatValue={(v) => v.toFixed(2)}
                                logScale
                                onChange={(v) => setBand(i, { q: v })}
                            />
                        </div>
                    ))}
                </div>
            </div>

            <footer style={{
                fontFamily: 'var(--font-sans, Inter, system-ui, sans-serif)',
                fontSize: 10,
                color: 'var(--fg-3, #5a5e66)',
                textAlign: 'right',
                paddingRight: 4,
            }}>
                Drag knobs to adjust · shift-drag fine · double-click reset · wheel = ±1 %
            </footer>
        </section>
    );
}

// ---------------------------------------------------------------
export default function register(api: ZeusPluginApi) {
    api.registerPanel({
        id: 'eq.main',
        component: () => <EqPanel api={api} />,
    });
}

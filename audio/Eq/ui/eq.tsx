// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus voice audio chain v1 — 10-Band Parametric EQ UI module.
// Copyright (C) 2025-2026 KB2UKA and contributors.
//
// Centerpiece SVG curve: combined magnitude of all 10 cascaded biquad
// peaking filters, plotted log-frequency on X (20 Hz → 20 kHz) vs
// gain dB on Y (-24 → +24). Below the curve: 10 columns of (freq,
// gain, Q) rotary knobs. Brass-plate header with Bypass + Reset
// buttons. All Zeus tokens, no raw hex.

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
}

interface EqMeters {
    inputPeakDb: number;
    outputPeakDb: number;
}

const BAND_COUNT = 10;

const DEFAULT_FREQS = [80, 150, 300, 500, 800, 1200, 1800, 2700, 4000, 6000];

const DEFAULT_PARAMS: EqParams = {
    bands: DEFAULT_FREQS.map((f) => ({ freqHz: f, gainDb: 0, q: 1.0 })),
    bypass: false,
};

const DEFAULT_METERS: EqMeters = { inputPeakDb: -200, outputPeakDb: -200 };

// ---------------------------------------------------------------
// Knob — SVG rotary with drag-to-rotate. Same shape as Compressor's
// knob (the Zeus chain-plugin convention).
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
// EQ curve viz — combined magnitude of all 10 peaking biquads,
// log-frequency on X (20 Hz to 20 kHz), gain dB on Y (-24 to +24).
// Sampled at 256 points across the log-frequency axis; cheap enough
// to recompute every render. Vertical band-position markers in
// --power gold show where each band sits on the frequency axis.
// ---------------------------------------------------------------

const SAMPLE_RATE = 48000;
const F_MIN = 20;
const F_MAX = 20000;

// Compute |H(e^jω)|² for a single peaking biquad at frequency f.
// Returns linear magnitude (NOT dB).
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

    // Evaluate at ω = 2πf/fs.
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

function EqCurve({ params }: { params: EqParams }) {
    const W = 720;
    const H = 220;
    const gainMin = -24;
    const gainMax = 24;
    const SAMPLES = 240;

    // Log freq mapping.
    const lnLo = Math.log(F_MIN);
    const lnHi = Math.log(F_MAX);
    const xOf = (f: number) => ((Math.log(Math.max(F_MIN, Math.min(F_MAX, f))) - lnLo) / (lnHi - lnLo)) * W;
    const yOf = (g: number) => H * (1 - (Math.max(gainMin, Math.min(gainMax, g)) - gainMin) / (gainMax - gainMin));

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

    // Frequency grid labels.
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
            aria-label="EQ frequency response curve"
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

            {/* Combined response fill */}
            <path d={fillPath} fill="url(#eq-fill)" />

            {/* Combined response curve */}
            <path d={path} stroke="var(--accent, #4a9eff)" strokeWidth={2.2} fill="none" filter="url(#eq-curve-glow)" />
        </svg>
    );
}

// ---------------------------------------------------------------
// EqPanel — header + curve + 10 band columns.
// ---------------------------------------------------------------
function EqPanel({ api }: { api: ZeusPluginApi }) {
    const [params, setParams] = useState<EqParams>(DEFAULT_PARAMS);
    const [meters, setMeters] = useState<EqMeters>(DEFAULT_METERS);
    const lastPostedRef = useRef<EqParams>(DEFAULT_PARAMS);

    useEffect(() => {
        let active = true;
        (async () => {
            try {
                const res = await api.callBackend('GET', '/params');
                if (active && res.ok) {
                    const p = (await res.json()) as EqParams;
                    if (p && p.bands && p.bands.length === BAND_COUNT) {
                        setParams(p);
                        lastPostedRef.current = p;
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
            if (Object.keys(patch).length === 0) return;
            void api.callBackend('POST', '/params', patch).then(async (res) => {
                if (res.ok) {
                    const echoed = (await res.json()) as EqParams;
                    if (echoed && echoed.bands && echoed.bands.length === BAND_COUNT) {
                        lastPostedRef.current = echoed;
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
            bands: DEFAULT_FREQS.map((f) => ({ freqHz: f, gainDb: 0, q: 1.0 })),
            bypass: params.bypass,
        };
        schedulePost(next);
    }, [params.bypass, schedulePost]);

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
            {/* Brass-plate header with Bypass + Reset */}
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
                    <span style={{
                        fontFamily: 'var(--font-mono, JetBrains Mono, ui-monospace, monospace)',
                        fontSize: 10,
                        color: 'var(--fg-2, #b8bcc3)',
                        letterSpacing: 0.5,
                    }}>
                        IN {meters.inputPeakDb > -150 ? meters.inputPeakDb.toFixed(0) : '—'} · OUT {meters.outputPeakDb > -150 ? meters.outputPeakDb.toFixed(0) : '—'} dBFS
                    </span>

                    <button
                        type="button"
                        onClick={resetAll}
                        title="Reset all 10 bands to default frequencies, 0 dB gain, Q=1.0"
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
                        title={params.bypass ? 'Currently bypassed — click to re-engage' : 'Click to bypass this block (audio flows through unchanged)'}
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

            <div style={{
                display: 'flex',
                flexDirection: 'column',
                gap: 12,
                opacity: params.bypass ? 0.45 : 1,
                transition: 'opacity 160ms ease-out',
                pointerEvents: params.bypass ? 'none' : 'auto',
            }}>
                {/* Combined transfer-function curve */}
                <EqCurve params={params} />

                {/* 10 band columns */}
                <div style={{
                    display: 'grid',
                    gridTemplateColumns: 'repeat(10, minmax(0, 1fr))',
                    gap: 6,
                    paddingTop: 4,
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

// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus voice audio chain — Tube Preamp UI module.
// Copyright (C) 2025-2026 KB2UKA and contributors.
//
// Centerpiece: SVG transfer-function viz showing the asymmetric tube curve
// (input → output, with the bias-shifted tanh S-curve drawn against the
// unity reference). The curve becomes visibly asymmetric as Bias departs
// from 0 — that's the 2nd-harmonic "warmth" knob made directly legible.
// Right of the curve: four rotary knobs (Drive / Bias / Warmth / Output).
// Brass-plate header with Bypass + live IN/OUT/DRIVE metering.

import { useCallback, useEffect, useRef, useState } from 'react';

interface ZeusPluginApi {
    registerPanel(spec: { id: string; component: React.ComponentType }): void;
    callBackend(method: string, path: string, body?: unknown): Promise<Response>;
}

interface TubePreampParams {
    driveDb: number;
    bias: number;
    warmthHz: number;
    outputDb: number;
    bypass: boolean;
}

interface TubePreampMeters {
    inputPeakDb: number;
    outputPeakDb: number;
    drivePeakDb: number;
}

const DEFAULT_PARAMS: TubePreampParams = {
    driveDb: 12,
    bias: 0.3,
    warmthHz: 12000,
    outputDb: -6,
    bypass: false,
};

const DEFAULT_METERS: TubePreampMeters = {
    inputPeakDb: -200,
    outputPeakDb: -200,
    drivePeakDb: -200,
};

// ---------------------------------------------------------------
// SVG rotary knob — same shape as Compressor / EQ / Exciter (audio-chain convention).
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
    bipolar?: boolean; // -1..+1 style — center indicator at 12 o'clock
    onChange: (v: number) => void;
}

function Knob({ label, value, min, max, defaultValue, unit, formatValue, logScale, bipolar, onChange }: KnobProps) {
    const dragStartRef = useRef<{ y: number; startValue: number; fine: boolean } | null>(null);
    const knobRef = useRef<SVGSVGElement | null>(null);

    const valueToNorm = useCallback((v: number): number => {
        if (logScale) {
            return (Math.log(Math.max(min, v)) - Math.log(min)) / (Math.log(max) - Math.log(min));
        }
        return (v - min) / (max - min);
    }, [logScale, min, max]);

    const normToValue = useCallback((n: number): number => {
        const c = Math.max(0, Math.min(1, n));
        if (logScale) return Math.exp(Math.log(min) + c * (Math.log(max) - Math.log(min)));
        return min + c * (max - min);
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
        const sens = dragStartRef.current.fine ? 0.0005 : 0.005;
        const startNorm = valueToNorm(dragStartRef.current.startValue);
        onChange(normToValue(startNorm + dy * sens));
    }, [valueToNorm, normToValue, onChange]);

    const onPointerUp = useCallback((e: React.PointerEvent<SVGSVGElement>) => {
        knobRef.current?.releasePointerCapture(e.pointerId);
        dragStartRef.current = null;
    }, []);

    const onDoubleClick = useCallback(() => onChange(defaultValue), [defaultValue, onChange]);

    const onWheel = useCallback((e: React.WheelEvent<SVGSVGElement>) => {
        e.preventDefault();
        const step = e.shiftKey ? 0.001 : 0.01;
        const dir = e.deltaY > 0 ? -1 : 1;
        const startNorm = valueToNorm(value);
        onChange(normToValue(startNorm + dir * step));
    }, [value, valueToNorm, normToValue, onChange]);

    const display = formatValue ? formatValue(value) : value.toFixed(2);

    const ticks: number[] = [];
    for (let i = 0; i <= 9; i++) ticks.push(-135 + (i / 9) * 270);

    const R = 22;
    const TICK_OUTER = 24;
    const TICK_INNER = 26;
    const POINTER_LEN = 17;
    const CENTER_DEG = 0; // 12-o'clock — for bipolar reference

    return (
        <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 4, minWidth: 68, userSelect: 'none' }}>
            <span style={{
                fontFamily: 'var(--font-sans, Inter, system-ui, sans-serif)',
                fontSize: 11, letterSpacing: 0.5, textTransform: 'uppercase',
                color: 'var(--fg-2, #b8bcc3)', fontWeight: 500,
            }}>{label}</span>

            <svg
                ref={knobRef}
                viewBox="-32 -32 64 64"
                width={56}
                height={56}
                onPointerDown={onPointerDown}
                onPointerMove={onPointerMove}
                onPointerUp={onPointerUp}
                onPointerCancel={onPointerUp}
                onDoubleClick={onDoubleClick}
                onWheel={onWheel}
                style={{ cursor: 'ns-resize', touchAction: 'none', filter: 'drop-shadow(0 1px 2px rgba(0,0,0,0.45))' }}
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

                    // For bipolar knobs the active range fills *outward* from
                    // center toward the current position. For monopolar it
                    // fills from the min end (-135°) like a normal pot.
                    const active = bipolar
                        ? (deg >= Math.min(CENTER_DEG, angleDeg) - 0.5 && deg <= Math.max(CENTER_DEG, angleDeg) + 0.5)
                        : deg <= angleDeg + 0.5;

                    return (
                        <line key={i} x1={x1} y1={y1} x2={x2} y2={y2}
                              stroke={active ? 'var(--accent, #4a9eff)' : 'var(--line-1, #2a2c30)'}
                              strokeWidth={1.5} strokeLinecap="round" />
                    );
                })}

                <defs>
                    <radialGradient id={`dial-tube-${label}`} cx="0.35" cy="0.35" r="0.75">
                        <stop offset="0%"  stopColor="var(--bg-2, #1f2226)" />
                        <stop offset="100%" stopColor="var(--bg-0, #0e1014)" />
                    </radialGradient>
                </defs>
                <circle cx={0} cy={0} r={R} fill={`url(#dial-tube-${label})`} stroke="var(--line-2, #3a3d42)" strokeWidth={1} />

                {(() => {
                    const rad = (angleDeg - 90) * Math.PI / 180;
                    const x = POINTER_LEN * Math.cos(rad);
                    const y = POINTER_LEN * Math.sin(rad);
                    return (
                        <>
                            <line x1={0} y1={0} x2={x} y2={y} stroke="var(--accent, #4a9eff)" strokeWidth={2.5} strokeLinecap="round" />
                            <circle cx={x} cy={y} r={2.5} fill="var(--accent, #4a9eff)" />
                        </>
                    );
                })()}
            </svg>

            <span style={{
                fontFamily: 'var(--font-mono, JetBrains Mono, ui-monospace, monospace)',
                fontSize: 12, color: 'var(--fg-0, #e8eaed)',
                fontVariantNumeric: 'tabular-nums', lineHeight: 1,
            }}>{display}{unit ? ` ${unit}` : ''}</span>
        </div>
    );
}

// ---------------------------------------------------------------
// TubeCurve — the asymmetric tanh transfer-function viz.
//
//   y = tanh(driveLin * x + bias) - tanh(bias)
//
// As Bias > 0 the upper half-cycle compresses while the lower half
// stays cleaner → asymmetric clipping → strong 2nd-harmonic content.
// As Drive grows the S-curve flattens at the extremes → more harmonics.
// At Bias = 0 the curve is symmetric (pure tanh).
// ---------------------------------------------------------------
function TubeCurve({ driveDb, bias }: { driveDb: number; bias: number }) {
    const W = 240;
    const H = 200;

    const xOf = (x: number) => (x + 1) * 0.5 * W; // -1 → 0, +1 → W
    const yOf = (y: number) => H - (y + 1) * 0.5 * H;

    const driveLin = Math.pow(10, driveDb / 20);
    const dcOffset = Math.tanh(bias);

    const pts: string[] = [];
    const N = 240;
    for (let i = 0; i <= N; i++) {
        const x = -1 + 2 * (i / N);
        const y = Math.tanh(driveLin * x + bias) - dcOffset;
        // Clamp to viewable range so a huge drive doesn't escape the box.
        const yClamped = Math.max(-1, Math.min(1, y));
        pts.push((i === 0 ? 'M' : 'L') + ' ' + xOf(x).toFixed(1) + ' ' + yOf(yClamped).toFixed(1));
    }

    return (
        <svg
            viewBox={`0 0 ${W} ${H}`}
            preserveAspectRatio="none"
            style={{
                width: '100%',
                maxWidth: 300,
                height: 'auto',
                background: 'var(--bg-inset, #0e1014)',
                borderRadius: 4,
                border: '1px solid var(--line-1, #2a2c30)',
            }}
            role="img"
            aria-label="Tube preamp transfer function"
        >
            <defs>
                <filter id="tube-glow" x="-2%" y="-2%" width="104%" height="104%">
                    <feGaussianBlur stdDeviation="1.4" result="blur" />
                    <feMerge>
                        <feMergeNode in="blur" />
                        <feMergeNode in="SourceGraphic" />
                    </feMerge>
                </filter>
            </defs>

            {/* Grid */}
            {[-0.5, 0.5].map((v) => (
                <g key={`vg${v}`}>
                    <line x1={xOf(v)} y1={0} x2={xOf(v)} y2={H} stroke="var(--line-1, #2a2c30)" strokeDasharray="2 3" opacity={0.5} />
                    <line x1={0} y1={yOf(v)} x2={W} y2={yOf(v)} stroke="var(--line-1, #2a2c30)" strokeDasharray="2 3" opacity={0.5} />
                </g>
            ))}

            {/* Axes through (0,0) */}
            <line x1={xOf(0)} y1={0} x2={xOf(0)} y2={H} stroke="var(--fg-3, #5a5e66)" strokeWidth={1} opacity={0.7} />
            <line x1={0} y1={yOf(0)} x2={W} y2={yOf(0)} stroke="var(--fg-3, #5a5e66)" strokeWidth={1} opacity={0.7} />

            {/* Unity reference y = x */}
            <line x1={xOf(-1)} y1={yOf(-1)} x2={xOf(1)} y2={yOf(1)}
                  stroke="var(--fg-3, #5a5e66)" strokeDasharray="1 4" opacity={0.6} />

            {/* The transfer curve */}
            <path d={pts.join(' ')} stroke="var(--accent, #4a9eff)" strokeWidth={2.4} fill="none" filter="url(#tube-glow)" />

            {/* Axis labels */}
            <text x={4} y={H - 4} fill="var(--fg-3, #5a5e66)" fontSize={8} fontFamily="var(--font-mono, JetBrains Mono, ui-monospace, monospace)">IN</text>
            <text x={W - 14} y={12} fill="var(--fg-3, #5a5e66)" fontSize={8} fontFamily="var(--font-mono, JetBrains Mono, ui-monospace, monospace)">OUT</text>
        </svg>
    );
}

// ---------------------------------------------------------------
// Main panel.
// ---------------------------------------------------------------
function TubePreampPanel({ api }: { api: ZeusPluginApi }) {
    const [params, setParams] = useState<TubePreampParams>(DEFAULT_PARAMS);
    const [meters, setMeters] = useState<TubePreampMeters>(DEFAULT_METERS);
    const lastPostedRef = useRef<TubePreampParams>(DEFAULT_PARAMS);

    useEffect(() => {
        let active = true;
        (async () => {
            try {
                const res = await api.callBackend('GET', '/params');
                if (active && res.ok) {
                    const p = (await res.json()) as TubePreampParams;
                    setParams(p);
                    lastPostedRef.current = p;
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
                if (active && res.ok) setMeters((await res.json()) as TubePreampMeters);
            } catch { /* swallow */ }
        };
        const t = window.setInterval(tick, 66);
        tick();
        return () => { active = false; window.clearInterval(t); };
    }, [api]);

    const postDebounceRef = useRef<number | null>(null);
    const schedulePost = useCallback((next: TubePreampParams) => {
        setParams(next);
        if (postDebounceRef.current != null) window.clearTimeout(postDebounceRef.current);
        postDebounceRef.current = window.setTimeout(() => {
            const last = lastPostedRef.current;
            const patch: Partial<TubePreampParams> = {};
            if (next.driveDb  !== last.driveDb)  patch.driveDb  = next.driveDb;
            if (next.bias     !== last.bias)     patch.bias     = next.bias;
            if (next.warmthHz !== last.warmthHz) patch.warmthHz = next.warmthHz;
            if (next.outputDb !== last.outputDb) patch.outputDb = next.outputDb;
            if (next.bypass   !== last.bypass)   patch.bypass   = next.bypass;
            if (Object.keys(patch).length === 0) return;
            void api.callBackend('POST', '/params', patch).then(async (res) => {
                if (res.ok) {
                    const echoed = (await res.json()) as TubePreampParams;
                    lastPostedRef.current = echoed;
                    setParams(echoed);
                }
            });
        }, 120);
    }, [api]);

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
            {/* Brass-plate header */}
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
                }}>Tube Preamp</h3>

                <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
                    <span style={{
                        fontFamily: 'var(--font-mono, JetBrains Mono, ui-monospace, monospace)',
                        fontSize: 10,
                        color: 'var(--fg-2, #b8bcc3)',
                        letterSpacing: 0.5,
                    }}>
                        IN {meters.inputPeakDb > -150 ? meters.inputPeakDb.toFixed(0) : '—'} ·
                        DRIVE {meters.drivePeakDb > -150 ? meters.drivePeakDb.toFixed(0) : '—'} ·
                        OUT {meters.outputPeakDb > -150 ? meters.outputPeakDb.toFixed(0) : '—'} dBFS
                    </span>

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
                gap: 14,
                alignItems: 'flex-start',
                flexWrap: 'wrap',
                opacity: params.bypass ? 0.45 : 1,
                transition: 'opacity 160ms ease-out',
                pointerEvents: params.bypass ? 'none' : 'auto',
            }}>
                {/* Transfer curve viz */}
                <div style={{ flex: '1 1 280px', minWidth: 240, maxWidth: 360 }}>
                    <TubeCurve driveDb={params.driveDb} bias={params.bias} />
                    <div style={{
                        fontFamily: 'var(--font-mono, JetBrains Mono, ui-monospace, monospace)',
                        fontSize: 10,
                        color: 'var(--fg-3, #5a5e66)',
                        marginTop: 4,
                        textAlign: 'center',
                    }}>
                        tanh(drive · x + bias) − tanh(bias) · post-LP @ {params.warmthHz >= 1000 ? (params.warmthHz / 1000).toFixed(1) + ' kHz' : params.warmthHz.toFixed(0) + ' Hz'}
                    </div>
                </div>

                {/* Knob column */}
                <div style={{
                    display: 'grid',
                    gridTemplateColumns: 'repeat(2, minmax(68px, 1fr))',
                    gap: 12,
                    paddingTop: 4,
                }}>
                    <Knob
                        label="Drive"
                        value={params.driveDb}
                        min={0}
                        max={36}
                        defaultValue={12}
                        unit="dB"
                        formatValue={(v) => v.toFixed(1)}
                        onChange={(v) => schedulePost({ ...params, driveDb: v })}
                    />
                    <Knob
                        label="Bias"
                        value={params.bias}
                        min={-1}
                        max={1}
                        defaultValue={0.3}
                        unit=""
                        formatValue={(v) => v >= 0 ? `+${v.toFixed(2)}` : v.toFixed(2)}
                        bipolar
                        onChange={(v) => schedulePost({ ...params, bias: v })}
                    />
                    <Knob
                        label="Warmth"
                        value={params.warmthHz}
                        min={4000}
                        max={20000}
                        defaultValue={12000}
                        unit={params.warmthHz >= 1000 ? 'kHz' : 'Hz'}
                        formatValue={(v) => v >= 1000 ? (v / 1000).toFixed(1) : v.toFixed(0)}
                        logScale
                        onChange={(v) => schedulePost({ ...params, warmthHz: v })}
                    />
                    <Knob
                        label="Output"
                        value={params.outputDb}
                        min={-24}
                        max={12}
                        defaultValue={-6}
                        unit="dB"
                        formatValue={(v) => v >= 0 ? `+${v.toFixed(1)}` : v.toFixed(1)}
                        onChange={(v) => schedulePost({ ...params, outputDb: v })}
                    />
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
        id: 'tubepreamp.main',
        component: () => <TubePreampPanel api={api} />,
    });
}

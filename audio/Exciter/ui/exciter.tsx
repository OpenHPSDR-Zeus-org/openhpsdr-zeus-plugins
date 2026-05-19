// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus voice audio chain v1 — Aural Exciter UI module.
// Copyright (C) 2025-2026 KB2UKA and contributors.
//
// Centerpiece: SVG saturation transfer-function (input → output, with the
// post-drive tanh S-curve drawn against the unity reference). Right of
// the curve: three rotary knobs (Frequency / Drive / Mix). Brass-plate
// header with Bypass + live IN/OUT/HARMONIC metering. All Zeus tokens.

import { useCallback, useEffect, useRef, useState } from 'react';

interface ZeusPluginApi {
    registerPanel(spec: { id: string; component: React.ComponentType }): void;
    callBackend(method: string, path: string, body?: unknown): Promise<Response>;
}

interface ExciterParams {
    freqHz: number;
    driveDb: number;
    mixPct: number;
    inputDb: number;
    outputDb: number;
    bypass: boolean;
}

interface ExciterMeters {
    inputPeakDb: number;
    outputPeakDb: number;
    harmonicLevelDb: number;
}

const DEFAULT_PARAMS: ExciterParams = {
    freqHz: 5000,
    driveDb: 6,
    mixPct: 20,
    inputDb: 0,
    outputDb: 0,
    bypass: false,
};

const DEFAULT_METERS: ExciterMeters = { inputPeakDb: -200, outputPeakDb: -200, harmonicLevelDb: -200 };

const METER_FLOOR_DB = -60;

// ---------------------------------------------------------------
// Vertical peak meter with peak-hold tick (v0.2.0 addition).
// ---------------------------------------------------------------
function VerticalMeter({ label, peakDb, ngId }: { label: string; peakDb: number; ngId: string }) {
    const W = 26;
    const H = 140;
    const floor = METER_FLOOR_DB;

    const [holdDb, setHoldDb] = useState<number>(floor);
    const lastUpdateRef = useRef<number>(performance.now());
    useEffect(() => {
        const now = performance.now();
        const dt = (now - lastUpdateRef.current) / 1000;
        lastUpdateRef.current = now;
        setHoldDb((prev) => Math.max(floor, Math.max(peakDb, prev - 40 * dt)));
    }, [peakDb, floor]);

    const dbToY = (db: number) => H - ((Math.max(floor, Math.min(0, db)) - floor) / (0 - floor)) * H;
    const peakY = dbToY(peakDb);
    const holdY = dbToY(holdDb);
    const fillH = H - peakY;

    return (
        <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 4 }}>
            <span style={{
                fontFamily: 'var(--font-sans, Inter, system-ui, sans-serif)',
                fontSize: 10, letterSpacing: 0.6, textTransform: 'uppercase',
                color: 'var(--fg-2, #b8bcc3)', fontWeight: 500,
            }}>{label}</span>
            <svg viewBox={`0 0 ${W} ${H}`} width={W} height={H}
                 style={{ background: 'var(--bg-inset, #0e1014)', border: '1px solid var(--line-1, #2a2c30)', borderRadius: 3 }}
                 role="meter" aria-label={`${label} ${peakDb.toFixed(1)} dBFS`}>
                <defs>
                    <linearGradient id={`mtr-${ngId}`} x1={0} y1={1} x2={0} y2={0}>
                        <stop offset="0%"   stopColor="var(--accent, #4a9eff)" />
                        <stop offset="70%"  stopColor="var(--accent, #4a9eff)" />
                        <stop offset="85%"  stopColor="var(--power, #ffc93a)" />
                        <stop offset="100%" stopColor="var(--tx, #e63a2b)" />
                    </linearGradient>
                </defs>
                {[-12, -24, -36, -48].map((db) => (
                    <line key={db} x1={2} y1={dbToY(db)} x2={W - 2} y2={dbToY(db)}
                          stroke="var(--line-1, #2a2c30)" strokeWidth={0.5} opacity={0.6} />
                ))}
                {fillH > 0 && <rect x={3} y={peakY} width={W - 6} height={fillH} fill={`url(#mtr-${ngId})`} />}
                {holdDb > floor + 1 && (
                    <line x1={2} y1={holdY} x2={W - 2} y2={holdY} stroke="var(--accent, #4a9eff)" strokeWidth={1.5} opacity={0.9} />
                )}
            </svg>
            <span style={{
                fontFamily: 'var(--font-mono, JetBrains Mono, ui-monospace, monospace)',
                fontSize: 10, color: 'var(--fg-1, #d6d8dc)', fontVariantNumeric: 'tabular-nums',
                minWidth: 36, textAlign: 'center',
            }}>{peakDb > -150 ? peakDb.toFixed(0) : '—'} dB</span>
        </div>
    );
}

// ---------------------------------------------------------------
// SVG rotary knob — same shape as Compressor / EQ (the chain-plugin
// convention).
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
}

function Knob({ label, value, min, max, defaultValue, unit, formatValue, logScale, onChange }: KnobProps) {
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
                    const active = deg <= angleDeg + 0.5;
                    return (
                        <line key={i} x1={x1} y1={y1} x2={x2} y2={y2}
                              stroke={active ? 'var(--accent, #4a9eff)' : 'var(--line-1, #2a2c30)'}
                              strokeWidth={1.5} strokeLinecap="round" />
                    );
                })}

                <defs>
                    <radialGradient id={`dial-exc-${label}`} cx="0.35" cy="0.35" r="0.75">
                        <stop offset="0%"  stopColor="var(--bg-2, #1f2226)" />
                        <stop offset="100%" stopColor="var(--bg-0, #0e1014)" />
                    </radialGradient>
                </defs>
                <circle cx={0} cy={0} r={R} fill={`url(#dial-exc-${label})`} stroke="var(--line-2, #3a3d42)" strokeWidth={1} />

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
// SaturationCurve — plot the tanh(drive · x) waveshaper that turns the
// high-passed signal into harmonics. Input X axis (-1 → +1), output Y
// axis (-1 → +1). Unity reference line + saturation S-curve in
// --accent blue with soft glow. As Drive increases, the curve gets
// steeper at low input and flatter at high input → more harmonic
// content gets synthesised.
// ---------------------------------------------------------------
function SaturationCurve({ driveDb }: { driveDb: number }) {
    const W = 240;
    const H = 200;

    const xOf = (x: number) => (x + 1) * 0.5 * W; // -1 → 0, +1 → W
    const yOf = (y: number) => H - (y + 1) * 0.5 * H;

    const driveLin = Math.pow(10, driveDb / 20);

    const pts: string[] = [];
    const N = 200;
    for (let i = 0; i <= N; i++) {
        const x = -1 + 2 * (i / N);
        const y = Math.tanh(driveLin * x);
        pts.push((i === 0 ? 'M' : 'L') + ' ' + xOf(x).toFixed(1) + ' ' + yOf(y).toFixed(1));
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
            aria-label="Saturation transfer function"
        >
            <defs>
                <filter id="exc-glow" x="-2%" y="-2%" width="104%" height="104%">
                    <feGaussianBlur stdDeviation="1.2" result="blur" />
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

            {/* Unity reference (y = x) */}
            <line x1={xOf(-1)} y1={yOf(-1)} x2={xOf(1)} y2={yOf(1)}
                  stroke="var(--fg-3, #5a5e66)" strokeDasharray="1 4" opacity={0.6} />

            {/* The saturation S-curve */}
            <path d={pts.join(' ')} stroke="var(--accent, #4a9eff)" strokeWidth={2.4} fill="none" filter="url(#exc-glow)" />

            {/* Axis labels */}
            <text x={4} y={H - 4} fill="var(--fg-3, #5a5e66)" fontSize={8} fontFamily="var(--font-mono, JetBrains Mono, ui-monospace, monospace)">IN</text>
            <text x={W - 14} y={12} fill="var(--fg-3, #5a5e66)" fontSize={8} fontFamily="var(--font-mono, JetBrains Mono, ui-monospace, monospace)">OUT</text>
        </svg>
    );
}

// ---------------------------------------------------------------
// Main panel.
// ---------------------------------------------------------------
function ExciterPanel({ api }: { api: ZeusPluginApi }) {
    const [params, setParams] = useState<ExciterParams>(DEFAULT_PARAMS);
    const [meters, setMeters] = useState<ExciterMeters>(DEFAULT_METERS);
    const lastPostedRef = useRef<ExciterParams>(DEFAULT_PARAMS);

    useEffect(() => {
        let active = true;
        (async () => {
            try {
                const res = await api.callBackend('GET', '/params');
                if (active && res.ok) {
                    const p = (await res.json()) as ExciterParams;
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
                if (active && res.ok) setMeters((await res.json()) as ExciterMeters);
            } catch { /* swallow */ }
        };
        const t = window.setInterval(tick, 66);
        tick();
        return () => { active = false; window.clearInterval(t); };
    }, [api]);

    const postDebounceRef = useRef<number | null>(null);
    const schedulePost = useCallback((next: ExciterParams) => {
        setParams(next);
        if (postDebounceRef.current != null) window.clearTimeout(postDebounceRef.current);
        postDebounceRef.current = window.setTimeout(() => {
            const last = lastPostedRef.current;
            const patch: Partial<ExciterParams> = {};
            if (next.freqHz   !== last.freqHz)   patch.freqHz   = next.freqHz;
            if (next.driveDb  !== last.driveDb)  patch.driveDb  = next.driveDb;
            if (next.mixPct   !== last.mixPct)   patch.mixPct   = next.mixPct;
            if (next.inputDb  !== last.inputDb)  patch.inputDb  = next.inputDb;
            if (next.outputDb !== last.outputDb) patch.outputDb = next.outputDb;
            if (next.bypass   !== last.bypass)   patch.bypass   = next.bypass;
            if (Object.keys(patch).length === 0) return;
            void api.callBackend('POST', '/params', patch).then(async (res) => {
                if (res.ok) {
                    const echoed = (await res.json()) as ExciterParams;
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
                }}>Aural Exciter</h3>

                <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
                    <span style={{
                        fontFamily: 'var(--font-mono, JetBrains Mono, ui-monospace, monospace)',
                        fontSize: 10,
                        color: 'var(--fg-2, #b8bcc3)',
                        letterSpacing: 0.5,
                    }}>
                        HARM {meters.harmonicLevelDb > -150 ? meters.harmonicLevelDb.toFixed(0) : '—'} · OUT {meters.outputPeakDb > -150 ? meters.outputPeakDb.toFixed(0) : '—'} dBFS
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
                <VerticalMeter label="IN" peakDb={meters.inputPeakDb} ngId="exc-in" />

                {/* Saturation curve */}
                <div style={{ flex: '1 1 240px', minWidth: 220, maxWidth: 340 }}>
                    <SaturationCurve driveDb={params.driveDb} />
                    <div style={{
                        fontFamily: 'var(--font-mono, JetBrains Mono, ui-monospace, monospace)',
                        fontSize: 10,
                        color: 'var(--fg-3, #5a5e66)',
                        marginTop: 4,
                        textAlign: 'center',
                    }}>
                        tanh(drive · x) — input above {params.freqHz >= 1000 ? (params.freqHz / 1000).toFixed(2) + ' kHz' : params.freqHz.toFixed(0) + ' Hz'} gets saturated
                    </div>
                </div>

                <VerticalMeter label="OUT" peakDb={meters.outputPeakDb} ngId="exc-out" />

                {/* Knob grid — 2×3 (Frequency / Drive / Mix on top, Input / Output / blank on bottom) */}
                <div style={{
                    display: 'grid',
                    gridTemplateColumns: 'repeat(3, minmax(68px, 1fr))',
                    gap: 12,
                    paddingTop: 4,
                }}>
                    <Knob
                        label="Frequency"
                        value={params.freqHz}
                        min={1000}
                        max={12000}
                        defaultValue={5000}
                        unit={params.freqHz >= 1000 ? 'kHz' : 'Hz'}
                        formatValue={(v) => v >= 1000 ? (v / 1000).toFixed(2) : v.toFixed(0)}
                        logScale
                        onChange={(v) => schedulePost({ ...params, freqHz: v })}
                    />
                    <Knob
                        label="Drive"
                        value={params.driveDb}
                        min={0}
                        max={36}
                        defaultValue={6}
                        unit="dB"
                        formatValue={(v) => v.toFixed(1)}
                        onChange={(v) => schedulePost({ ...params, driveDb: v })}
                    />
                    <Knob
                        label="Mix"
                        value={params.mixPct}
                        min={0}
                        max={100}
                        defaultValue={20}
                        unit="%"
                        formatValue={(v) => v.toFixed(0)}
                        onChange={(v) => schedulePost({ ...params, mixPct: v })}
                    />
                    <Knob
                        label="Input"
                        value={params.inputDb}
                        min={-24}
                        max={12}
                        defaultValue={0}
                        unit="dB"
                        formatValue={(v) => v >= 0 ? `+${v.toFixed(1)}` : v.toFixed(1)}
                        onChange={(v) => schedulePost({ ...params, inputDb: v })}
                    />
                    <Knob
                        label="Output"
                        value={params.outputDb}
                        min={-24}
                        max={12}
                        defaultValue={0}
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
        id: 'exciter.main',
        component: () => <ExciterPanel api={api} />,
    });
}

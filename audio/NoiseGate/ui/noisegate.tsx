// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus voice audio chain — Noise Gate UI module.
// Copyright (C) 2025-2026 KB2UKA and contributors.
//
// Centerpiece layout:
//
//   [ vertical IN meter ] [ threshold rail + GR strip + state pill ] [ vertical OUT meter ]
//
// The threshold rail is the operator's main read: a dBFS scale with the
// input peak rendered as a moving needle, the threshold drawn as a vertical
// marker, and an OPEN / HOLD / CLOSED state pill. Below the rail a small
// horizontal GR strip shows the gate's currently-applied attenuation —
// fills from the right as the gate closes (mirrors the convention used by
// the Compressor block's GR meter).
//
// Six knobs underneath: Threshold / Attack / Hold / Release / Range / Output.

import { useCallback, useEffect, useRef, useState } from 'react';

interface ZeusPluginApi {
    registerPanel(spec: { id: string; component: React.ComponentType }): void;
    callBackend(method: string, path: string, body?: unknown): Promise<Response>;
}

interface GateParams {
    thresholdDb: number;
    attackMs: number;
    holdMs: number;
    releaseMs: number;
    rangeDb: number;
    outputDb: number;
    bypass: boolean;
}

interface GateMeters {
    inputPeakDb: number;
    outputPeakDb: number;
    gainDb: number;
    state: number; // 0 = closed, 1 = hold, 2 = open
}

const DEFAULT_PARAMS: GateParams = {
    thresholdDb: -35,
    attackMs: 2,
    holdMs: 80,
    releaseMs: 150,
    rangeDb: -60,
    outputDb: 0,
    bypass: false,
};

const DEFAULT_METERS: GateMeters = {
    inputPeakDb: -200,
    outputPeakDb: -200,
    gainDb: 0,
    state: 0,
};

const METER_FLOOR_DB = -60; // scale floor for vertical meters + threshold rail

// ---------------------------------------------------------------
// Generic SVG rotary knob — same shape as the rest of the audio chain.
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
    bipolar?: boolean;
    onChange: (v: number) => void;
}

function Knob({ label, value, min, max, defaultValue, unit, formatValue, logScale, bipolar, onChange }: KnobProps) {
    const dragStartRef = useRef<{ y: number; startValue: number; fine: boolean } | null>(null);
    const knobRef = useRef<SVGSVGElement | null>(null);

    const valueToNorm = useCallback((v: number): number => {
        if (logScale) return (Math.log(Math.max(min, v)) - Math.log(min)) / (Math.log(max) - Math.log(min));
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
    const CENTER_DEG = 0;

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
                    <radialGradient id={`dial-ng-${label}`} cx="0.35" cy="0.35" r="0.75">
                        <stop offset="0%"  stopColor="var(--bg-2, #1f2226)" />
                        <stop offset="100%" stopColor="var(--bg-0, #0e1014)" />
                    </radialGradient>
                </defs>
                <circle cx={0} cy={0} r={R} fill={`url(#dial-ng-${label})`} stroke="var(--line-2, #3a3d42)" strokeWidth={1} />

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
// Vertical peak meter with peak-hold tick.
// ---------------------------------------------------------------
function VerticalMeter({ label, peakDb, accent }: { label: string; peakDb: number; accent: string }) {
    const W = 30;
    const H = 160;
    const floor = METER_FLOOR_DB;

    // Peak-hold: cache the recent maximum, decay linearly over ~1.5 s.
    const [holdDb, setHoldDb] = useState<number>(floor);
    const lastUpdateRef = useRef<number>(performance.now());
    useEffect(() => {
        const now = performance.now();
        const dt = (now - lastUpdateRef.current) / 1000;
        lastUpdateRef.current = now;
        // Decay 40 dB/s.
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
            <svg
                viewBox={`0 0 ${W} ${H}`}
                width={W}
                height={H}
                style={{
                    background: 'var(--bg-inset, #0e1014)',
                    border: '1px solid var(--line-1, #2a2c30)',
                    borderRadius: 3,
                }}
                role="meter"
                aria-label={`${label} ${peakDb.toFixed(1)} dBFS`}
                aria-valuenow={peakDb}
                aria-valuemin={floor}
                aria-valuemax={0}
            >
                <defs>
                    <linearGradient id={`mtr-${label}`} x1={0} y1={1} x2={0} y2={0}>
                        <stop offset="0%"  stopColor={accent} />
                        <stop offset="70%" stopColor={accent} />
                        <stop offset="85%" stopColor="var(--power, #ffc93a)" />
                        <stop offset="100%" stopColor="var(--tx, #e63a2b)" />
                    </linearGradient>
                </defs>

                {/* Tick marks every 12 dB (0, -12, -24, -36, -48) */}
                {[-12, -24, -36, -48].map((db) => (
                    <line key={db} x1={2} y1={dbToY(db)} x2={W - 2} y2={dbToY(db)}
                          stroke="var(--line-1, #2a2c30)" strokeWidth={0.5} opacity={0.6} />
                ))}

                {/* Filled bar */}
                {fillH > 0 && (
                    <rect x={3} y={peakY} width={W - 6} height={fillH}
                          fill={`url(#mtr-${label})`} />
                )}

                {/* Peak-hold tick */}
                {holdDb > floor + 1 && (
                    <line x1={2} y1={holdY} x2={W - 2} y2={holdY}
                          stroke="var(--accent, #4a9eff)" strokeWidth={1.5} opacity={0.9} />
                )}
            </svg>
            <span style={{
                fontFamily: 'var(--font-mono, JetBrains Mono, ui-monospace, monospace)',
                fontSize: 10, color: 'var(--fg-1, #d6d8dc)',
                fontVariantNumeric: 'tabular-nums', minWidth: 36, textAlign: 'center',
            }}>{peakDb > -150 ? peakDb.toFixed(0) : '—'} dB</span>
        </div>
    );
}

// ---------------------------------------------------------------
// Horizontal threshold rail + state pill + GR strip.
// ---------------------------------------------------------------
function ThresholdRail({
    inputPeakDb,
    thresholdDb,
    rangeDb,
    gainDb,
    state,
}: {
    inputPeakDb: number;
    thresholdDb: number;
    rangeDb: number;
    gainDb: number;
    state: number;
}) {
    const W = 280;
    const H = 60;
    const floor = METER_FLOOR_DB;

    const dbToX = (db: number) => ((Math.max(floor, Math.min(0, db)) - floor) / (0 - floor)) * W;

    const inputX = dbToX(inputPeakDb);
    const thrOpenX = dbToX(thresholdDb);
    const thrCloseX = dbToX(thresholdDb - 3);

    // GR strip: gainDb runs 0 (open, wide) to RangeDb (closed). Map to "how
    // far from unity we are" as a fraction.
    const gr = Math.max(0, Math.min(1, gainDb / Math.min(-0.01, rangeDb)));

    const stateLabel = state === 2 ? 'OPEN' : state === 1 ? 'HOLD' : 'CLOSED';
    const stateColor =
        state === 2 ? 'var(--accent, #4a9eff)' :
        state === 1 ? 'var(--power, #ffc93a)' :
                      'var(--fg-3, #5a5e66)';

    return (
        <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'stretch', gap: 6, flex: '1 1 auto', minWidth: 240 }}>
            {/* State pill */}
            <div style={{ display: 'flex', justifyContent: 'center', alignItems: 'center', gap: 8 }}>
                <span style={{
                    display: 'inline-block',
                    padding: '3px 12px',
                    fontFamily: 'var(--font-sans, Inter, system-ui, sans-serif)',
                    fontSize: 10,
                    fontWeight: 700,
                    letterSpacing: 2,
                    color: state === 0 ? 'var(--fg-1, #d6d8dc)' : '#0e1014',
                    background: stateColor,
                    border: `1px solid ${stateColor}`,
                    borderRadius: 12,
                    boxShadow: state !== 0 ? `0 0 8px ${stateColor}80` : 'none',
                    transition: 'background 80ms linear, box-shadow 80ms linear',
                }}>{stateLabel}</span>
            </div>

            {/* Threshold rail */}
            <svg viewBox={`0 0 ${W} ${H}`} width="100%" height={H} preserveAspectRatio="none"
                 style={{
                    background: 'var(--bg-inset, #0e1014)',
                    border: '1px solid var(--line-1, #2a2c30)',
                    borderRadius: 3,
                 }}
                 role="img" aria-label="Threshold rail">

                {/* Hysteresis band — between close and open thresholds */}
                <rect x={thrCloseX} y={0} width={thrOpenX - thrCloseX} height={H}
                      fill="var(--accent, #4a9eff)" opacity={0.08} />

                {/* Threshold markers */}
                <line x1={thrOpenX} y1={0} x2={thrOpenX} y2={H}
                      stroke="var(--accent, #4a9eff)" strokeWidth={1.5} opacity={0.9} />
                <line x1={thrCloseX} y1={0} x2={thrCloseX} y2={H}
                      stroke="var(--accent, #4a9eff)" strokeWidth={1} strokeDasharray="2 2" opacity={0.6} />

                {/* Tick scale below */}
                {[-60, -48, -36, -24, -12, 0].map((db) => (
                    <g key={db}>
                        <line x1={dbToX(db)} y1={H - 6} x2={dbToX(db)} y2={H} stroke="var(--fg-3, #5a5e66)" strokeWidth={0.6} />
                        <text x={dbToX(db) + 2} y={H - 8} fill="var(--fg-3, #5a5e66)" fontSize={7.5}
                              fontFamily="var(--font-mono, JetBrains Mono, ui-monospace, monospace)">{db}</text>
                    </g>
                ))}

                {/* Input peak needle */}
                <line x1={inputX} y1={2} x2={inputX} y2={H - 12}
                      stroke={state === 0 ? 'var(--fg-3, #5a5e66)' : 'var(--power, #ffc93a)'}
                      strokeWidth={2}
                      style={{ filter: state !== 0 ? 'drop-shadow(0 0 3px rgba(255,201,58,0.6))' : 'none' }} />
                <circle cx={inputX} cy={(H - 12) / 2 + 1} r={3}
                        fill={state === 0 ? 'var(--fg-3, #5a5e66)' : 'var(--power, #ffc93a)'} />
            </svg>

            {/* GR strip — fills from the right as the gate closes */}
            <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
                <span style={{
                    fontFamily: 'var(--font-sans, Inter, system-ui, sans-serif)',
                    fontSize: 9,
                    color: 'var(--fg-3, #5a5e66)',
                    letterSpacing: 0.8,
                    textTransform: 'uppercase',
                    width: 26,
                }}>GR</span>
                <div style={{
                    flex: 1, height: 8, background: 'var(--bg-inset, #0e1014)',
                    border: '1px solid var(--line-1, #2a2c30)', borderRadius: 2,
                    position: 'relative', overflow: 'hidden',
                }}>
                    <div style={{
                        position: 'absolute', right: 0, top: 0, bottom: 0,
                        width: `${gr * 100}%`,
                        background: 'var(--tx, #e63a2b)',
                        opacity: 0.85,
                        transition: 'width 80ms linear',
                    }} />
                </div>
                <span style={{
                    fontFamily: 'var(--font-mono, JetBrains Mono, ui-monospace, monospace)',
                    fontSize: 10, color: 'var(--fg-1, #d6d8dc)',
                    minWidth: 40, textAlign: 'right',
                    fontVariantNumeric: 'tabular-nums',
                }}>{gainDb < -0.5 ? gainDb.toFixed(0) : '0'} dB</span>
            </div>
        </div>
    );
}

// ---------------------------------------------------------------
// Main panel.
// ---------------------------------------------------------------
function NoiseGatePanel({ api }: { api: ZeusPluginApi }) {
    const [params, setParams] = useState<GateParams>(DEFAULT_PARAMS);
    const [meters, setMeters] = useState<GateMeters>(DEFAULT_METERS);
    const lastPostedRef = useRef<GateParams>(DEFAULT_PARAMS);

    useEffect(() => {
        let active = true;
        (async () => {
            try {
                const res = await api.callBackend('GET', '/params');
                if (active && res.ok) {
                    const p = (await res.json()) as GateParams;
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
                if (active && res.ok) setMeters((await res.json()) as GateMeters);
            } catch { /* swallow */ }
        };
        const t = window.setInterval(tick, 66);
        tick();
        return () => { active = false; window.clearInterval(t); };
    }, [api]);

    const postDebounceRef = useRef<number | null>(null);
    const schedulePost = useCallback((next: GateParams) => {
        setParams(next);
        if (postDebounceRef.current != null) window.clearTimeout(postDebounceRef.current);
        postDebounceRef.current = window.setTimeout(() => {
            const last = lastPostedRef.current;
            const patch: Partial<GateParams> = {};
            if (next.thresholdDb !== last.thresholdDb) patch.thresholdDb = next.thresholdDb;
            if (next.attackMs    !== last.attackMs)    patch.attackMs    = next.attackMs;
            if (next.holdMs      !== last.holdMs)      patch.holdMs      = next.holdMs;
            if (next.releaseMs   !== last.releaseMs)   patch.releaseMs   = next.releaseMs;
            if (next.rangeDb     !== last.rangeDb)     patch.rangeDb     = next.rangeDb;
            if (next.outputDb    !== last.outputDb)    patch.outputDb    = next.outputDb;
            if (next.bypass      !== last.bypass)      patch.bypass      = next.bypass;
            if (Object.keys(patch).length === 0) return;
            void api.callBackend('POST', '/params', patch).then(async (res) => {
                if (res.ok) {
                    const echoed = (await res.json()) as GateParams;
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
                }}>Noise Gate</h3>

                <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
                    <span style={{
                        fontFamily: 'var(--font-mono, JetBrains Mono, ui-monospace, monospace)',
                        fontSize: 10,
                        color: 'var(--fg-2, #b8bcc3)',
                        letterSpacing: 0.5,
                    }}>
                        IN {meters.inputPeakDb > -150 ? meters.inputPeakDb.toFixed(0) : '—'} ·
                        GR {meters.gainDb < -0.5 ? meters.gainDb.toFixed(0) : '0'} ·
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

            {/* Centerpiece — vertical IN meter, threshold rail, vertical OUT meter */}
            <div style={{
                display: 'flex',
                gap: 12,
                alignItems: 'center',
                justifyContent: 'center',
                opacity: params.bypass ? 0.45 : 1,
                transition: 'opacity 160ms ease-out',
            }}>
                <VerticalMeter label="IN" peakDb={meters.inputPeakDb} accent="var(--accent, #4a9eff)" />
                <ThresholdRail
                    inputPeakDb={meters.inputPeakDb}
                    thresholdDb={params.thresholdDb}
                    rangeDb={params.rangeDb}
                    gainDb={meters.gainDb}
                    state={params.bypass ? 2 : meters.state}
                />
                <VerticalMeter label="OUT" peakDb={meters.outputPeakDb} accent="var(--accent, #4a9eff)" />
            </div>

            {/* Knob grid — 2 rows × 3 columns */}
            <div style={{
                display: 'grid',
                gridTemplateColumns: 'repeat(3, minmax(68px, 1fr))',
                gap: 12,
                paddingTop: 4,
                opacity: params.bypass ? 0.45 : 1,
                transition: 'opacity 160ms ease-out',
                pointerEvents: params.bypass ? 'none' : 'auto',
            }}>
                <Knob
                    label="Threshold"
                    value={params.thresholdDb}
                    min={-80}
                    max={0}
                    defaultValue={-35}
                    unit="dB"
                    formatValue={(v) => v.toFixed(0)}
                    onChange={(v) => schedulePost({ ...params, thresholdDb: v })}
                />
                <Knob
                    label="Attack"
                    value={params.attackMs}
                    min={0.1}
                    max={50}
                    defaultValue={2}
                    unit="ms"
                    formatValue={(v) => v < 10 ? v.toFixed(1) : v.toFixed(0)}
                    logScale
                    onChange={(v) => schedulePost({ ...params, attackMs: v })}
                />
                <Knob
                    label="Hold"
                    value={params.holdMs}
                    min={0}
                    max={500}
                    defaultValue={80}
                    unit="ms"
                    formatValue={(v) => v.toFixed(0)}
                    onChange={(v) => schedulePost({ ...params, holdMs: v })}
                />
                <Knob
                    label="Release"
                    value={params.releaseMs}
                    min={5}
                    max={1000}
                    defaultValue={150}
                    unit="ms"
                    formatValue={(v) => v < 100 ? v.toFixed(0) : v.toFixed(0)}
                    logScale
                    onChange={(v) => schedulePost({ ...params, releaseMs: v })}
                />
                <Knob
                    label="Range"
                    value={params.rangeDb}
                    min={-80}
                    max={0}
                    defaultValue={-60}
                    unit="dB"
                    formatValue={(v) => v.toFixed(0)}
                    onChange={(v) => schedulePost({ ...params, rangeDb: v })}
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

            <footer style={{
                fontFamily: 'var(--font-sans, Inter, system-ui, sans-serif)',
                fontSize: 10,
                color: 'var(--fg-3, #5a5e66)',
                textAlign: 'right',
                paddingRight: 4,
            }}>
                Drag knobs to adjust · shift-drag fine · double-click reset · 3 dB hysteresis built in
            </footer>
        </section>
    );
}

// ---------------------------------------------------------------
export default function register(api: ZeusPluginApi) {
    api.registerPanel({
        id: 'noisegate.main',
        component: () => <NoiseGatePanel api={api} />,
    });
}

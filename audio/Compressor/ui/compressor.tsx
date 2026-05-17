// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus voice audio chain v1 — Compressor UI module.
// Copyright (C) 2025-2026 KB2UKA and contributors.
//
// AetherVoice-inspired layout translated to Zeus tokens
// (per project_audio_chain_visual_direction memory): brass-instrument-plate
// header, centerpiece SVG transfer-function curve, SVG rotary knobs with
// drag-to-rotate, and segmented vertical LED meters with peak hold. All
// colours via tokens.css CSS variables; no raw hex.

import { useCallback, useEffect, useRef, useState } from 'react';

// ---------------------------------------------------------------
// ZeusPluginApi contract — same shape the Amplifier + AntennaGenius
// samples use. registerPanel + callBackend, no more.
// ---------------------------------------------------------------
interface ZeusPluginApi {
    registerPanel(spec: { id: string; component: React.ComponentType }): void;
    callBackend(method: string, path: string, body?: unknown): Promise<Response>;
}

// ---------------------------------------------------------------
// REST wire shape — mirrors CompressorParamsDto / CompressorMetersDto
// from CompressorPlugin.cs.
// ---------------------------------------------------------------
interface CompressorParams {
    thresholdDb: number;
    ratio: number;
    attackMs: number;
    releaseMs: number;
    kneeDb: number;
    makeupDb: number;
    bypass: boolean;
}

interface CompressorMeters {
    inputPeakDb: number;
    outputPeakDb: number;
    gainReductionDb: number;
}

const DEFAULT_PARAMS: CompressorParams = {
    thresholdDb: -18,
    ratio: 3,
    attackMs: 5,
    releaseMs: 100,
    kneeDb: 6,
    makeupDb: 0,
    bypass: false,
};

const DEFAULT_METERS: CompressorMeters = {
    inputPeakDb: -200,
    outputPeakDb: -200,
    gainReductionDb: 0,
};

// ---------------------------------------------------------------
// Knob — SVG rotary control with drag-to-rotate. Pointer line sweeps
// from -135° (min) to +135° (max), 270° arc total — matches the
// industry convention for audio rotary knobs. Drag vertically to
// change: dragging UP increases value, DOWN decreases. Shift+drag
// for fine adjustment (10× slower). Double-click resets to default.
//
// Realtime safety: this is a pure UI control. State lives in React;
// the operator's value pushes through callBackend at the parent's
// debounce cadence.
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

    // Map value → [0, 1] (linear or log).
    const valueToNorm = (v: number): number => {
        if (logScale) {
            const ln = Math.log(Math.max(min, v));
            return (ln - Math.log(min)) / (Math.log(max) - Math.log(min));
        }
        return (v - min) / (max - min);
    };

    const normToValue = (n: number): number => {
        const clamped = Math.max(0, Math.min(1, n));
        if (logScale) {
            return Math.exp(Math.log(min) + clamped * (Math.log(max) - Math.log(min)));
        }
        return min + clamped * (max - min);
    };

    const norm = valueToNorm(value);
    // 270° arc, centred at top (-90° from horizontal in SVG space).
    // Knob pointer angle in degrees, where -135 = leftmost, +135 = rightmost.
    const angleDeg = -135 + norm * 270;

    const onPointerDown = useCallback((e: React.PointerEvent<SVGSVGElement>) => {
        e.preventDefault();
        knobRef.current?.setPointerCapture(e.pointerId);
        dragStartRef.current = { y: e.clientY, startValue: value, fine: e.shiftKey };
    }, [value]);

    const onPointerMove = useCallback((e: React.PointerEvent<SVGSVGElement>) => {
        if (!dragStartRef.current) return;
        const dy = dragStartRef.current.y - e.clientY; // up = positive
        const sensitivity = dragStartRef.current.fine ? 0.0005 : 0.005;
        const startNorm = valueToNorm(dragStartRef.current.startValue);
        const next = normToValue(startNorm + dy * sensitivity);
        onChange(next);
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [onChange]);

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
    }, [value, onChange]);

    const display = formatValue ? formatValue(value) : value.toFixed(2);

    // Tick marks every 30° around the 270° arc, lighter outside the active range.
    const ticks: number[] = [];
    for (let i = 0; i <= 9; i++) {
        const t = i / 9;
        ticks.push(-135 + t * 270);
    }

    const R = 22;          // dial radius
    const TICK_OUTER = 24;
    const TICK_INNER = 26;
    const POINTER_LEN = 17;

    return (
        <div style={{
            display: 'flex',
            flexDirection: 'column',
            alignItems: 'center',
            gap: 4,
            minWidth: 64,
            userSelect: 'none',
        }}>
            <span style={{
                fontFamily: 'var(--font-sans, Inter, system-ui, sans-serif)',
                fontSize: 10,
                letterSpacing: 1,
                textTransform: 'uppercase',
                color: 'var(--fg-2, #b8bcc3)',
                fontWeight: 500,
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
                style={{
                    cursor: 'ns-resize',
                    touchAction: 'none',
                    filter: 'drop-shadow(0 1px 2px rgba(0,0,0,0.45))',
                }}
                aria-label={`${label} ${display} ${unit}. Drag to adjust, shift-drag for fine, double-click to reset.`}
                role="slider"
                aria-valuenow={value}
                aria-valuemin={min}
                aria-valuemax={max}
            >
                {/* Tick ring */}
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
                            strokeWidth={1.5}
                            strokeLinecap="round"
                        />
                    );
                })}

                {/* Dial body — subtle gradient for depth */}
                <defs>
                    <radialGradient id={`dial-${label}`} cx="0.35" cy="0.35" r="0.75">
                        <stop offset="0%" stopColor="var(--bg-2, #1f2226)" />
                        <stop offset="100%" stopColor="var(--bg-0, #0e1014)" />
                    </radialGradient>
                </defs>
                <circle cx={0} cy={0} r={R} fill={`url(#dial-${label})`} stroke="var(--line-2, #3a3d42)" strokeWidth={1} />

                {/* Pointer line — accent blue, full length to indicator dot */}
                {(() => {
                    const rad = (angleDeg - 90) * Math.PI / 180;
                    const x = POINTER_LEN * Math.cos(rad);
                    const y = POINTER_LEN * Math.sin(rad);
                    return (
                        <>
                            <line
                                x1={0} y1={0} x2={x} y2={y}
                                stroke="var(--accent, #4a9eff)"
                                strokeWidth={2.5}
                                strokeLinecap="round"
                            />
                            <circle cx={x} cy={y} r={2.5} fill="var(--accent, #4a9eff)" />
                        </>
                    );
                })()}
            </svg>

            <span style={{
                fontFamily: 'var(--font-mono, JetBrains Mono, ui-monospace, monospace)',
                fontSize: 11,
                color: 'var(--fg-0, #e8eaed)',
                fontVariantNumeric: 'tabular-nums',
                lineHeight: 1,
            }}>{display}{unit ? ` ${unit}` : ''}</span>
        </div>
    );
}

// ---------------------------------------------------------------
// VMeter — segmented vertical LED-style meter with peak hold.
// 20 segments split 0..1, lit when value >= segment threshold.
// Peak-hold tick decays 1 dB/sec after a peak is captured.
// Gradient: accent (low) → power (mid) → tx (clip), or all-tx for GR.
// ---------------------------------------------------------------
interface VMeterProps {
    label: string;
    valueDb: number;
    minDb: number;
    maxDb: number;
    invert?: boolean; // GR fills downward
    grStyle?: boolean; // pure --tx gradient for GR meter
}

function VMeter({ label, valueDb, minDb, maxDb, invert, grStyle }: VMeterProps) {
    const SEGMENTS = 20;
    const [peakHold, setPeakHold] = useState<number>(valueDb);
    const peakHoldRef = useRef<{ value: number; t: number }>({ value: valueDb, t: performance.now() });

    // Peak-hold decay: 1 dB / 100 ms after the peak was captured.
    useEffect(() => {
        if (valueDb > peakHoldRef.current.value) {
            peakHoldRef.current = { value: valueDb, t: performance.now() };
            setPeakHold(valueDb);
        }
        const interval = setInterval(() => {
            const dt = (performance.now() - peakHoldRef.current.t) / 1000;
            const decayed = peakHoldRef.current.value - dt * 8; // 8 dB/sec
            if (decayed < valueDb) {
                peakHoldRef.current = { value: valueDb, t: performance.now() };
                setPeakHold(valueDb);
            } else {
                setPeakHold(decayed);
            }
        }, 50);
        return () => clearInterval(interval);
    }, [valueDb]);

    const clamped = Math.max(minDb, Math.min(maxDb, valueDb));
    const range = maxDb - minDb;
    const valueNorm = (clamped - minDb) / range;
    const peakNorm = Math.max(0, Math.min(1, (Math.max(minDb, Math.min(maxDb, peakHold)) - minDb) / range));

    // Color per segment — for IN/OUT meters: accent → power → tx
    // For GR: all --tx (gain reduction is "trouble", always warm)
    const segColor = (segNorm: number): string => {
        if (grStyle) return 'var(--tx, #e63a2b)';
        if (segNorm < 0.6) return 'var(--accent, #4a9eff)';
        if (segNorm < 0.85) return 'var(--power, #ffc93a)';
        return 'var(--tx, #e63a2b)';
    };

    return (
        <div style={{
            display: 'flex',
            flexDirection: 'column',
            alignItems: 'center',
            gap: 2,
            width: 24,
        }}>
            <span style={{
                fontFamily: 'var(--font-sans, Inter, system-ui, sans-serif)',
                fontSize: 9,
                letterSpacing: 0.6,
                textTransform: 'uppercase',
                color: 'var(--fg-2, #b8bcc3)',
            }}>{label}</span>

            <div style={{
                position: 'relative',
                width: 14,
                height: 132,
                background: 'var(--bg-inset, #0e1014)',
                borderRadius: 3,
                padding: 1,
                border: '1px solid var(--line-1, #2a2c30)',
                display: 'flex',
                flexDirection: invert ? 'column' : 'column-reverse',
                gap: 1,
            }}>
                {Array.from({ length: SEGMENTS }, (_, i) => {
                    // i=0 is bottom (or top if invert)
                    const segNorm = (i + 0.5) / SEGMENTS;
                    const lit = segNorm <= valueNorm;
                    const atPeak = Math.abs(segNorm - peakNorm) < 1 / SEGMENTS / 2 && valueNorm > 0.02;
                    const segHeight = (132 - 2 - (SEGMENTS - 1)) / SEGMENTS;
                    return (
                        <div key={i} style={{
                            height: segHeight,
                            background: lit ? segColor(segNorm) : 'var(--bg-1, #14161a)',
                            opacity: lit ? 1 : 0.6,
                            outline: atPeak ? '1px solid var(--amber, #FFA028)' : undefined,
                            transition: 'opacity 30ms linear',
                        }} />
                    );
                })}
            </div>

            <span style={{
                fontFamily: 'var(--font-mono, JetBrains Mono, ui-monospace, monospace)',
                fontSize: 9,
                color: 'var(--fg-0, #e8eaed)',
                lineHeight: 1,
                fontVariantNumeric: 'tabular-nums',
            }}>{valueDb <= -150 ? '—' : valueDb.toFixed(0)}</span>
        </div>
    );
}

// ---------------------------------------------------------------
// TransferCurve — input dB → output dB plot of the compressor's
// transfer function. Unity reference line dim, threshold marker in
// --power dashed, the function itself in --accent blue with a soft
// gradient fill below. Live operating-point dot in --amber.
// ---------------------------------------------------------------
function TransferCurve({ params, meters }: { params: CompressorParams; meters: CompressorMeters }) {
    const minDb = -60;
    const maxDb = 0;
    const range = maxDb - minDb;
    const W = 260;
    const H = 200;

    const xOf = (db: number) => ((db - minDb) / range) * W;
    const yOf = (db: number) => H - ((db - minDb) / range) * H;

    // Sample the transfer function densely.
    const curvePoints: string[] = [];
    const slopeFactor = 1 - 1 / Math.max(1.001, params.ratio);
    const halfKnee = params.kneeDb * 0.5;

    for (let i = 0; i <= 240; i++) {
        const inDb = minDb + (i / 240) * range;
        const over = inDb - params.thresholdDb;
        let grDb: number;
        if (over <= -halfKnee) grDb = 0;
        else if (over >= halfKnee || params.kneeDb <= 0) grDb = over * slopeFactor;
        else {
            const t = (over + halfKnee) / params.kneeDb;
            grDb = slopeFactor * (over + halfKnee) * t * 0.5;
        }
        const outDb = Math.max(minDb, inDb + params.makeupDb - grDb);
        const cmd = i === 0 ? 'M' : 'L';
        curvePoints.push(`${cmd} ${xOf(inDb).toFixed(1)} ${yOf(outDb).toFixed(1)}`);
    }

    const curvePath = curvePoints.join(' ');
    const fillPath = `${curvePath} L ${xOf(maxDb)} ${H} L ${xOf(minDb)} ${H} Z`;
    const unityPath = `M ${xOf(minDb)} ${yOf(minDb)} L ${xOf(maxDb)} ${yOf(maxDb)}`;

    const threshX = xOf(params.thresholdDb);
    const opIn = Math.max(minDb, Math.min(maxDb, meters.inputPeakDb));
    const opOut = Math.max(minDb, Math.min(maxDb, meters.outputPeakDb));
    const showOp = meters.inputPeakDb > minDb;

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
            aria-label="Compressor transfer function"
        >
            <defs>
                <linearGradient id="cmp-fill" x1="0" y1={H} x2="0" y2="0" gradientUnits="userSpaceOnUse">
                    <stop offset="0%"  stopColor="var(--accent, #4a9eff)" stopOpacity={0.0} />
                    <stop offset="60%" stopColor="var(--accent, #4a9eff)" stopOpacity={0.10} />
                    <stop offset="100%" stopColor="var(--accent, #4a9eff)" stopOpacity={0.22} />
                </linearGradient>
                <filter id="cmp-curve-glow" x="-2%" y="-2%" width="104%" height="104%">
                    <feGaussianBlur stdDeviation="1.2" result="blur" />
                    <feMerge>
                        <feMergeNode in="blur" />
                        <feMergeNode in="SourceGraphic" />
                    </feMerge>
                </filter>
            </defs>

            {/* Grid every 12 dB */}
            {[-48, -36, -24, -12].map((db) => (
                <g key={db}>
                    <line x1={xOf(db)} y1={0} x2={xOf(db)} y2={H} stroke="var(--line-1, #2a2c30)" strokeDasharray="2 3" opacity={0.6} />
                    <line x1={0} y1={yOf(db)} x2={W} y2={yOf(db)} stroke="var(--line-1, #2a2c30)" strokeDasharray="2 3" opacity={0.6} />
                </g>
            ))}

            {/* Unity reference line */}
            <path d={unityPath} stroke="var(--fg-3, #5a5e66)" strokeWidth={1} fill="none" strokeDasharray="1 4" />

            {/* Threshold marker (vertical line + label) */}
            <line x1={threshX} y1={0} x2={threshX} y2={H} stroke="var(--power, #ffc93a)" strokeWidth={1} strokeDasharray="3 4" opacity={0.75} />
            <text x={threshX + 3} y={12} fill="var(--power, #ffc93a)" fontSize={9} fontFamily="var(--font-mono, JetBrains Mono, ui-monospace, monospace)" opacity={0.85}>
                THR {params.thresholdDb.toFixed(0)}
            </text>

            {/* Gradient fill under curve */}
            <path d={fillPath} fill="url(#cmp-fill)" />

            {/* Transfer function — weighty + glow */}
            <path d={curvePath} stroke="var(--accent, #4a9eff)" strokeWidth={2.4} fill="none" filter="url(#cmp-curve-glow)" />

            {/* Live operating point */}
            {showOp && (
                <g>
                    <circle cx={xOf(opIn)} cy={yOf(opOut)} r={6} fill="var(--amber, #FFA028)" opacity={0.25} />
                    <circle cx={xOf(opIn)} cy={yOf(opOut)} r={3.5} fill="var(--amber, #FFA028)" stroke="var(--bg-0, #0e1014)" strokeWidth={1} />
                </g>
            )}

            {/* Axis labels */}
            <text x={4} y={H - 4} fill="var(--fg-3, #5a5e66)" fontSize={8} fontFamily="var(--font-mono, JetBrains Mono, ui-monospace, monospace)">IN dBFS</text>
            <text x={W - 36} y={12} fill="var(--fg-3, #5a5e66)" fontSize={8} fontFamily="var(--font-mono, JetBrains Mono, ui-monospace, monospace)">OUT dBFS</text>
        </svg>
    );
}

// ---------------------------------------------------------------
// Main panel.
// ---------------------------------------------------------------
function CompressorPanel({ api }: { api: ZeusPluginApi }) {
    const [params, setParams] = useState<CompressorParams>(DEFAULT_PARAMS);
    const [meters, setMeters] = useState<CompressorMeters>(DEFAULT_METERS);
    const lastPostedRef = useRef<CompressorParams>(DEFAULT_PARAMS);

    // Hydrate params on first mount.
    useEffect(() => {
        let active = true;
        (async () => {
            try {
                const res = await api.callBackend('GET', '/params');
                if (active && res.ok) {
                    const p = await res.json() as CompressorParams;
                    setParams(p);
                    lastPostedRef.current = p;
                }
            } catch { /* swallow — display defaults */ }
        })();
        return () => { active = false; };
    }, [api]);

    // Poll meters at ~15 Hz.
    useEffect(() => {
        let active = true;
        const tick = async () => {
            try {
                const res = await api.callBackend('GET', '/meters');
                if (active && res.ok) {
                    const m = await res.json() as CompressorMeters;
                    setMeters(m);
                }
            } catch { /* network blip — keep last value */ }
        };
        const t = window.setInterval(tick, 66);
        tick();
        return () => { active = false; window.clearInterval(t); };
    }, [api]);

    // Debounced POST.
    const postDebounceRef = useRef<number | null>(null);
    const schedulePost = useCallback((next: CompressorParams) => {
        setParams(next);
        if (postDebounceRef.current != null) window.clearTimeout(postDebounceRef.current);
        postDebounceRef.current = window.setTimeout(() => {
            const last = lastPostedRef.current;
            const patch: Partial<CompressorParams> = {};
            if (next.thresholdDb !== last.thresholdDb) patch.thresholdDb = next.thresholdDb;
            if (next.ratio       !== last.ratio)       patch.ratio       = next.ratio;
            if (next.attackMs    !== last.attackMs)    patch.attackMs    = next.attackMs;
            if (next.releaseMs   !== last.releaseMs)   patch.releaseMs   = next.releaseMs;
            if (next.kneeDb      !== last.kneeDb)      patch.kneeDb      = next.kneeDb;
            if (next.makeupDb    !== last.makeupDb)    patch.makeupDb    = next.makeupDb;
            if (next.bypass      !== last.bypass)      patch.bypass      = next.bypass;
            if (Object.keys(patch).length === 0) return;
            void api.callBackend('POST', '/params', patch).then(async (res) => {
                if (res.ok) {
                    const echoed = await res.json() as CompressorParams;
                    lastPostedRef.current = echoed;
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
            {/* Brass-instrument-plate header with bypass toggle */}
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
                }}>Compressor</h3>

                <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
                    <span style={{
                        fontFamily: 'var(--font-mono, JetBrains Mono, ui-monospace, monospace)',
                        fontSize: 10,
                        color: 'var(--fg-2, #b8bcc3)',
                        letterSpacing: 0.5,
                    }}>TX · pre-CFC</span>

                    {/* Bypass toggle — every Zeus audio-chain plugin exposes this
                        same control at the same position so operators can A/B
                        any block in the chain identically. Active state in --tx
                        red so it's unmistakable on the brass plate. */}
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
                            background: params.bypass
                                ? 'var(--tx, #e63a2b)'
                                : 'var(--bg-2, #1f2226)',
                            border: '1px solid ' + (params.bypass ? 'var(--tx, #e63a2b)' : 'var(--line-2, #3a3d42)'),
                            borderRadius: 3,
                            cursor: 'pointer',
                            boxShadow: params.bypass
                                ? '0 0 8px rgba(230, 58, 43, 0.5), inset 0 1px 0 rgba(255, 255, 255, 0.15)'
                                : 'inset 0 1px 0 rgba(255, 255, 255, 0.04)',
                            transition: 'all 120ms ease-out',
                        }}
                    >
                        {params.bypass ? 'Bypassed' : 'Bypass'}
                    </button>
                </div>
            </header>

            <div style={{
                display: 'flex',
                gap: 14,
                alignItems: 'stretch',
                flexWrap: 'wrap',
                opacity: params.bypass ? 0.45 : 1,
                transition: 'opacity 160ms ease-out',
                pointerEvents: params.bypass ? 'none' : 'auto',
            }}>
                {/* Left knob column */}
                <div style={{ display: 'flex', flexDirection: 'column', gap: 12, paddingTop: 4 }}>
                    <Knob label="Threshold" value={params.thresholdDb} min={-60} max={0}  defaultValue={-18} step={0.5} unit="dB" onChange={(v) => schedulePost({ ...params, thresholdDb: v })} formatValue={(v) => v.toFixed(1)} />
                    <Knob label="Ratio"     value={params.ratio}       min={1}   max={20} defaultValue={3}   step={0.1} unit=":1" onChange={(v) => schedulePost({ ...params, ratio: v })}      formatValue={(v) => v.toFixed(1)} logScale />
                    <Knob label="Knee"      value={params.kneeDb}      min={0}   max={24} defaultValue={6}   step={0.5} unit="dB" onChange={(v) => schedulePost({ ...params, kneeDb: v })}     formatValue={(v) => v.toFixed(1)} />
                </div>

                {/* Center: transfer curve, flexes to fill */}
                <div style={{ flex: '1 1 280px', minWidth: 240, maxWidth: 360 }}>
                    <TransferCurve params={params} meters={meters} />
                </div>

                {/* Right knob column */}
                <div style={{ display: 'flex', flexDirection: 'column', gap: 12, paddingTop: 4 }}>
                    <Knob label="Attack"  value={params.attackMs}  min={0.1} max={100}  defaultValue={5}   step={0.1} unit="ms" onChange={(v) => schedulePost({ ...params, attackMs:  v })} formatValue={(v) => v.toFixed(1)} logScale />
                    <Knob label="Release" value={params.releaseMs} min={1}   max={2000} defaultValue={100} step={1}   unit="ms" onChange={(v) => schedulePost({ ...params, releaseMs: v })} formatValue={(v) => v.toFixed(0)} logScale />
                    <Knob label="Makeup"  value={params.makeupDb}  min={0}   max={24}   defaultValue={0}   step={0.5} unit="dB" onChange={(v) => schedulePost({ ...params, makeupDb:  v })} formatValue={(v) => v.toFixed(1)} />
                </div>

                {/* Meters */}
                <div style={{ display: 'flex', gap: 6, paddingTop: 4 }}>
                    <VMeter label="IN"  valueDb={meters.inputPeakDb}     minDb={-60} maxDb={0} />
                    <VMeter label="OUT" valueDb={meters.outputPeakDb}    minDb={-60} maxDb={0} />
                    <VMeter label="GR"  valueDb={meters.gainReductionDb} minDb={0}  maxDb={24} invert grStyle />
                </div>
            </div>

            {/* Footer hint — tucked subtle */}
            <footer style={{
                fontFamily: 'var(--font-sans, Inter, system-ui, sans-serif)',
                fontSize: 10,
                color: 'var(--fg-3, #5a5e66)',
                textAlign: 'right',
                paddingRight: 4,
            }}>
                Drag knobs to adjust · shift-drag fine · double-click reset · wheel = ±1 % · {meters.inputPeakDb > -150 ? `peak ${meters.inputPeakDb.toFixed(1)} in / ${meters.outputPeakDb.toFixed(1)} out / ${meters.gainReductionDb.toFixed(1)} GR` : 'idle'}
            </footer>
        </section>
    );
}

// ---------------------------------------------------------------
// Module entry.
// ---------------------------------------------------------------
export default function register(api: ZeusPluginApi) {
    api.registerPanel({
        id: 'compressor.main',
        component: () => <CompressorPanel api={api} />,
    });
}

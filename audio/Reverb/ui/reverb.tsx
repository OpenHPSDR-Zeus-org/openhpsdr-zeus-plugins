// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus voice audio chain v1 — Reverb UI module.
// Copyright (C) 2025-2026 KB2UKA and contributors.
//
// Centerpiece: SVG impulse-response illustration — a dry transient at t=0
// followed by a pre-delay gap, then a decaying envelope of early reflections
// and a smooth diffuse tail. Pre-delay = gap width, Size = density of early
// reflections, Decay = envelope length, Damp = how fast the high frequencies
// disappear (rendered as a brightness gradient front-to-back), Mix = wet
// amplitude. Five rotary knobs. Brass-plate header with Bypass.

import { useCallback, useEffect, useRef, useState } from 'react';

interface ZeusPluginApi {
    registerPanel(spec: { id: string; component: React.ComponentType }): void;
    callBackend(method: string, path: string, body?: unknown): Promise<Response>;
}

interface ReverbParams {
    sizePct: number;
    decayPct: number;
    dampPct: number;
    preDelayMs: number;
    mixPct: number;
    bypass: boolean;
}

interface ReverbMeters {
    inputPeakDb: number;
    outputPeakDb: number;
    wetLevelDb: number;
}

const DEFAULT_PARAMS: ReverbParams = {
    sizePct: 50,
    decayPct: 40,
    dampPct: 50,
    preDelayMs: 15,
    mixPct: 12,
    bypass: false,
};

const DEFAULT_METERS: ReverbMeters = { inputPeakDb: -200, outputPeakDb: -200, wetLevelDb: -200 };

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
        if (logScale) return (Math.log(Math.max(min, v)) - Math.log(Math.max(1e-6, min))) / (Math.log(max) - Math.log(Math.max(1e-6, min)));
        return (v - min) / (max - min);
    }, [logScale, min, max]);

    const normToValue = useCallback((n: number): number => {
        const c = Math.max(0, Math.min(1, n));
        if (logScale) return Math.exp(Math.log(Math.max(1e-6, min)) + c * (Math.log(max) - Math.log(Math.max(1e-6, min))));
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
                role="slider" aria-valuenow={value} aria-valuemin={min} aria-valuemax={max}
            >
                {ticks.map((deg, i) => {
                    const rad = (deg - 90) * Math.PI / 180;
                    const isActive = deg <= angleDeg + 0.5;
                    return (
                        <line key={i}
                              x1={26 * Math.cos(rad)} y1={26 * Math.sin(rad)}
                              x2={24 * Math.cos(rad)} y2={24 * Math.sin(rad)}
                              stroke={isActive ? 'var(--accent, #4a9eff)' : 'var(--line-1, #2a2c30)'}
                              strokeWidth={1.5} strokeLinecap="round" />
                    );
                })}
                <defs>
                    <radialGradient id={`dial-reverb-${label}`} cx="0.35" cy="0.35" r="0.75">
                        <stop offset="0%" stopColor="var(--bg-2, #1f2226)" />
                        <stop offset="100%" stopColor="var(--bg-0, #0e1014)" />
                    </radialGradient>
                </defs>
                <circle cx={0} cy={0} r={22} fill={`url(#dial-reverb-${label})`} stroke="var(--line-2, #3a3d42)" strokeWidth={1} />
                {(() => {
                    const rad = (angleDeg - 90) * Math.PI / 180;
                    const x = 17 * Math.cos(rad);
                    const y = 17 * Math.sin(rad);
                    return (<>
                        <line x1={0} y1={0} x2={x} y2={y} stroke="var(--accent, #4a9eff)" strokeWidth={2.5} strokeLinecap="round" />
                        <circle cx={x} cy={y} r={2.5} fill="var(--accent, #4a9eff)" />
                    </>);
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
// ImpulseResponseViz — illustrates the reverb shape in the time domain.
// • Dry click at t=0 in --fg-1 (the operator's transient).
// • Pre-delay gap: empty space before any reflections.
// • Early reflections: sparse vertical lines whose density scales with Size.
// • Diffuse tail: exponential decay envelope whose length scales with Decay
//   and whose front-to-back colour fades from --accent blue toward
//   --fg-3 grey as Damp increases (higher damp = HF disappears faster).
// • Wet amplitude scales with Mix.
// ---------------------------------------------------------------
function ImpulseResponseViz({ params }: { params: ReverbParams }) {
    const W = 280;
    const H = 200;

    // Time axis covers 0..1.6 s — long enough to show a max-decay tail.
    const tMax = 1.6;
    const xOfT = (t: number) => (Math.max(0, Math.min(tMax, t)) / tMax) * W;

    // Approximate RT60 from comb feedback: fb = 0.70..0.97 over decay 0..100.
    // RT60 ≈ -3 * combDelay / log10(fb). For the median Freeverb comb (1188
    // samples / 44.1 kHz ≈ 27 ms), RT60 ranges roughly 0.08-2.0 s. We just
    // need a believable shape for the picture.
    const decayNorm = Math.max(0, Math.min(1, params.decayPct / 100));
    const fb = 0.70 + decayNorm * 0.27;
    const tailSec = Math.min(tMax - 0.1, Math.max(0.12, -3 * 0.027 / Math.log10(Math.max(1e-4, fb))));

    const preDelaySec = Math.max(0, Math.min(0.1, params.preDelayMs * 0.001));
    const dryX = xOfT(0);
    const earlyStartX = xOfT(preDelaySec);
    const tailEndX = xOfT(preDelaySec + tailSec);

    const sizeNorm = Math.max(0, Math.min(1, params.sizePct / 100));
    const dampNorm = Math.max(0, Math.min(1, params.dampPct / 100));
    const mixNorm = Math.max(0, Math.min(1, params.mixPct / 100));

    // Wet envelope amplitude — capped so it never visually exceeds dry.
    const wetAmp = Math.min(0.85, 0.30 + mixNorm * 0.7);

    // Build early-reflection picks. Use a deterministic pseudo-random pattern
    // (sin-based hash) so the picture is stable as you twist Size up and down.
    const earlyCount = Math.max(3, Math.round(4 + sizeNorm * 14));
    const earlyEnd = preDelaySec + Math.min(0.08, 0.025 + sizeNorm * 0.06);
    const reflections: Array<{ x: number; amp: number }> = [];
    for (let i = 0; i < earlyCount; i++) {
        const seed = Math.abs(Math.sin((i + 1) * 12.9898) * 43758.5453) % 1;
        const t = preDelaySec + (i / Math.max(1, earlyCount - 1)) * (earlyEnd - preDelaySec) + seed * 0.004;
        // Reflections decay exponentially across the early window.
        const env = Math.exp(-3 * (t - preDelaySec) / Math.max(0.02, earlyEnd - preDelaySec));
        const amp = wetAmp * env * (0.6 + seed * 0.4);
        reflections.push({ x: xOfT(t), amp });
    }

    // Build the tail envelope polyline — exponential decay.
    const tailPoints: Array<[number, number]> = [];
    const steps = 60;
    for (let i = 0; i <= steps; i++) {
        const u = i / steps;
        const t = preDelaySec + u * tailSec;
        const env = Math.exp(-3 * u);
        const amp = wetAmp * env;
        tailPoints.push([xOfT(t), amp]);
    }

    return (
        <svg
            viewBox={`0 0 ${W} ${H}`}
            preserveAspectRatio="none"
            style={{
                width: '100%', maxWidth: 340, height: 'auto',
                background: 'var(--bg-inset, #0e1014)',
                borderRadius: 4,
                border: '1px solid var(--line-1, #2a2c30)',
            }}
            role="img"
            aria-label="Reverb impulse-response illustration"
        >
            <defs>
                {/* Damp gradient — higher damp = tail fades to grey faster. */}
                <linearGradient id="rev-tail-grad" x1={earlyStartX} y1="0" x2={tailEndX} y2="0" gradientUnits="userSpaceOnUse">
                    <stop offset="0%"  stopColor="var(--accent, #4a9eff)" stopOpacity={0.95} />
                    <stop offset={`${30 + dampNorm * 50}%`} stopColor="var(--accent, #4a9eff)" stopOpacity={0.55 - dampNorm * 0.3} />
                    <stop offset="100%" stopColor="var(--fg-3, #5a5e66)" stopOpacity={0.15} />
                </linearGradient>
                <linearGradient id="rev-tail-fill" x1={earlyStartX} y1="0" x2={tailEndX} y2="0" gradientUnits="userSpaceOnUse">
                    <stop offset="0%"  stopColor="var(--accent, #4a9eff)" stopOpacity={0.35} />
                    <stop offset={`${30 + dampNorm * 50}%`} stopColor="var(--accent, #4a9eff)" stopOpacity={0.18 - dampNorm * 0.1} />
                    <stop offset="100%" stopColor="var(--fg-3, #5a5e66)" stopOpacity={0.05} />
                </linearGradient>
                <filter id="rev-glow" x="-2%" y="-2%" width="104%" height="104%">
                    <feGaussianBlur stdDeviation="1.0" result="blur" />
                    <feMerge>
                        <feMergeNode in="blur" />
                        <feMergeNode in="SourceGraphic" />
                    </feMerge>
                </filter>
            </defs>

            {/* Time grid — 100 ms ticks. */}
            {[0.1, 0.2, 0.4, 0.8, 1.6].map((t) => (
                <g key={`t-${t}`}>
                    <line x1={xOfT(t)} y1={0} x2={xOfT(t)} y2={H} stroke="var(--line-1, #2a2c30)" strokeDasharray="2 3" opacity={0.5} />
                    <text x={xOfT(t) + 2} y={H - 4} fill="var(--fg-3, #5a5e66)" fontSize={8} fontFamily="var(--font-mono, JetBrains Mono, ui-monospace, monospace)">
                        {t >= 1 ? `${t.toFixed(1)}s` : `${(t * 1000).toFixed(0)}ms`}
                    </text>
                </g>
            ))}

            {/* Floor / baseline. */}
            <line x1={0} y1={H - 16} x2={W} y2={H - 16} stroke="var(--fg-3, #5a5e66)" strokeWidth={1} opacity={0.6} />

            {/* Pre-delay band — render the gap visually as a slightly darker stripe. */}
            {earlyStartX > dryX + 2 && (
                <rect x={dryX} y={H * 0.18} width={earlyStartX - dryX} height={H * 0.65}
                      fill="var(--power, #ffc93a)" opacity={0.06} />
            )}
            {earlyStartX > dryX + 2 && (
                <text x={(dryX + earlyStartX) / 2} y={H * 0.16}
                      fill="var(--power, #ffc93a)" fontSize={9} textAnchor="middle" opacity={0.85}
                      fontFamily="var(--font-mono, JetBrains Mono, ui-monospace, monospace)">
                    {params.preDelayMs.toFixed(0)}ms
                </text>
            )}

            {/* Dry transient at t=0. */}
            {(() => {
                const dryAmp = 0.9;
                const yTop = (H - 16) - dryAmp * (H - 32);
                return (
                    <g>
                        <line x1={dryX} y1={H - 16} x2={dryX} y2={yTop}
                              stroke="var(--fg-0, #e8eaed)" strokeWidth={2} filter="url(#rev-glow)" />
                        <circle cx={dryX} cy={yTop} r={2.5} fill="var(--fg-0, #e8eaed)" />
                    </g>
                );
            })()}

            {/* Early reflections — sparse picks. */}
            {reflections.map((r, i) => {
                const yTop = (H - 16) - r.amp * (H - 32);
                return (
                    <line key={`er-${i}`}
                          x1={r.x} y1={H - 16} x2={r.x} y2={yTop}
                          stroke="var(--accent, #4a9eff)" strokeWidth={1.4} opacity={0.9} filter="url(#rev-glow)" />
                );
            })}

            {/* Diffuse tail envelope — filled polygon under the curve. */}
            {(() => {
                if (tailPoints.length < 2) return null;
                const top = tailPoints.map(([x, amp]) => `${x},${(H - 16) - amp * (H - 32)}`).join(' ');
                const polyPoints =
                    `${tailPoints[0][0]},${H - 16} ` +
                    top + ' ' +
                    `${tailPoints[tailPoints.length - 1][0]},${H - 16}`;
                return (
                    <>
                        <polygon points={polyPoints} fill="url(#rev-tail-fill)" />
                        <polyline points={top} fill="none" stroke="url(#rev-tail-grad)" strokeWidth={1.5} />
                    </>
                );
            })()}

            {/* Labels. */}
            <text x={4} y={14} fill="var(--fg-2, #b8bcc3)" fontSize={9}
                  fontFamily="var(--font-mono, JetBrains Mono, ui-monospace, monospace)">
                dry
            </text>
            <text x={earlyStartX + 4} y={H * 0.32} fill="var(--accent, #4a9eff)" fontSize={9}
                  fontFamily="var(--font-mono, JetBrains Mono, ui-monospace, monospace)" opacity={0.85}>
                early refl
            </text>
            <text x={tailEndX - 4} y={H * 0.32} fill="var(--fg-3, #5a5e66)" fontSize={9}
                  fontFamily="var(--font-mono, JetBrains Mono, ui-monospace, monospace)" textAnchor="end" opacity={0.85}>
                tail
            </text>
        </svg>
    );
}

// ---------------------------------------------------------------
function ReverbPanel({ api }: { api: ZeusPluginApi }) {
    const [params, setParams] = useState<ReverbParams>(DEFAULT_PARAMS);
    const [meters, setMeters] = useState<ReverbMeters>(DEFAULT_METERS);
    const lastPostedRef = useRef<ReverbParams>(DEFAULT_PARAMS);

    useEffect(() => {
        let active = true;
        (async () => {
            try {
                const res = await api.callBackend('GET', '/params');
                if (active && res.ok) {
                    const p = (await res.json()) as ReverbParams;
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
                if (active && res.ok) setMeters((await res.json()) as ReverbMeters);
            } catch { /* swallow */ }
        };
        const t = window.setInterval(tick, 66);
        tick();
        return () => { active = false; window.clearInterval(t); };
    }, [api]);

    const postDebounceRef = useRef<number | null>(null);
    const schedulePost = useCallback((next: ReverbParams) => {
        setParams(next);
        if (postDebounceRef.current != null) window.clearTimeout(postDebounceRef.current);
        postDebounceRef.current = window.setTimeout(() => {
            const last = lastPostedRef.current;
            const patch: Partial<ReverbParams> = {};
            if (next.sizePct    !== last.sizePct)    patch.sizePct    = next.sizePct;
            if (next.decayPct   !== last.decayPct)   patch.decayPct   = next.decayPct;
            if (next.dampPct    !== last.dampPct)    patch.dampPct    = next.dampPct;
            if (next.preDelayMs !== last.preDelayMs) patch.preDelayMs = next.preDelayMs;
            if (next.mixPct     !== last.mixPct)     patch.mixPct     = next.mixPct;
            if (next.bypass     !== last.bypass)     patch.bypass     = next.bypass;
            if (Object.keys(patch).length === 0) return;
            void api.callBackend('POST', '/params', patch).then(async (res) => {
                if (res.ok) {
                    const echoed = (await res.json()) as ReverbParams;
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
            <header style={{
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
                    fontSize: 13, fontWeight: 600, letterSpacing: 2, textTransform: 'uppercase',
                    color: params.bypass ? 'var(--fg-2, #b8bcc3)' : 'var(--fg-0, #e8eaed)',
                    textShadow: params.bypass ? 'none' : '0 0 8px rgba(255, 201, 58, 0.18)',
                    transition: 'color 120ms linear',
                }}>Reverb</h3>

                <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
                    <span style={{
                        fontFamily: 'var(--font-mono, JetBrains Mono, ui-monospace, monospace)',
                        fontSize: 10, color: 'var(--fg-2, #b8bcc3)', letterSpacing: 0.5,
                    }}>
                        WET {meters.wetLevelDb > -150 ? meters.wetLevelDb.toFixed(0) : '—'} · OUT {meters.outputPeakDb > -150 ? meters.outputPeakDb.toFixed(0) : '—'} dBFS
                    </span>

                    <button
                        type="button"
                        onClick={() => schedulePost({ ...params, bypass: !params.bypass })}
                        aria-pressed={params.bypass}
                        title={params.bypass ? 'Currently bypassed — click to re-engage' : 'Click to bypass this block'}
                        style={{
                            padding: '4px 12px',
                            fontSize: 10, fontWeight: 600, letterSpacing: 1.5, textTransform: 'uppercase',
                            color: params.bypass ? '#fff' : 'var(--fg-1, #d6d8dc)',
                            background: params.bypass ? 'var(--tx, #e63a2b)' : 'var(--bg-2, #1f2226)',
                            border: '1px solid ' + (params.bypass ? 'var(--tx, #e63a2b)' : 'var(--line-2, #3a3d42)'),
                            borderRadius: 3, cursor: 'pointer',
                            boxShadow: params.bypass ? '0 0 8px rgba(230, 58, 43, 0.5), inset 0 1px 0 rgba(255, 255, 255, 0.15)' : 'inset 0 1px 0 rgba(255, 255, 255, 0.04)',
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
                <div style={{ flex: '1 1 280px', minWidth: 240, maxWidth: 360 }}>
                    <ImpulseResponseViz params={params} />
                    <div style={{
                        fontFamily: 'var(--font-mono, JetBrains Mono, ui-monospace, monospace)',
                        fontSize: 10, color: 'var(--fg-3, #5a5e66)',
                        marginTop: 4, textAlign: 'center',
                    }}>
                        dry · {params.preDelayMs.toFixed(0)}ms pre-delay · early refl · diffuse tail
                    </div>
                </div>

                <div style={{
                    display: 'grid',
                    gridTemplateColumns: 'repeat(3, auto)',
                    gap: 12,
                    paddingTop: 4,
                    justifyContent: 'center',
                }}>
                    <Knob
                        label="Size"
                        value={params.sizePct}
                        min={0}
                        max={100}
                        defaultValue={50}
                        unit="%"
                        formatValue={(v) => v.toFixed(0)}
                        onChange={(v) => schedulePost({ ...params, sizePct: v })}
                    />
                    <Knob
                        label="Decay"
                        value={params.decayPct}
                        min={0}
                        max={100}
                        defaultValue={40}
                        unit="%"
                        formatValue={(v) => v.toFixed(0)}
                        onChange={(v) => schedulePost({ ...params, decayPct: v })}
                    />
                    <Knob
                        label="Damp"
                        value={params.dampPct}
                        min={0}
                        max={100}
                        defaultValue={50}
                        unit="%"
                        formatValue={(v) => v.toFixed(0)}
                        onChange={(v) => schedulePost({ ...params, dampPct: v })}
                    />
                    <Knob
                        label="PreDelay"
                        value={params.preDelayMs}
                        min={0}
                        max={100}
                        defaultValue={15}
                        unit="ms"
                        formatValue={(v) => v.toFixed(0)}
                        onChange={(v) => schedulePost({ ...params, preDelayMs: v })}
                    />
                    <Knob
                        label="Mix"
                        value={params.mixPct}
                        min={0}
                        max={100}
                        defaultValue={12}
                        unit="%"
                        formatValue={(v) => v.toFixed(0)}
                        onChange={(v) => schedulePost({ ...params, mixPct: v })}
                    />
                </div>
            </div>

            <footer style={{
                fontFamily: 'var(--font-sans, Inter, system-ui, sans-serif)',
                fontSize: 10, color: 'var(--fg-3, #5a5e66)',
                textAlign: 'right', paddingRight: 4,
            }}>
                Drag knobs to adjust · shift-drag fine · double-click reset · wheel = ±1 %
            </footer>
        </section>
    );
}

export default function register(api: ZeusPluginApi) {
    api.registerPanel({
        id: 'reverb.main',
        component: () => <ReverbPanel api={api} />,
    });
}

// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus voice audio chain v1 — Bass Enhancer UI module.
// Copyright (C) 2025-2026 KB2UKA and contributors.
//
// Centerpiece: SVG illustration showing the LP/HP split point in
// --power gold + the synthesised octave-up harmonic series in
// --accent blue rising above the cutoff. Three rotary knobs
// (Frequency / Amount / Mix). Brass-plate header with Bypass.

import { useCallback, useEffect, useRef, useState } from 'react';

interface ZeusPluginApi {
    registerPanel(spec: { id: string; component: React.ComponentType }): void;
    callBackend(method: string, path: string, body?: unknown): Promise<Response>;
}

interface BassParams {
    freqHz: number;
    amountPct: number;
    mixPct: number;
    bypass: boolean;
}

interface BassMeters {
    inputPeakDb: number;
    outputPeakDb: number;
    harmonicLevelDb: number;
}

const DEFAULT_PARAMS: BassParams = {
    freqHz: 150,
    amountPct: 35,
    mixPct: 25,
    bypass: false,
};

const DEFAULT_METERS: BassMeters = { inputPeakDb: -200, outputPeakDb: -200, harmonicLevelDb: -200 };

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
                    <radialGradient id={`dial-bass-${label}`} cx="0.35" cy="0.35" r="0.75">
                        <stop offset="0%" stopColor="var(--bg-2, #1f2226)" />
                        <stop offset="100%" stopColor="var(--bg-0, #0e1014)" />
                    </radialGradient>
                </defs>
                <circle cx={0} cy={0} r={22} fill={`url(#dial-bass-${label})`} stroke="var(--line-2, #3a3d42)" strokeWidth={1} />
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
// SpectrumViz — illustrates the algorithm in the frequency domain.
// Below the cutoff: original bass band in --fg-3 grey (gets isolated).
// Above the cutoff: synthesised harmonics (2H, 4H, 6H) in --accent blue
// with --power gold cutoff marker. As Amount/Mix increase the harmonic
// bars get taller.
// ---------------------------------------------------------------
function SpectrumViz({ params }: { params: BassParams }) {
    const W = 280;
    const H = 200;

    // Log frequency axis 20..2000 Hz (we only care about the bass + low-mid
    // region where this block lives — full 20 kHz axis would waste real estate).
    const lnLo = Math.log(20);
    const lnHi = Math.log(2000);
    const xOf = (f: number) => ((Math.log(Math.max(20, Math.min(2000, f))) - lnLo) / (lnHi - lnLo)) * W;

    const cutoffX = xOf(params.freqHz);

    // Synth amplitude scales with amount * mix.
    const amountNorm = Math.max(0, Math.min(1, params.amountPct / 100));
    const mixNorm = Math.max(0, Math.min(1, params.mixPct / 100));
    const synthAmp = Math.min(1, amountNorm * 0.6 + mixNorm * 0.6);

    // Generate harmonic bars at 2x, 4x, 6x of the cutoff.
    const harmonics: Array<{ f: number; h: number }> = [];
    for (const mult of [2, 3, 4, 5, 6]) {
        const f = params.freqHz * mult;
        if (f > 2000) break;
        // Each higher harmonic is quieter (typ -3 to -6 dB per step).
        const amp = synthAmp * Math.pow(0.6, mult - 2);
        harmonics.push({ f, h: amp });
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
            aria-label="Bass enhancer frequency illustration"
        >
            <defs>
                <linearGradient id="bass-bar-grad" x1="0" y1="0" x2="0" y2={H} gradientUnits="userSpaceOnUse">
                    <stop offset="0%"  stopColor="var(--accent, #4a9eff)" stopOpacity={0.95} />
                    <stop offset="100%" stopColor="var(--accent, #4a9eff)" stopOpacity={0.4} />
                </linearGradient>
                <filter id="bass-glow" x="-2%" y="-2%" width="104%" height="104%">
                    <feGaussianBlur stdDeviation="1.0" result="blur" />
                    <feMerge>
                        <feMergeNode in="blur" />
                        <feMergeNode in="SourceGraphic" />
                    </feMerge>
                </filter>
            </defs>

            {/* Frequency grid */}
            {[50, 100, 200, 500, 1000].map((f) => (
                <g key={`f-${f}`}>
                    <line x1={xOf(f)} y1={0} x2={xOf(f)} y2={H} stroke="var(--line-1, #2a2c30)" strokeDasharray="2 3" opacity={0.5} />
                    <text x={xOf(f) + 2} y={H - 4} fill="var(--fg-3, #5a5e66)" fontSize={8} fontFamily="var(--font-mono, JetBrains Mono, ui-monospace, monospace)">
                        {f >= 1000 ? `${f / 1000}k` : f}
                    </text>
                </g>
            ))}

            {/* Bass band — below cutoff, in dim grey (isolated, then transmitted untouched as part of dry) */}
            <rect x={0} y={H * 0.55} width={cutoffX} height={H * 0.35}
                  fill="var(--fg-3, #5a5e66)" opacity={0.25} />
            <text x={4} y={H * 0.50} fill="var(--fg-3, #5a5e66)" fontSize={9} fontFamily="var(--font-mono, JetBrains Mono, ui-monospace, monospace)">
                bass band
            </text>

            {/* Synthesised harmonic bars */}
            {harmonics.map((h, i) => {
                const x = xOf(h.f);
                const barHeight = h.h * H * 0.65;
                return (
                    <g key={`h-${i}`}>
                        <rect x={x - 4} y={H - 8 - barHeight} width={8} height={barHeight}
                              fill="url(#bass-bar-grad)" filter="url(#bass-glow)" />
                        <text x={x} y={H - 8 - barHeight - 4} fill="var(--accent, #4a9eff)" fontSize={8}
                              textAnchor="middle"
                              fontFamily="var(--font-mono, JetBrains Mono, ui-monospace, monospace)" opacity={0.8}>
                            {i + 2}H
                        </text>
                    </g>
                );
            })}

            {/* Cutoff marker */}
            <line x1={cutoffX} y1={0} x2={cutoffX} y2={H} stroke="var(--power, #ffc93a)" strokeWidth={1.5} strokeDasharray="3 3" opacity={0.85} />
            <text x={cutoffX + 4} y={14} fill="var(--power, #ffc93a)" fontSize={9}
                  fontFamily="var(--font-mono, JetBrains Mono, ui-monospace, monospace)">
                {params.freqHz >= 1000 ? `${(params.freqHz / 1000).toFixed(2)} kHz` : `${params.freqHz.toFixed(0)} Hz`}
            </text>

            {/* Floor line */}
            <line x1={0} y1={H - 8} x2={W} y2={H - 8} stroke="var(--fg-3, #5a5e66)" strokeWidth={1} opacity={0.6} />
        </svg>
    );
}

// ---------------------------------------------------------------
function BassPanel({ api }: { api: ZeusPluginApi }) {
    const [params, setParams] = useState<BassParams>(DEFAULT_PARAMS);
    const [meters, setMeters] = useState<BassMeters>(DEFAULT_METERS);
    const lastPostedRef = useRef<BassParams>(DEFAULT_PARAMS);

    useEffect(() => {
        let active = true;
        (async () => {
            try {
                const res = await api.callBackend('GET', '/params');
                if (active && res.ok) {
                    const p = (await res.json()) as BassParams;
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
                if (active && res.ok) setMeters((await res.json()) as BassMeters);
            } catch { /* swallow */ }
        };
        const t = window.setInterval(tick, 66);
        tick();
        return () => { active = false; window.clearInterval(t); };
    }, [api]);

    const postDebounceRef = useRef<number | null>(null);
    const schedulePost = useCallback((next: BassParams) => {
        setParams(next);
        if (postDebounceRef.current != null) window.clearTimeout(postDebounceRef.current);
        postDebounceRef.current = window.setTimeout(() => {
            const last = lastPostedRef.current;
            const patch: Partial<BassParams> = {};
            if (next.freqHz    !== last.freqHz)    patch.freqHz    = next.freqHz;
            if (next.amountPct !== last.amountPct) patch.amountPct = next.amountPct;
            if (next.mixPct    !== last.mixPct)    patch.mixPct    = next.mixPct;
            if (next.bypass    !== last.bypass)    patch.bypass    = next.bypass;
            if (Object.keys(patch).length === 0) return;
            void api.callBackend('POST', '/params', patch).then(async (res) => {
                if (res.ok) {
                    const echoed = (await res.json()) as BassParams;
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
                }}>Bass Enhancer</h3>

                <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
                    <span style={{
                        fontFamily: 'var(--font-mono, JetBrains Mono, ui-monospace, monospace)',
                        fontSize: 10, color: 'var(--fg-2, #b8bcc3)', letterSpacing: 0.5,
                    }}>
                        HARM {meters.harmonicLevelDb > -150 ? meters.harmonicLevelDb.toFixed(0) : '—'} · OUT {meters.outputPeakDb > -150 ? meters.outputPeakDb.toFixed(0) : '—'} dBFS
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
                    <SpectrumViz params={params} />
                    <div style={{
                        fontFamily: 'var(--font-mono, JetBrains Mono, ui-monospace, monospace)',
                        fontSize: 10, color: 'var(--fg-3, #5a5e66)',
                        marginTop: 4, textAlign: 'center',
                    }}>
                        bass below {params.freqHz}Hz → octave-up harmonics in radiable band
                    </div>
                </div>

                <div style={{ display: 'flex', flexDirection: 'column', gap: 12, paddingTop: 4 }}>
                    <Knob
                        label="Frequency"
                        value={params.freqHz}
                        min={40}
                        max={500}
                        defaultValue={150}
                        unit="Hz"
                        formatValue={(v) => v.toFixed(0)}
                        logScale
                        onChange={(v) => schedulePost({ ...params, freqHz: v })}
                    />
                    <Knob
                        label="Amount"
                        value={params.amountPct}
                        min={0}
                        max={100}
                        defaultValue={35}
                        unit="%"
                        formatValue={(v) => v.toFixed(0)}
                        onChange={(v) => schedulePost({ ...params, amountPct: v })}
                    />
                    <Knob
                        label="Mix"
                        value={params.mixPct}
                        min={0}
                        max={100}
                        defaultValue={25}
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
        id: 'bass.main',
        component: () => <BassPanel api={api} />,
    });
}

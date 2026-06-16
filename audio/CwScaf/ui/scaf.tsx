// SPDX-License-Identifier: GPL-2.0-or-later
//
// CW SCAF — RX audio filter UI module.
// Copyright (C) 2026 contributors.
//
// Same visual language as the Compressor/EQ audio-chain panels: brass-plate
// header, centerpiece SVG plot, SVG rotary knobs with drag-to-rotate, and
// segmented LED peak meters. Centerpiece here is the filter's magnitude
// response, computed live from the same RBJ bandpass biquad the C# DSP runs.
// All colours via tokens.css CSS variables; no raw hex.

import { useCallback, useEffect, useRef, useState } from 'react';

// ---------------------------------------------------------------
// ZeusPluginApi contract — registerPanel + callBackend, no more.
// ---------------------------------------------------------------
interface ZeusPluginApi {
    registerPanel(spec: { id: string; component: React.ComponentType }): void;
    callBackend(method: string, path: string, body?: unknown): Promise<Response>;
}

// ---------------------------------------------------------------
// REST wire shape — mirrors ScafParamsDto / ScafMetersDto from
// CwScafPlugin.cs.
// ---------------------------------------------------------------
interface ScafParams {
    centerHz: number;
    bandwidthHz: number;
    sections: number;
    bypass: boolean;
}

interface ScafMeters {
    inputPeak: number;   // linear amplitude 0..~1
    outputPeak: number;
}

const DEFAULT_PARAMS: ScafParams = {
    centerHz: 600,
    bandwidthHz: 200,
    sections: 2,
    bypass: false,
};

const DEFAULT_METERS: ScafMeters = { inputPeak: 0, outputPeak: 0 };

const SAMPLE_RATE = 48000;

// ---------------------------------------------------------------
// Biquad magnitude response — mirrors ScafDsp.RecomputeCoefficients
// (RBJ "BPF, constant 0 dB peak gain") so the curve matches what the
// operator actually hears.
// ---------------------------------------------------------------
interface Biquad { b0: number; b1: number; b2: number; a1: number; a2: number; }

function bandpassCoeffs(centerHz: number, bandwidthHz: number): Biquad {
    const nyq = SAMPLE_RATE * 0.5;
    const f0 = Math.max(50, Math.min(nyq - 50, centerHz));
    const bw = Math.max(10, bandwidthHz);
    const q = Math.max(0.3, f0 / bw);
    const w0 = (2 * Math.PI * f0) / SAMPLE_RATE;
    const cosw0 = Math.cos(w0);
    const alpha = Math.sin(w0) / (2 * q);
    const a0 = 1 + alpha;
    return {
        b0: alpha / a0,
        b1: 0,
        b2: -alpha / a0,
        a1: (-2 * cosw0) / a0,
        a2: (1 - alpha) / a0,
    };
}

function magnitudeDb(freqHz: number, c: Biquad, sections: number): number {
    const w = (2 * Math.PI * freqHz) / SAMPLE_RATE;
    const cos1 = Math.cos(w), sin1 = Math.sin(w);
    const cos2 = Math.cos(2 * w), sin2 = Math.sin(2 * w);
    const nRe = c.b0 + c.b1 * cos1 + c.b2 * cos2;
    const nIm = -(c.b1 * sin1 + c.b2 * sin2);
    const dRe = 1 + c.a1 * cos1 + c.a2 * cos2;
    const dIm = -(c.a1 * sin1 + c.a2 * sin2);
    const mag = Math.hypot(nRe, nIm) / Math.hypot(dRe, dIm);
    return 20 * Math.log10(Math.max(1e-9, Math.pow(mag, sections)));
}

const linToDb = (lin: number): number => (lin <= 1e-6 ? -120 : 20 * Math.log10(lin));

// ---------------------------------------------------------------
// Knob — SVG rotary control with drag-to-rotate. (Same component the
// Compressor/EQ panels use; see those for the interaction notes.)
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

    const valueToNorm = (v: number): number => {
        if (logScale) {
            const ln = Math.log(Math.max(min, v));
            return (ln - Math.log(min)) / (Math.log(max) - Math.log(min));
        }
        return (v - min) / (max - min);
    };
    const normToValue = (n: number): number => {
        const clamped = Math.max(0, Math.min(1, n));
        if (logScale) return Math.exp(Math.log(min) + clamped * (Math.log(max) - Math.log(min)));
        return min + clamped * (max - min);
    };

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
        onChange(normToValue(startNorm + dy * sensitivity));
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [onChange]);

    const onPointerUp = useCallback((e: React.PointerEvent<SVGSVGElement>) => {
        knobRef.current?.releasePointerCapture(e.pointerId);
        dragStartRef.current = null;
    }, []);

    const onDoubleClick = useCallback(() => onChange(defaultValue), [defaultValue, onChange]);

    const onWheel = useCallback((e: React.WheelEvent<SVGSVGElement>) => {
        e.preventDefault();
        const step = e.shiftKey ? 0.001 : 0.01;
        const dir = e.deltaY > 0 ? -1 : 1;
        onChange(normToValue(valueToNorm(value) + dir * step));
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [value, onChange]);

    const display = formatValue ? formatValue(value) : value.toFixed(2);

    const ticks: number[] = [];
    for (let i = 0; i <= 9; i++) ticks.push(-135 + (i / 9) * 270);

    const R = 22, TICK_OUTER = 24, TICK_INNER = 26, POINTER_LEN = 17;

    return (
        <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 4, minWidth: 64, userSelect: 'none' }}>
            <span style={{ fontFamily: 'var(--font-sans, Inter, system-ui, sans-serif)', fontSize: 10, letterSpacing: 1, textTransform: 'uppercase', color: 'var(--fg-2, #b8bcc3)', fontWeight: 500 }}>{label}</span>
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
                aria-label={`${label} ${display} ${unit}. Drag to adjust, shift-drag for fine, double-click to reset.`}
                role="slider"
                aria-valuenow={value}
                aria-valuemin={min}
                aria-valuemax={max}
            >
                {ticks.map((deg, i) => {
                    const rad = ((deg - 90) * Math.PI) / 180;
                    const x1 = TICK_INNER * Math.cos(rad), y1 = TICK_INNER * Math.sin(rad);
                    const x2 = TICK_OUTER * Math.cos(rad), y2 = TICK_OUTER * Math.sin(rad);
                    const isActive = deg <= angleDeg + 0.5;
                    return <line key={i} x1={x1} y1={y1} x2={x2} y2={y2} stroke={isActive ? 'var(--accent, #4a9eff)' : 'var(--line-1, #2a2c30)'} strokeWidth={1.5} strokeLinecap="round" />;
                })}
                <defs>
                    <radialGradient id={`scaf-dial-${label}`} cx="0.35" cy="0.35" r="0.75">
                        <stop offset="0%" stopColor="var(--bg-2, #1f2226)" />
                        <stop offset="100%" stopColor="var(--bg-0, #0e1014)" />
                    </radialGradient>
                </defs>
                <circle cx={0} cy={0} r={R} fill={`url(#scaf-dial-${label})`} stroke="var(--line-2, #3a3d42)" strokeWidth={1} />
                {(() => {
                    const rad = ((angleDeg - 90) * Math.PI) / 180;
                    const x = POINTER_LEN * Math.cos(rad), y = POINTER_LEN * Math.sin(rad);
                    return (
                        <>
                            <line x1={0} y1={0} x2={x} y2={y} stroke="var(--accent, #4a9eff)" strokeWidth={2.5} strokeLinecap="round" />
                            <circle cx={x} cy={y} r={2.5} fill="var(--accent, #4a9eff)" />
                        </>
                    );
                })()}
            </svg>
            <span style={{ fontFamily: 'var(--font-mono, JetBrains Mono, ui-monospace, monospace)', fontSize: 11, color: 'var(--fg-0, #e8eaed)', fontVariantNumeric: 'tabular-nums', lineHeight: 1 }}>{display}{unit ? ` ${unit}` : ''}</span>
        </div>
    );
}

// ---------------------------------------------------------------
// VMeter — segmented vertical LED meter with peak hold (dBFS scale).
// ---------------------------------------------------------------
function VMeter({ label, valueDb, minDb, maxDb }: { label: string; valueDb: number; minDb: number; maxDb: number }) {
    const SEGMENTS = 20;
    const [peakHold, setPeakHold] = useState<number>(valueDb);
    const peakHoldRef = useRef<{ value: number; t: number }>({ value: valueDb, t: performance.now() });

    useEffect(() => {
        if (valueDb > peakHoldRef.current.value) {
            peakHoldRef.current = { value: valueDb, t: performance.now() };
            setPeakHold(valueDb);
        }
        const interval = setInterval(() => {
            const dt = (performance.now() - peakHoldRef.current.t) / 1000;
            const decayed = peakHoldRef.current.value - dt * 8;
            if (decayed < valueDb) {
                peakHoldRef.current = { value: valueDb, t: performance.now() };
                setPeakHold(valueDb);
            } else setPeakHold(decayed);
        }, 50);
        return () => clearInterval(interval);
    }, [valueDb]);

    const clamped = Math.max(minDb, Math.min(maxDb, valueDb));
    const range = maxDb - minDb;
    const valueNorm = (clamped - minDb) / range;
    const peakNorm = Math.max(0, Math.min(1, (Math.max(minDb, Math.min(maxDb, peakHold)) - minDb) / range));

    const segColor = (segNorm: number): string => {
        if (segNorm < 0.6) return 'var(--accent, #4a9eff)';
        if (segNorm < 0.85) return 'var(--power, #ffc93a)';
        return 'var(--tx, #e63a2b)';
    };

    return (
        <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 2, width: 24 }}>
            <span style={{ fontFamily: 'var(--font-sans, Inter, system-ui, sans-serif)', fontSize: 9, letterSpacing: 0.6, textTransform: 'uppercase', color: 'var(--fg-2, #b8bcc3)' }}>{label}</span>
            <div style={{ position: 'relative', width: 14, height: 132, background: 'var(--bg-inset, #0e1014)', borderRadius: 3, padding: 1, border: '1px solid var(--line-1, #2a2c30)', display: 'flex', flexDirection: 'column-reverse', gap: 1 }}>
                {Array.from({ length: SEGMENTS }, (_, i) => {
                    const segNorm = (i + 0.5) / SEGMENTS;
                    const lit = segNorm <= valueNorm;
                    const atPeak = Math.abs(segNorm - peakNorm) < 1 / SEGMENTS / 2 && valueNorm > 0.02;
                    const segHeight = (132 - 2 - (SEGMENTS - 1)) / SEGMENTS;
                    return <div key={i} style={{ height: segHeight, background: lit ? segColor(segNorm) : 'var(--bg-1, #14161a)', opacity: lit ? 1 : 0.6, outline: atPeak ? '1px solid var(--amber, #FFA028)' : undefined, transition: 'opacity 30ms linear' }} />;
                })}
            </div>
            <span style={{ fontFamily: 'var(--font-mono, JetBrains Mono, ui-monospace, monospace)', fontSize: 9, color: 'var(--fg-0, #e8eaed)', lineHeight: 1, fontVariantNumeric: 'tabular-nums' }}>{valueDb <= -110 ? '—' : valueDb.toFixed(0)}</span>
        </div>
    );
}

// ---------------------------------------------------------------
// ResponseCurve — magnitude (dB) vs frequency (log) of the cascaded
// bandpass. Centre pitch marked in --power; the −3 dB band shaded;
// trace in --accent with a soft glow + gradient fill.
// ---------------------------------------------------------------
function ResponseCurve({ params }: { params: ScafParams }) {
    const W = 280, H = 200;
    const fMin = 100, fMax = 3000;
    const dbMin = -42, dbMax = 6;

    const logF = (f: number) => Math.log10(f);
    const xOf = (f: number) => ((logF(f) - logF(fMin)) / (logF(fMax) - logF(fMin))) * W;
    const yOf = (db: number) => H - ((db - dbMin) / (dbMax - dbMin)) * H;

    const c = bandpassCoeffs(params.centerHz, params.bandwidthHz);
    const sections = Math.max(1, Math.min(4, Math.round(params.sections)));

    const pts: string[] = [];
    const N = 280;
    for (let i = 0; i <= N; i++) {
        const f = Math.pow(10, logF(fMin) + (i / N) * (logF(fMax) - logF(fMin)));
        const db = magnitudeDb(f, c, sections);
        const y = Math.max(0, Math.min(H, yOf(db)));
        pts.push(`${i === 0 ? 'M' : 'L'} ${xOf(f).toFixed(1)} ${y.toFixed(1)}`);
    }
    const curve = pts.join(' ');
    const fill = `${curve} L ${xOf(fMax)} ${H} L ${xOf(fMin)} ${H} Z`;

    const centerX = xOf(Math.max(fMin, Math.min(fMax, params.centerHz)));
    const loEdge = Math.max(fMin, params.centerHz - params.bandwidthHz / 2);
    const hiEdge = Math.min(fMax, params.centerHz + params.bandwidthHz / 2);

    const gridFreqs = [100, 200, 500, 1000, 2000, 3000];

    return (
        <svg
            viewBox={`0 0 ${W} ${H}`}
            preserveAspectRatio="none"
            style={{ width: '100%', height: 'auto', maxHeight: 280, background: 'var(--bg-inset, #0e1014)', borderRadius: 4, border: '1px solid var(--line-1, #2a2c30)' }}
            role="img"
            aria-label="CW SCAF magnitude response"
        >
            <defs>
                <linearGradient id="scaf-fill" x1="0" y1={H} x2="0" y2="0" gradientUnits="userSpaceOnUse">
                    <stop offset="0%" stopColor="var(--accent, #4a9eff)" stopOpacity={0.0} />
                    <stop offset="60%" stopColor="var(--accent, #4a9eff)" stopOpacity={0.1} />
                    <stop offset="100%" stopColor="var(--accent, #4a9eff)" stopOpacity={0.22} />
                </linearGradient>
                <filter id="scaf-glow" x="-2%" y="-2%" width="104%" height="104%">
                    <feGaussianBlur stdDeviation="1.2" result="blur" />
                    <feMerge>
                        <feMergeNode in="blur" />
                        <feMergeNode in="SourceGraphic" />
                    </feMerge>
                </filter>
            </defs>

            {/* Vertical frequency grid + labels */}
            {gridFreqs.map((f) => (
                <g key={f}>
                    <line x1={xOf(f)} y1={0} x2={xOf(f)} y2={H} stroke="var(--line-1, #2a2c30)" strokeDasharray="2 3" opacity={0.6} />
                    <text x={xOf(f) + 2} y={H - 4} fill="var(--fg-3, #5a5e66)" fontSize={8} fontFamily="var(--font-mono, JetBrains Mono, ui-monospace, monospace)">{f >= 1000 ? `${f / 1000}k` : f}</text>
                </g>
            ))}
            {/* Horizontal 0 dB + −12/−24/−36 lines */}
            {[0, -12, -24, -36].map((db) => (
                <line key={db} x1={0} y1={yOf(db)} x2={W} y2={yOf(db)} stroke="var(--line-1, #2a2c30)" strokeDasharray="2 3" opacity={db === 0 ? 0.9 : 0.5} />
            ))}

            {/* −3 dB passband shading */}
            <rect x={xOf(loEdge)} y={0} width={Math.max(0, xOf(hiEdge) - xOf(loEdge))} height={H} fill="var(--power, #ffc93a)" opacity={0.06} />

            {/* Gradient fill under trace */}
            <path d={fill} fill="url(#scaf-fill)" />
            {/* Magnitude trace */}
            <path d={curve} stroke="var(--accent, #4a9eff)" strokeWidth={2.4} fill="none" filter="url(#scaf-glow)" />

            {/* Centre pitch marker */}
            <line x1={centerX} y1={0} x2={centerX} y2={H} stroke="var(--power, #ffc93a)" strokeWidth={1} strokeDasharray="3 4" opacity={0.8} />
            <text x={centerX + 3} y={12} fill="var(--power, #ffc93a)" fontSize={9} fontFamily="var(--font-mono, JetBrains Mono, ui-monospace, monospace)" opacity={0.9}>
                {params.centerHz.toFixed(0)} Hz
            </text>

            <text x={4} y={12} fill="var(--fg-3, #5a5e66)" fontSize={8} fontFamily="var(--font-mono, JetBrains Mono, ui-monospace, monospace)">dB</text>
        </svg>
    );
}

// ---------------------------------------------------------------
// Main panel.
// ---------------------------------------------------------------
function ScafPanel({ api }: { api: ZeusPluginApi }) {
    const [params, setParams] = useState<ScafParams>(DEFAULT_PARAMS);
    const [meters, setMeters] = useState<ScafMeters>(DEFAULT_METERS);
    const lastPostedRef = useRef<ScafParams>(DEFAULT_PARAMS);

    useEffect(() => {
        let active = true;
        (async () => {
            try {
                const res = await api.callBackend('GET', '/params');
                if (active && res.ok) {
                    const p = await res.json() as ScafParams;
                    setParams(p);
                    lastPostedRef.current = p;
                }
            } catch { /* defaults */ }
        })();
        return () => { active = false; };
    }, [api]);

    useEffect(() => {
        let active = true;
        const tick = async () => {
            try {
                const res = await api.callBackend('GET', '/meters');
                if (active && res.ok) setMeters(await res.json() as ScafMeters);
            } catch { /* keep last */ }
        };
        const t = window.setInterval(tick, 66);
        tick();
        return () => { active = false; window.clearInterval(t); };
    }, [api]);

    const postDebounceRef = useRef<number | null>(null);
    const schedulePost = useCallback((next: ScafParams) => {
        setParams(next);
        if (postDebounceRef.current != null) window.clearTimeout(postDebounceRef.current);
        postDebounceRef.current = window.setTimeout(() => {
            const last = lastPostedRef.current;
            const patch: Partial<ScafParams> = {};
            if (next.centerHz    !== last.centerHz)    patch.centerHz    = next.centerHz;
            if (next.bandwidthHz !== last.bandwidthHz) patch.bandwidthHz = next.bandwidthHz;
            if (next.sections    !== last.sections)    patch.sections    = next.sections;
            if (next.bypass      !== last.bypass)      patch.bypass      = next.bypass;
            if (Object.keys(patch).length === 0) return;
            void api.callBackend('POST', '/params', patch).then(async (res) => {
                if (res.ok) lastPostedRef.current = await res.json() as ScafParams;
            });
        }, 120);
    }, [api]);

    return (
        <section style={{ display: 'flex', flexDirection: 'column', gap: 12, padding: 12, background: 'linear-gradient(180deg, var(--panel-top, #14161a), var(--panel-bot, #0e1014))', border: '1px solid var(--line-1, #2a2c30)', borderRadius: 8, color: 'var(--fg-0, #e8eaed)', fontFamily: 'var(--font-sans, Inter, system-ui, sans-serif)', boxShadow: '0 1px 0 rgba(255,255,255,0.04) inset, 0 4px 12px rgba(0,0,0,0.25)' }}>
            {/* Brass-plate header with bypass toggle */}
            <header style={{ position: 'relative', padding: '8px 6px 10px', borderBottom: '1px solid var(--line-1, #2a2c30)', boxShadow: 'inset 0 2px 0 var(--power, #ffc93a), inset 0 3px 8px rgba(255, 201, 58, 0.12)', display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 12 }}>
                <h3 style={{ margin: 0, fontFamily: 'var(--font-sans, Inter, system-ui, sans-serif)', fontSize: 13, fontWeight: 600, letterSpacing: 2, textTransform: 'uppercase', color: params.bypass ? 'var(--fg-2, #b8bcc3)' : 'var(--fg-0, #e8eaed)', textShadow: params.bypass ? 'none' : '0 0 8px rgba(255, 201, 58, 0.18)', transition: 'color 120ms linear' }}>CW SCAF</h3>
                <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
                    <span style={{ fontFamily: 'var(--font-mono, JetBrains Mono, ui-monospace, monospace)', fontSize: 10, color: 'var(--fg-2, #b8bcc3)', letterSpacing: 0.5 }}>RX · post-demod</span>
                    <button
                        type="button"
                        onClick={() => schedulePost({ ...params, bypass: !params.bypass })}
                        aria-pressed={params.bypass}
                        title={params.bypass ? 'Currently bypassed — click to re-engage' : 'Click to bypass this filter (RX audio flows through unchanged)'}
                        style={{ padding: '4px 12px', fontFamily: 'var(--font-sans, Inter, system-ui, sans-serif)', fontSize: 10, fontWeight: 600, letterSpacing: 1.5, textTransform: 'uppercase', color: params.bypass ? '#fff' : 'var(--fg-1, #d6d8dc)', background: params.bypass ? 'var(--tx, #e63a2b)' : 'var(--bg-2, #1f2226)', border: '1px solid ' + (params.bypass ? 'var(--tx, #e63a2b)' : 'var(--line-2, #3a3d42)'), borderRadius: 3, cursor: 'pointer', boxShadow: params.bypass ? '0 0 8px rgba(230, 58, 43, 0.5), inset 0 1px 0 rgba(255, 255, 255, 0.15)' : 'inset 0 1px 0 rgba(255, 255, 255, 0.04)', transition: 'all 120ms ease-out' }}
                    >
                        {params.bypass ? 'Bypassed' : 'Bypass'}
                    </button>
                </div>
            </header>

            <div style={{ display: 'flex', gap: 14, alignItems: 'stretch', flexWrap: 'wrap', opacity: params.bypass ? 0.45 : 1, transition: 'opacity 160ms ease-out', pointerEvents: params.bypass ? 'none' : 'auto' }}>
                {/* Left knob column */}
                <div style={{ display: 'flex', flexDirection: 'column', gap: 14, paddingTop: 4 }}>
                    <Knob label="Pitch"  value={params.centerHz}    min={300} max={1200} defaultValue={600} unit="Hz" logScale onChange={(v) => schedulePost({ ...params, centerHz: Math.round(v) })} formatValue={(v) => v.toFixed(0)} />
                    <Knob label="Width"  value={params.bandwidthHz} min={50}  max={1000} defaultValue={200} unit="Hz" logScale onChange={(v) => schedulePost({ ...params, bandwidthHz: Math.round(v) })} formatValue={(v) => v.toFixed(0)} />
                </div>

                {/* Center: response curve */}
                <div style={{ flex: '1 1 280px', minWidth: 240, maxWidth: 380 }}>
                    <ResponseCurve params={params} />
                </div>

                {/* Right: section selector + meters */}
                <div style={{ display: 'flex', gap: 10, paddingTop: 4 }}>
                    <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 6 }}>
                        <span style={{ fontFamily: 'var(--font-sans, Inter, system-ui, sans-serif)', fontSize: 10, letterSpacing: 1, textTransform: 'uppercase', color: 'var(--fg-2, #b8bcc3)', fontWeight: 500 }}>Poles</span>
                        {[1, 2, 3, 4].map((n) => {
                            const active = Math.round(params.sections) === n;
                            return (
                                <button
                                    key={n}
                                    type="button"
                                    onClick={() => schedulePost({ ...params, sections: n })}
                                    aria-pressed={active}
                                    title={`${n} cascaded biquad section${n > 1 ? 's' : ''} — higher = steeper skirts`}
                                    style={{ width: 34, padding: '5px 0', fontFamily: 'var(--font-mono, JetBrains Mono, ui-monospace, monospace)', fontSize: 12, fontWeight: 600, color: active ? '#fff' : 'var(--fg-1, #d6d8dc)', background: active ? 'var(--accent, #4a9eff)' : 'var(--bg-2, #1f2226)', border: '1px solid ' + (active ? 'var(--accent, #4a9eff)' : 'var(--line-2, #3a3d42)'), borderRadius: 3, cursor: 'pointer', boxShadow: active ? '0 0 8px rgba(74, 158, 255, 0.45), inset 0 1px 0 rgba(255,255,255,0.15)' : 'inset 0 1px 0 rgba(255,255,255,0.04)', transition: 'all 120ms ease-out' }}
                                >
                                    {n}
                                </button>
                            );
                        })}
                    </div>
                    <div style={{ display: 'flex', gap: 6 }}>
                        <VMeter label="IN"  valueDb={linToDb(meters.inputPeak)}  minDb={-60} maxDb={0} />
                        <VMeter label="OUT" valueDb={linToDb(meters.outputPeak)} minDb={-60} maxDb={0} />
                    </div>
                </div>
            </div>

            <footer style={{ fontFamily: 'var(--font-sans, Inter, system-ui, sans-serif)', fontSize: 10, color: 'var(--fg-3, #5a5e66)', textAlign: 'right', paddingRight: 4 }}>
                Drag knobs to adjust · shift-drag fine · double-click reset · {meters.inputPeak > 1e-5 ? `peak ${linToDb(meters.inputPeak).toFixed(1)} in / ${linToDb(meters.outputPeak).toFixed(1)} out` : 'idle'}
            </footer>
        </section>
    );
}

// ---------------------------------------------------------------
// Module entry.
// ---------------------------------------------------------------
export default function register(api: ZeusPluginApi) {
    api.registerPanel({
        id: 'cwscaf.main',
        component: () => <ScafPanel api={api} />,
    });
}

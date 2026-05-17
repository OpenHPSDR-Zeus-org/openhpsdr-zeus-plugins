// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus voice audio chain v1 — Compressor UI module.
// Copyright (C) 2025-2026 KB2UKA and contributors.
//
// AetherVoice-inspired layout translated to Zeus tokens per
// project_audio_chain_visual_direction. Centerpiece transfer-function
// curve flanked by knob-style range inputs (Threshold / Ratio / Attack /
// Release / Knee / Makeup) and vertical IN / OUT / GR meters.
//
// Source-only for now; the vite build pipeline that emits
// `compressor.es.js` into this directory at install time ships in a
// later iteration of the plugin-system rebuild (matches the Amplifier
// sample's convention from PR #1).

import { useEffect, useRef, useState } from 'react';

// ---------------------------------------------------------------
// ZeusPluginApi contract — same shape the Amplifier sample uses.
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
};

const DEFAULT_METERS: CompressorMeters = {
    inputPeakDb: -200,
    outputPeakDb: -200,
    gainReductionDb: 0,
};

// ---------------------------------------------------------------
// Knob — range input styled to look like a rotary control, with the
// label above and numeric readout below in monospace. Operator-facing
// drag is via the range input (keyboard / mouse / touch all "just
// work"). The visual knob is decoration — the input itself stays the
// accessibility surface.
// ---------------------------------------------------------------
function Knob({
    label,
    value,
    min,
    max,
    step,
    unit,
    formatValue,
    onChange,
}: {
    label: string;
    value: number;
    min: number;
    max: number;
    step: number;
    unit: string;
    formatValue?: (v: number) => string;
    onChange: (v: number) => void;
}) {
    const display = formatValue ? formatValue(value) : value.toFixed(2);
    return (
        <div className="cmp-knob" style={{
            display: 'flex',
            flexDirection: 'column',
            alignItems: 'center',
            gap: 4,
            minWidth: 68,
        }}>
            <span className="label-xs" style={{
                fontFamily: 'var(--font-sans, Inter, system-ui, sans-serif)',
                fontSize: 11,
                letterSpacing: 0.5,
                textTransform: 'uppercase',
                color: 'var(--fg-2, #b8bcc3)',
            }}>{label}</span>
            <input
                type="range"
                min={min}
                max={max}
                step={step}
                value={value}
                onChange={(e) => onChange(Number(e.currentTarget.value))}
                style={{
                    width: 64,
                    accentColor: 'var(--accent, #4a9eff)',
                    cursor: 'pointer',
                }}
                aria-label={`${label} (${display} ${unit})`}
            />
            <span style={{
                fontFamily: 'var(--font-mono, JetBrains Mono, ui-monospace, monospace)',
                fontSize: 12,
                color: 'var(--fg-0, #e8eaed)',
                tabularNums: 'tabular-nums' as never,
            }}>{display}{unit ? ` ${unit}` : ''}</span>
        </div>
    );
}

// ---------------------------------------------------------------
// Vertical meter — gradient bar (accent → power → tx) with current
// value at the top. Used for IN, OUT, and GR (GR fills downward).
// ---------------------------------------------------------------
function VMeter({
    label,
    valueDb,
    minDb,
    maxDb,
    invert,    // GR meter fills downward
    accent,    // optional color override; defaults to the gradient
}: {
    label: string;
    valueDb: number;
    minDb: number;
    maxDb: number;
    invert?: boolean;
    accent?: string;
}) {
    const clamped = Math.max(minDb, Math.min(maxDb, valueDb));
    const t = (clamped - minDb) / (maxDb - minDb); // 0..1
    const fillPct = Math.round(t * 100);
    return (
        <div className="cmp-vmeter" style={{
            display: 'flex',
            flexDirection: 'column',
            alignItems: 'center',
            gap: 2,
            width: 24,
        }}>
            <span className="label-xs" style={{
                fontFamily: 'var(--font-sans, Inter, system-ui, sans-serif)',
                fontSize: 10,
                letterSpacing: 0.5,
                textTransform: 'uppercase',
                color: 'var(--fg-2, #b8bcc3)',
            }}>{label}</span>
            <div style={{
                position: 'relative',
                width: 12,
                height: 120,
                background: 'var(--bg-inset, #0e1014)',
                borderRadius: 2,
                overflow: 'hidden',
                border: '1px solid var(--line-1, #2a2c30)',
            }}>
                <div style={{
                    position: 'absolute',
                    left: 0,
                    width: '100%',
                    height: `${fillPct}%`,
                    [invert ? 'top' : 'bottom']: 0,
                    background: accent ?? 'linear-gradient(to top, var(--accent, #4a9eff), var(--power, #ffc93a), var(--tx, #e63a2b))',
                    transition: 'height 60ms linear',
                }} />
            </div>
            <span style={{
                fontFamily: 'var(--font-mono, JetBrains Mono, ui-monospace, monospace)',
                fontSize: 10,
                color: 'var(--fg-0, #e8eaed)',
            }}>{valueDb <= -200 ? '—' : valueDb.toFixed(1)}</span>
        </div>
    );
}

// ---------------------------------------------------------------
// Transfer-function curve viz — input dB on X, output dB on Y, with
// the threshold/knee/ratio shape drawn in the accent colour and the
// current operating point as a dot. SVG-based so it crisply scales
// with the panel.
// ---------------------------------------------------------------
function TransferCurve({
    params,
    meters,
}: {
    params: CompressorParams;
    meters: CompressorMeters;
}) {
    const minDb = -60;
    const maxDb = 0;
    const range = maxDb - minDb;
    const W = 240;
    const H = 180;

    const xOf = (db: number) => ((db - minDb) / range) * W;
    const yOf = (db: number) => H - ((db - minDb) / range) * H;

    // Sample the transfer function densely.
    const path: string[] = [];
    const slopeFactor = 1 - 1 / Math.max(1.001, params.ratio);
    const halfKnee = params.kneeDb * 0.5;

    for (let i = 0; i <= 120; i++) {
        const inDb = minDb + (i / 120) * range;
        const over = inDb - params.thresholdDb;
        let grDb: number;
        if (over <= -halfKnee) grDb = 0;
        else if (over >= halfKnee || params.kneeDb <= 0) grDb = over * slopeFactor;
        else {
            const t = (over + halfKnee) / params.kneeDb;
            grDb = slopeFactor * (over + halfKnee) * t * 0.5;
        }
        const outDb = inDb + params.makeupDb - grDb;
        const cmd = i === 0 ? 'M' : 'L';
        path.push(`${cmd} ${xOf(inDb).toFixed(1)} ${yOf(Math.max(minDb, outDb)).toFixed(1)}`);
    }

    // Unity reference line (output = input).
    const unityPath = `M ${xOf(minDb)} ${yOf(minDb)} L ${xOf(maxDb)} ${yOf(maxDb)}`;

    // Threshold marker.
    const threshX = xOf(params.thresholdDb);

    // Current operating point.
    const opIn = Math.max(minDb, Math.min(maxDb, meters.inputPeakDb));
    const opOut = Math.max(minDb, Math.min(maxDb, meters.outputPeakDb));

    return (
        <svg
            viewBox={`0 0 ${W} ${H}`}
            preserveAspectRatio="none"
            style={{
                width: '100%',
                maxWidth: 320,
                height: 'auto',
                background: 'var(--bg-inset, #0e1014)',
                borderRadius: 4,
                border: '1px solid var(--line-1, #2a2c30)',
            }}
            role="img"
            aria-label="Compressor transfer function"
        >
            {/* Grid every 12 dB */}
            {[-48, -36, -24, -12].map((db) => (
                <g key={db}>
                    <line x1={xOf(db)} y1={0} x2={xOf(db)} y2={H} stroke="var(--line-1, #2a2c30)" strokeDasharray="2 2" />
                    <line x1={0} y1={yOf(db)} x2={W} y2={yOf(db)} stroke="var(--line-1, #2a2c30)" strokeDasharray="2 2" />
                </g>
            ))}

            {/* Unity reference line — dimmer */}
            <path d={unityPath} stroke="var(--fg-3, #5a5e66)" strokeWidth={1} fill="none" />

            {/* Threshold vertical marker */}
            <line x1={threshX} y1={0} x2={threshX} y2={H} stroke="var(--power, #ffc93a)" strokeWidth={1} strokeDasharray="3 3" opacity={0.7} />

            {/* Transfer function — accent colour, weighty */}
            <path d={path.join(' ')} stroke="var(--accent, #4a9eff)" strokeWidth={2.2} fill="none" />

            {/* Current operating point */}
            {meters.inputPeakDb > minDb && (
                <circle cx={xOf(opIn)} cy={yOf(opOut)} r={3.5} fill="var(--amber, #FFA028)" stroke="var(--bg-0, #1a1c20)" strokeWidth={1} />
            )}
        </svg>
    );
}

// ---------------------------------------------------------------
// Main panel — brass-plate header + transfer curve + knob row + meters.
// ---------------------------------------------------------------
function CompressorPanel({ api }: { api: ZeusPluginApi }) {
    const [params, setParams] = useState<CompressorParams>(DEFAULT_PARAMS);
    const [meters, setMeters] = useState<CompressorMeters>(DEFAULT_METERS);
    const lastPostedRef = useRef<CompressorParams>(DEFAULT_PARAMS);

    // Hydrate params on first mount.
    useEffect(() => {
        let active = true;
        (async () => {
            const res = await api.callBackend('GET', '/params');
            if (active && res.ok) {
                const p = await res.json() as CompressorParams;
                setParams(p);
                lastPostedRef.current = p;
            }
        })();
        return () => { active = false; };
    }, [api]);

    // Poll meters at 15 Hz — comfortable for a TX-time GR meter
    // without flooding the backend.
    useEffect(() => {
        let active = true;
        const tick = async () => {
            const res = await api.callBackend('GET', '/meters');
            if (active && res.ok) {
                const m = await res.json() as CompressorMeters;
                setMeters(m);
            }
        };
        const t = setInterval(tick, 66);
        tick();
        return () => { active = false; clearInterval(t); };
    }, [api]);

    // Debounced POST — operator drags a knob, we push the partial update
    // after a short settle to avoid pinning the backend with one POST per
    // pointermove event.
    const postDebounceRef = useRef<number | null>(null);
    const schedulePost = (next: CompressorParams) => {
        setParams(next);
        if (postDebounceRef.current != null) {
            window.clearTimeout(postDebounceRef.current);
        }
        postDebounceRef.current = window.setTimeout(() => {
            const last = lastPostedRef.current;
            const patch: Partial<CompressorParams> = {};
            if (next.thresholdDb !== last.thresholdDb) patch.thresholdDb = next.thresholdDb;
            if (next.ratio       !== last.ratio)       patch.ratio       = next.ratio;
            if (next.attackMs    !== last.attackMs)    patch.attackMs    = next.attackMs;
            if (next.releaseMs   !== last.releaseMs)   patch.releaseMs   = next.releaseMs;
            if (next.kneeDb      !== last.kneeDb)      patch.kneeDb      = next.kneeDb;
            if (next.makeupDb    !== last.makeupDb)    patch.makeupDb    = next.makeupDb;
            if (Object.keys(patch).length === 0) return;
            void api.callBackend('POST', '/params', patch).then(async (res) => {
                if (res.ok) {
                    const echoed = await res.json() as CompressorParams;
                    lastPostedRef.current = echoed;
                    setParams(echoed);
                }
            });
        }, 120);
    };

    return (
        <section className="cmp-panel" style={{
            display: 'flex',
            flexDirection: 'column',
            gap: 10,
            padding: '10px 12px',
            background: 'linear-gradient(180deg, var(--panel-top, #14161a), var(--panel-bot, #0e1014))',
            border: '1px solid var(--line-1, #2a2c30)',
            borderRadius: 6,
            color: 'var(--fg-0, #e8eaed)',
        }}>
            {/* Brass-instrument-plate header — gold rail + soft bloom per v3 Lifted Dark */}
            <header style={{
                position: 'relative',
                padding: '6px 4px 8px',
                borderBottom: '1px solid var(--line-1, #2a2c30)',
                boxShadow: 'inset 0 2px 0 var(--power, #ffc93a)',
            }}>
                <h3 style={{
                    margin: 0,
                    fontFamily: 'var(--font-sans, Inter, system-ui, sans-serif)',
                    fontSize: 13,
                    fontWeight: 600,
                    letterSpacing: 1.5,
                    textTransform: 'uppercase',
                    color: 'var(--fg-0, #e8eaed)',
                    textShadow: '0 0 6px rgba(255, 201, 58, 0.15)',
                }}>Compressor</h3>
            </header>

            <div style={{ display: 'flex', gap: 12, alignItems: 'flex-start' }}>
                {/* Left knob column */}
                <div style={{ display: 'flex', flexDirection: 'column', gap: 10, paddingTop: 6 }}>
                    <Knob label="Threshold" value={params.thresholdDb} min={-60} max={0}   step={0.5} unit="dB" onChange={(v) => schedulePost({ ...params, thresholdDb: v })} formatValue={(v) => v.toFixed(1)} />
                    <Knob label="Ratio"     value={params.ratio}       min={1}   max={20}  step={0.1} unit=":1" onChange={(v) => schedulePost({ ...params, ratio:       v })} formatValue={(v) => v.toFixed(1)} />
                    <Knob label="Knee"      value={params.kneeDb}      min={0}   max={24}  step={0.5} unit="dB" onChange={(v) => schedulePost({ ...params, kneeDb:      v })} formatValue={(v) => v.toFixed(1)} />
                </div>

                {/* Center: transfer curve */}
                <div style={{ flex: '1 1 auto', minWidth: 0 }}>
                    <TransferCurve params={params} meters={meters} />
                </div>

                {/* Right knob column */}
                <div style={{ display: 'flex', flexDirection: 'column', gap: 10, paddingTop: 6 }}>
                    <Knob label="Attack"  value={params.attackMs}  min={0.1} max={100}  step={0.1}  unit="ms" onChange={(v) => schedulePost({ ...params, attackMs:  v })} formatValue={(v) => v.toFixed(1)} />
                    <Knob label="Release" value={params.releaseMs} min={1}   max={2000} step={1}    unit="ms" onChange={(v) => schedulePost({ ...params, releaseMs: v })} formatValue={(v) => v.toFixed(0)} />
                    <Knob label="Makeup"  value={params.makeupDb}  min={0}   max={24}   step={0.5}  unit="dB" onChange={(v) => schedulePost({ ...params, makeupDb:  v })} formatValue={(v) => v.toFixed(1)} />
                </div>

                {/* Meters: IN, OUT, GR */}
                <div style={{ display: 'flex', gap: 6, paddingTop: 6 }}>
                    <VMeter label="IN"  valueDb={meters.inputPeakDb}  minDb={-60} maxDb={0} />
                    <VMeter label="OUT" valueDb={meters.outputPeakDb} minDb={-60} maxDb={0} />
                    <VMeter label="GR"  valueDb={meters.gainReductionDb} minDb={0} maxDb={24} invert accent="var(--tx, #e63a2b)" />
                </div>
            </div>
        </section>
    );
}

// ---------------------------------------------------------------
// Module entry — matches the Amplifier sample's pattern.
// ---------------------------------------------------------------
export default function register(api: ZeusPluginApi) {
    api.registerPanel({
        id: 'compressor.main',
        component: (props: object) => <CompressorPanel api={api} {...props} />,
    });
}

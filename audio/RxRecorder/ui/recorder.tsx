// SPDX-License-Identifier: GPL-2.0-or-later
//
// RX Recorder — UI module. Records the RX audio you hear to WAV via the host's
// read-only RX audio tap. Brass-plate panel matching the other audio plugins;
// animated tape reels, level meter, and a downloadable recordings list.
// Copyright (C) 2026 contributors.

import { useCallback, useEffect, useRef, useState } from 'react';

interface ZeusPluginApi {
    registerPanel(spec: { id: string; component: React.ComponentType }): void;
    callBackend(method: string, path: string, body?: unknown): Promise<Response>;
}

interface RecorderStatus {
    recording: boolean;
    file: string | null;
    seconds: number;
    peak: number;       // linear 0..~1
    dropped: number;
    tapReady: boolean;
}

interface RecordingInfo {
    name: string;
    bytes: number;
    modifiedUnixMs: number;
}

const DEFAULT_STATUS: RecorderStatus = {
    recording: false, file: null, seconds: 0, peak: 0, dropped: 0, tapReady: false,
};

const fmtTime = (s: number): string => {
    const m = Math.floor(s / 60);
    const ss = Math.floor(s % 60);
    return `${m.toString().padStart(2, '0')}:${ss.toString().padStart(2, '0')}`;
};

const fmtSize = (b: number): string =>
    b >= 1024 * 1024 ? `${(b / 1024 / 1024).toFixed(1)} MB` : `${Math.max(1, Math.round(b / 1024))} KB`;

const linToDb = (lin: number): number => (lin <= 1e-6 ? -120 : 20 * Math.log10(lin));

// ---------------------------------------------------------------
// Spinning tape reel.
// ---------------------------------------------------------------
function Reel({ spinning, angle }: { spinning: boolean; angle: number }) {
    return (
        <svg viewBox="-24 -24 48 48" width={44} height={44} aria-hidden="true">
            <circle cx={0} cy={0} r={22} fill="var(--bg-0, #0e1014)" stroke="var(--line-2, #3a3d42)" strokeWidth={1.5} />
            <g transform={`rotate(${angle})`} style={{ transition: spinning ? 'none' : 'opacity 200ms' }}>
                {[0, 60, 120, 180, 240, 300].map((d) => {
                    const rad = (d * Math.PI) / 180;
                    return <line key={d} x1={6 * Math.cos(rad)} y1={6 * Math.sin(rad)} x2={18 * Math.cos(rad)} y2={18 * Math.sin(rad)} stroke={spinning ? 'var(--tx, #e63a2b)' : 'var(--line-2, #3a3d42)'} strokeWidth={2.5} strokeLinecap="round" />;
                })}
                <circle cx={0} cy={0} r={5} fill="var(--bg-2, #1f2226)" stroke="var(--line-2, #3a3d42)" strokeWidth={1} />
            </g>
        </svg>
    );
}

// ---------------------------------------------------------------
// Horizontal peak level meter (dBFS).
// ---------------------------------------------------------------
function LevelBar({ peak }: { peak: number }) {
    const db = linToDb(peak);
    const minDb = -60, maxDb = 0;
    const norm = Math.max(0, Math.min(1, (Math.max(minDb, Math.min(maxDb, db)) - minDb) / (maxDb - minDb)));
    const SEG = 28;
    return (
        <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
            <span style={{ fontFamily: 'var(--font-sans, Inter, system-ui, sans-serif)', fontSize: 9, letterSpacing: 0.6, textTransform: 'uppercase', color: 'var(--fg-2, #b8bcc3)' }}>RX</span>
            <div style={{ display: 'flex', gap: 1, flex: 1, height: 12, background: 'var(--bg-inset, #0e1014)', border: '1px solid var(--line-1, #2a2c30)', borderRadius: 3, padding: 1 }}>
                {Array.from({ length: SEG }, (_, i) => {
                    const segNorm = (i + 0.5) / SEG;
                    const lit = segNorm <= norm;
                    const color = segNorm < 0.6 ? 'var(--accent, #4a9eff)' : segNorm < 0.85 ? 'var(--power, #ffc93a)' : 'var(--tx, #e63a2b)';
                    return <div key={i} style={{ flex: 1, background: lit ? color : 'var(--bg-1, #14161a)', opacity: lit ? 1 : 0.5, borderRadius: 1, transition: 'opacity 40ms linear' }} />;
                })}
            </div>
            <span style={{ fontFamily: 'var(--font-mono, JetBrains Mono, ui-monospace, monospace)', fontSize: 10, color: 'var(--fg-1, #d6d8dc)', width: 34, textAlign: 'right', fontVariantNumeric: 'tabular-nums' }}>{db <= -110 ? '—' : db.toFixed(0)}</span>
        </div>
    );
}

function RecorderPanel({ api }: { api: ZeusPluginApi }) {
    const [status, setStatus] = useState<RecorderStatus>(DEFAULT_STATUS);
    const [list, setList] = useState<RecordingInfo[]>([]);
    const [busy, setBusy] = useState(false);
    const angleRef = useRef(0);
    const [angle, setAngle] = useState(0);

    const refreshList = useCallback(async () => {
        try {
            const res = await api.callBackend('GET', '/recordings');
            if (res.ok) setList((await res.json() as { recordings: RecordingInfo[] }).recordings);
        } catch { /* keep last */ }
    }, [api]);

    // Status poll ~10 Hz (drives timer + meter + reels).
    useEffect(() => {
        let active = true;
        const tick = async () => {
            try {
                const res = await api.callBackend('GET', '/status');
                if (active && res.ok) setStatus(await res.json() as RecorderStatus);
            } catch { /* keep last */ }
        };
        const t = window.setInterval(tick, 100);
        tick();
        return () => { active = false; window.clearInterval(t); };
    }, [api]);

    useEffect(() => { void refreshList(); }, [refreshList]);

    // Reel spin animation while recording.
    useEffect(() => {
        if (!status.recording) return;
        let raf = 0;
        const step = () => {
            angleRef.current = (angleRef.current + 3) % 360;
            setAngle(angleRef.current);
            raf = window.requestAnimationFrame(step);
        };
        raf = window.requestAnimationFrame(step);
        return () => window.cancelAnimationFrame(raf);
    }, [status.recording]);

    const toggle = useCallback(async () => {
        setBusy(true);
        try {
            if (status.recording) {
                await api.callBackend('POST', '/record/stop');
                await refreshList();
            } else {
                await api.callBackend('POST', '/record/start');
            }
        } catch { /* surfaced via status poll */ }
        finally { setBusy(false); }
    }, [api, status.recording, refreshList]);

    const download = useCallback(async (name: string) => {
        try {
            const res = await api.callBackend('GET', `/download/${encodeURIComponent(name)}`);
            if (!res.ok) return;
            const blob = await res.blob();
            const url = URL.createObjectURL(blob);
            const a = document.createElement('a');
            a.href = url; a.download = name;
            document.body.appendChild(a); a.click(); a.remove();
            URL.revokeObjectURL(url);
        } catch { /* ignore */ }
    }, [api]);

    const remove = useCallback(async (name: string) => {
        try {
            await api.callBackend('DELETE', `/recordings/${encodeURIComponent(name)}`);
            await refreshList();
        } catch { /* ignore */ }
    }, [api, refreshList]);

    return (
        <section style={{ display: 'flex', flexDirection: 'column', gap: 12, padding: 12, background: 'linear-gradient(180deg, var(--panel-top, #14161a), var(--panel-bot, #0e1014))', border: '1px solid var(--line-1, #2a2c30)', borderRadius: 8, color: 'var(--fg-0, #e8eaed)', fontFamily: 'var(--font-sans, Inter, system-ui, sans-serif)', boxShadow: '0 1px 0 rgba(255,255,255,0.04) inset, 0 4px 12px rgba(0,0,0,0.25)' }}>
            <header style={{ padding: '8px 6px 10px', borderBottom: '1px solid var(--line-1, #2a2c30)', boxShadow: 'inset 0 2px 0 var(--power, #ffc93a), inset 0 3px 8px rgba(255, 201, 58, 0.12)', display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 12 }}>
                <h3 style={{ margin: 0, fontSize: 13, fontWeight: 600, letterSpacing: 2, textTransform: 'uppercase' }}>RX Recorder</h3>
                <span style={{ fontFamily: 'var(--font-mono, JetBrains Mono, ui-monospace, monospace)', fontSize: 10, color: 'var(--fg-2, #b8bcc3)', letterSpacing: 0.5 }}>RX · tap</span>
            </header>

            {/* Transport */}
            <div style={{ display: 'flex', alignItems: 'center', gap: 14 }}>
                <Reel spinning={status.recording} angle={angle} />
                <div style={{ display: 'flex', flexDirection: 'column', gap: 6, flex: 1 }}>
                    <div style={{ display: 'flex', alignItems: 'baseline', gap: 10 }}>
                        <span style={{ fontFamily: 'var(--font-mono, JetBrains Mono, ui-monospace, monospace)', fontSize: 22, fontWeight: 600, color: status.recording ? 'var(--tx, #e63a2b)' : 'var(--fg-1, #d6d8dc)', fontVariantNumeric: 'tabular-nums', lineHeight: 1 }}>{fmtTime(status.seconds)}</span>
                        <span style={{ fontSize: 10, color: 'var(--fg-3, #5a5e66)' }}>
                            {!status.tapReady ? 'waiting for RX audio…' : status.recording ? 'recording' : 'ready'}
                            {status.dropped > 0 ? ` · ${status.dropped} dropped` : ''}
                        </span>
                    </div>
                    <LevelBar peak={status.peak} />
                </div>
                <button
                    type="button"
                    onClick={toggle}
                    disabled={busy || (!status.tapReady && !status.recording)}
                    title={!status.tapReady ? 'Connect a radio so RX audio is flowing' : status.recording ? 'Stop recording' : 'Start recording'}
                    style={{ width: 70, height: 70, borderRadius: '50%', cursor: status.tapReady ? 'pointer' : 'not-allowed', border: '2px solid ' + (status.recording ? 'var(--tx, #e63a2b)' : 'var(--line-2, #3a3d42)'), background: status.recording ? 'var(--tx, #e63a2b)' : 'var(--bg-2, #1f2226)', color: status.recording ? '#fff' : 'var(--tx, #e63a2b)', fontSize: 11, fontWeight: 700, letterSpacing: 1, textTransform: 'uppercase', boxShadow: status.recording ? '0 0 14px rgba(230,58,43,0.55)' : 'inset 0 1px 0 rgba(255,255,255,0.05)', opacity: (!status.tapReady && !status.recording) ? 0.4 : 1, transition: 'all 140ms ease-out' }}
                >
                    {status.recording ? '■ Stop' : '● Rec'}
                </button>
            </div>

            {/* Recordings list */}
            <div style={{ display: 'flex', flexDirection: 'column', gap: 4, maxHeight: 200, overflowY: 'auto' }}>
                {list.length === 0 && (
                    <span style={{ fontSize: 11, color: 'var(--fg-3, #5a5e66)', padding: '6px 2px' }}>No recordings yet.</span>
                )}
                {list.map((r) => (
                    <div key={r.name} style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '5px 8px', background: 'var(--bg-1, #14161a)', border: '1px solid var(--line-1, #2a2c30)', borderRadius: 4 }}>
                        <span style={{ flex: 1, fontFamily: 'var(--font-mono, JetBrains Mono, ui-monospace, monospace)', fontSize: 11, color: 'var(--fg-1, #d6d8dc)', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{r.name.replace(/^rx-/, '')}</span>
                        <span style={{ fontSize: 10, color: 'var(--fg-3, #5a5e66)', fontVariantNumeric: 'tabular-nums' }}>{fmtSize(r.bytes)}</span>
                        <button type="button" onClick={() => download(r.name)} title="Download" style={iconBtn}>↓</button>
                        <button type="button" onClick={() => remove(r.name)} title="Delete" disabled={status.recording && status.file === r.name} style={{ ...iconBtn, color: 'var(--tx, #e63a2b)' }}>✕</button>
                    </div>
                ))}
            </div>

            <footer style={{ fontSize: 10, color: 'var(--fg-3, #5a5e66)', textAlign: 'right', paddingRight: 4 }}>
                Records the demodulated RX audio · WAV float32 48 kHz mono
            </footer>
        </section>
    );
}

const iconBtn: React.CSSProperties = {
    width: 24, height: 22, borderRadius: 3, cursor: 'pointer',
    border: '1px solid var(--line-2, #3a3d42)', background: 'var(--bg-2, #1f2226)',
    color: 'var(--fg-1, #d6d8dc)', fontSize: 12, lineHeight: 1, padding: 0,
};

export default function register(api: ZeusPluginApi) {
    api.registerPanel({
        id: 'rxrecorder.main',
        component: () => <RecorderPanel api={api} />,
    });
}

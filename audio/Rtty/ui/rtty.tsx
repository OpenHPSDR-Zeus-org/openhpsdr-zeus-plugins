// SPDX-License-Identifier: GPL-2.0-or-later
//
// RTTY — UI module. Shows decoded Baudot/ITA2 text from the host RX audio tap,
// a mark/space tuning indicator, and tuning controls (mark tone, shift, baud,
// reverse, unshift-on-space). Brass-plate panel matching the other audio
// plugins. Receive only for now. Copyright (C) 2026 contributors.

import { useCallback, useEffect, useRef, useState } from 'react';

interface ZeusPluginApi {
    registerPanel(spec: { id: string; component: React.ComponentType }): void;
    callBackend(method: string, path: string, body?: unknown): Promise<Response>;
}

interface RttyStatus {
    tapReady: boolean;
    markHz: number;
    shiftHz: number;
    baud: number;
    reverse: boolean;
    usos: boolean;
    markLevel: number;
    spaceLevel: number;
    markHigh: boolean;
    charCount: number;
    text: string;
}

const DEFAULT_STATUS: RttyStatus = {
    tapReady: false, markHz: 2125, shiftHz: 170, baud: 45.45, reverse: false, usos: true,
    markLevel: 0, spaceLevel: 0, markHigh: true, charCount: 0, text: '',
};

const BAUDS = [45.45, 50, 75, 100];
const SHIFTS = [170, 425, 850];

// Map a raw tone power (unbounded) to 0..1 for the meters. Powers are small
// (~mic-level squared); a log-ish curve keeps low signals visible.
const norm = (p: number): number => {
    const db = 10 * Math.log10(Math.max(p, 1e-9));
    return Math.max(0, Math.min(1, (db + 70) / 70)); // -70 dB → 0, 0 dB → 1
};

function ToneBar({ label, value, high, color }: { label: string; value: number; high: boolean; color: string }) {
    return (
        <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
            <span style={{ width: 44, fontFamily: 'var(--font-mono, ui-monospace, monospace)', fontSize: 10, fontWeight: 600, letterSpacing: 0.5, color: high ? color : 'var(--fg-2, #b8bcc3)' }}>{label}</span>
            <div style={{ flex: 1, height: 10, background: 'var(--bg-inset, #0e1014)', border: '1px solid var(--line-1, #2a2c30)', borderRadius: 3, overflow: 'hidden' }}>
                <div style={{ width: `${Math.round(value * 100)}%`, height: '100%', background: color, opacity: high ? 1 : 0.55, transition: 'width 60ms linear' }} />
            </div>
            <span style={{ width: 14, textAlign: 'center', fontSize: 11, color: high ? color : 'var(--fg-3, #5a5e66)' }}>{high ? '●' : '○'}</span>
        </div>
    );
}

function RttyPanel({ api }: { api: ZeusPluginApi }) {
    const [status, setStatus] = useState<RttyStatus>(DEFAULT_STATUS);
    const textRef = useRef<HTMLDivElement>(null);
    const atBottomRef = useRef(true);

    const post = useCallback(async (body: Partial<RttyStatus>) => {
        try { await api.callBackend('POST', '/params', body); } catch { /* status poll reconciles */ }
    }, [api]);

    useEffect(() => {
        let active = true;
        const tick = async () => {
            try {
                const res = await api.callBackend('GET', '/status');
                if (active && res.ok) setStatus(await res.json() as RttyStatus);
            } catch { /* keep last */ }
        };
        const t = window.setInterval(tick, 150);
        tick();
        return () => { active = false; window.clearInterval(t); };
    }, [api]);

    // Keep the text view pinned to the bottom unless the operator scrolled up.
    useEffect(() => {
        const el = textRef.current;
        if (el && atBottomRef.current) el.scrollTop = el.scrollHeight;
    }, [status.text]);

    const onScroll = useCallback(() => {
        const el = textRef.current;
        if (el) atBottomRef.current = el.scrollHeight - el.scrollTop - el.clientHeight < 24;
    }, []);

    const clear = useCallback(async () => {
        try { await api.callBackend('POST', '/clear'); setStatus((s) => ({ ...s, text: '' })); } catch { /* ignore */ }
    }, [api]);

    const setField = useCallback((patch: Partial<RttyStatus>) => {
        setStatus((s) => ({ ...s, ...patch }));
        void post(patch);
    }, [post]);

    const tuned = Math.abs(norm(status.markLevel) - norm(status.spaceLevel)) > 0.08 || (status.markLevel + status.spaceLevel > 1e-6);

    return (
        <section style={panel}>
            <header style={header}>
                <h3 style={{ margin: 0, fontSize: 13, fontWeight: 600, letterSpacing: 2, textTransform: 'uppercase' }}>RTTY</h3>
                <span style={{ fontFamily: 'var(--font-mono, ui-monospace, monospace)', fontSize: 10, color: status.tapReady ? 'var(--accent, #4a9eff)' : 'var(--fg-3, #5a5e66)', letterSpacing: 1 }}>
                    {status.tapReady ? `${status.charCount} chr` : 'waiting for audio…'}
                </span>
            </header>

            {/* Tuning indicator */}
            <div style={{ display: 'flex', flexDirection: 'column', gap: 5 }}>
                <ToneBar label="MARK" value={norm(status.markLevel)} high={status.markHigh} color="var(--accent, #4a9eff)" />
                <ToneBar label="SPACE" value={norm(status.spaceLevel)} high={!status.markHigh} color="var(--power, #ffc93a)" />
            </div>

            {/* Decoded text */}
            <div ref={textRef} onScroll={onScroll} style={textView}>
                {status.text || <span style={{ color: 'var(--fg-3, #5a5e66)' }}>Tune a RTTY signal so the mark/space bars peak…</span>}
            </div>

            {/* Controls */}
            <div style={{ display: 'flex', flexWrap: 'wrap', gap: 8, alignItems: 'center' }}>
                <Field label="Mark Hz">
                    <input type="number" value={Math.round(status.markHz)} min={300} max={5000} step={5}
                        onChange={(e) => setField({ markHz: Number(e.target.value) })} style={numInput} />
                </Field>
                <Field label="Shift">
                    <select value={status.shiftHz} onChange={(e) => setField({ shiftHz: Number(e.target.value) })} style={select}>
                        {SHIFTS.map((s) => <option key={s} value={s}>{s}</option>)}
                    </select>
                </Field>
                <Field label="Baud">
                    <select value={status.baud} onChange={(e) => setField({ baud: Number(e.target.value) })} style={select}>
                        {BAUDS.map((b) => <option key={b} value={b}>{b}</option>)}
                    </select>
                </Field>
                <Toggle label="REV" on={status.reverse} onClick={() => setField({ reverse: !status.reverse })} title="Swap mark/space (USB vs LSB)" />
                <Toggle label="USOS" on={status.usos} onClick={() => setField({ usos: !status.usos })} title="Unshift on space" />
                <button type="button" onClick={clear} style={clearBtn} title="Clear decoded text">Clear</button>
            </div>

            <footer style={{ fontSize: 10, color: 'var(--fg-3, #5a5e66)', textAlign: 'right' }}>
                {tuned ? `${status.markHz | 0}/${(status.markHz + status.shiftHz) | 0} Hz · ${status.baud} baud` : 'read-only RX tap · receive only'}
            </footer>
        </section>
    );
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
    return (
        <label style={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
            <span style={{ fontSize: 8, letterSpacing: 0.8, textTransform: 'uppercase', color: 'var(--fg-2, #b8bcc3)' }}>{label}</span>
            {children}
        </label>
    );
}

function Toggle({ label, on, onClick, title }: { label: string; on: boolean; onClick: () => void; title: string }) {
    return (
        <button type="button" onClick={onClick} aria-pressed={on} title={title}
            style={{ padding: '5px 9px', alignSelf: 'flex-end', fontFamily: 'var(--font-mono, ui-monospace, monospace)', fontSize: 10, fontWeight: 600, letterSpacing: 0.5, color: on ? '#fff' : 'var(--fg-1, #d6d8dc)', background: on ? 'var(--accent, #4a9eff)' : 'var(--bg-2, #1f2226)', border: '1px solid ' + (on ? 'var(--accent, #4a9eff)' : 'var(--line-2, #3a3d42)'), borderRadius: 3, cursor: 'pointer', boxShadow: on ? '0 0 8px rgba(74,158,255,0.4)' : 'inset 0 1px 0 rgba(255,255,255,0.04)' }}>
            {label}
        </button>
    );
}

const panel: React.CSSProperties = {
    display: 'flex', flexDirection: 'column', gap: 12, padding: 12,
    background: 'linear-gradient(180deg, var(--panel-top, #14161a), var(--panel-bot, #0e1014))',
    border: '1px solid var(--line-1, #2a2c30)', borderRadius: 8, color: 'var(--fg-0, #e8eaed)',
    fontFamily: 'var(--font-sans, Inter, system-ui, sans-serif)',
    boxShadow: '0 1px 0 rgba(255,255,255,0.04) inset, 0 4px 12px rgba(0,0,0,0.25)',
};
const header: React.CSSProperties = {
    padding: '8px 6px 10px', borderBottom: '1px solid var(--line-1, #2a2c30)',
    boxShadow: 'inset 0 2px 0 var(--accent, #4a9eff), inset 0 3px 8px rgba(74,158,255,0.12)',
    display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 12,
};
const textView: React.CSSProperties = {
    height: 150, overflowY: 'auto', padding: '8px 10px',
    background: 'var(--bg-inset, #0e1014)', border: '1px solid var(--line-1, #2a2c30)', borderRadius: 4,
    fontFamily: 'var(--font-mono, JetBrains Mono, ui-monospace, monospace)', fontSize: 12, lineHeight: 1.5,
    color: 'var(--fg-0, #e8eaed)', whiteSpace: 'pre-wrap', wordBreak: 'break-word',
};
const numInput: React.CSSProperties = {
    width: 60, padding: '4px 6px', fontFamily: 'var(--font-mono, ui-monospace, monospace)', fontSize: 11,
    color: 'var(--fg-0, #e8eaed)', background: 'var(--bg-2, #1f2226)', border: '1px solid var(--line-2, #3a3d42)', borderRadius: 3,
};
const select: React.CSSProperties = { ...numInput, width: 64 };
const clearBtn: React.CSSProperties = {
    padding: '5px 12px', alignSelf: 'flex-end', fontSize: 11, fontWeight: 600,
    color: 'var(--fg-1, #d6d8dc)', background: 'var(--bg-2, #1f2226)',
    border: '1px solid var(--line-2, #3a3d42)', borderRadius: 3, cursor: 'pointer',
};

export default function register(api: ZeusPluginApi) {
    api.registerPanel({
        id: 'rtty.main',
        component: () => <RttyPanel api={api} />,
    });
}

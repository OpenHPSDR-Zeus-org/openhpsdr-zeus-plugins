// SPDX-License-Identifier: GPL-2.0-or-later
//
// 4O3A Antenna Genius — Openhpsdr-Zeus plugin UI module.
//
// Ported from Log4YM's `AntennaGeniusPlugin.tsx` widget. The original used the
// Log4YM SignalR hub + a GlassPanel + lucide-react icons + dark-* / accent-* /
// glass-* Tailwind tokens — none of which exist in Zeus. Here we poll the
// plugin's own backend (GET /status, 1 Hz, mirroring the Amplifier sample),
// post antenna selections to POST /select-antenna, and use plain HTML +
// inline <style> with token-friendly fallbacks.
//
// Contract: the module's default export receives a ZeusPluginApi instance and
// registers one panel (id `antennagenius.main`).

import { useEffect, useState } from 'react';

// ---------------------------------------------------------------------------
// ZeusPluginApi — copied from the host plugin runtime contract (the host
// supplies this at register time; redeclaring it here keeps the module
// self-contained).
// ---------------------------------------------------------------------------

interface ZeusPluginApi {
    registerPanel(spec: { id: string; component: React.ComponentType }): void;
    callBackend(method: string, path: string, body?: unknown): Promise<Response>;
}

// ---------------------------------------------------------------------------
// DTOs — must match Openhpsdr.Zeus.Plugins.AntennaGenius records (camelCase
// via JsonPropertyName on the backend).
// ---------------------------------------------------------------------------

interface AntennaInfo {
    id: number;
    name: string;
    txBandMask: number;
    rxBandMask: number;
    inbandMask: number;
}

interface BandInfo {
    id: number;
    name: string;
    freqStart: number;
    freqStop: number;
}

interface PortStatus {
    portId: number;
    auto: boolean;
    source: string;
    band: number;
    rxAntenna: number;
    txAntenna: number;
    isTransmitting: boolean;
    isInhibited: boolean;
}

interface AntennaGeniusStatus {
    deviceSerial: string;
    deviceName: string;
    ipAddress: string;
    version: string;
    isConnected: boolean;
    antennas: AntennaInfo[];
    bands: BandInfo[];
    portA: PortStatus;
    portB: PortStatus;
}

interface ManualSettings {
    manualIpAddress: string;
    manualPort: number;
}

interface TestResult {
    ok: boolean;
    version: string;
    error: string | null;
}

// ---------------------------------------------------------------------------
// Styles — single <style> tag injected once. Token vars (--accent, --tx,
// --panel-bot, --border-mid, --text-primary) are referenced where present on
// the host; the literal fallbacks keep the panel readable if the module is
// loaded outside Zeus (e.g. in a docs preview).
// ---------------------------------------------------------------------------

const STYLES = `
.antgen-panel {
    font-family: 'Archivo Narrow', system-ui, sans-serif;
    color: var(--text-primary, #e6ebf2);
    background: var(--panel-bot, #1a1f26);
    border: 1px solid var(--border-mid, #2a323d);
    border-radius: 6px;
    padding: 12px;
    display: flex;
    flex-direction: column;
    gap: 12px;
}
.antgen-empty {
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    padding: 24px;
    text-align: center;
    color: var(--text-muted, #8a95a3);
}
.antgen-empty .antgen-empty-glyph {
    font-size: 28px;
    line-height: 1;
    margin-bottom: 8px;
    opacity: 0.6;
}
.antgen-empty .antgen-empty-hint {
    font-size: 12px;
    margin-top: 4px;
    opacity: 0.8;
}
.antgen-header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 8px;
    border-bottom: 1px solid var(--border-mid, #2a323d);
    padding-bottom: 8px;
}
.antgen-header-title {
    font-weight: 600;
    letter-spacing: 0.02em;
}
.antgen-header-meta {
    font-family: ui-monospace, 'JetBrains Mono', monospace;
    font-size: 12px;
    color: var(--text-muted, #8a95a3);
    display: flex;
    align-items: center;
    gap: 6px;
}
.antgen-header-meta .antgen-sep { opacity: 0.4; }
.antgen-conn {
    display: inline-flex;
    align-items: center;
    gap: 4px;
    font-size: 12px;
}
.antgen-conn.is-on  { color: var(--accent, #4a9eff); }
.antgen-conn.is-off { color: var(--text-muted, #8a95a3); }
.antgen-conn-glyph {
    display: inline-block;
    width: 10px;
    text-align: center;
    line-height: 1;
}
.antgen-ports {
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: 8px;
}
.antgen-port {
    background: var(--panel-top, #232a33);
    border: 1px solid var(--border-mid, #2a323d);
    border-radius: 4px;
    padding: 8px 10px;
    display: flex;
    flex-direction: column;
    gap: 4px;
}
.antgen-port-row {
    display: flex;
    align-items: center;
    justify-content: space-between;
}
.antgen-port-label {
    font-size: 12px;
    font-weight: 600;
}
.antgen-port-a .antgen-port-label { color: var(--accent, #4a9eff); }
.antgen-port-b .antgen-port-label { color: #2fbf71; }
.antgen-port-band {
    font-family: ui-monospace, 'JetBrains Mono', monospace;
    font-size: 16px;
    font-weight: 700;
    color: var(--text-primary, #e6ebf2);
}
.antgen-tx {
    display: inline-flex;
    align-items: center;
    gap: 3px;
    font-size: 11px;
    font-weight: 700;
    color: var(--tx, #e63a2b);
    animation: antgen-pulse 1.2s ease-in-out infinite;
}
@keyframes antgen-pulse {
    0%, 100% { opacity: 1; }
    50%      { opacity: 0.55; }
}
.antgen-list {
    display: flex;
    flex-direction: column;
    gap: 4px;
}
.antgen-row {
    display: grid;
    grid-template-columns: 32px 1fr 32px;
    gap: 8px;
    align-items: center;
    padding: 4px 6px;
    background: var(--panel-top, #232a33);
    border: 1px solid var(--border-mid, #2a323d);
    border-radius: 4px;
}
.antgen-row.is-selected { background: var(--panel-bot, #1a1f26); }
.antgen-row-name {
    font-family: ui-monospace, 'JetBrains Mono', monospace;
    font-size: 13px;
    color: var(--text-primary, #e6ebf2);
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
}
.antgen-row-name.is-muted { color: var(--text-muted, #8a95a3); }
.antgen-btn {
    width: 28px;
    height: 28px;
    border-radius: 4px;
    border: 1px solid var(--border-mid, #2a323d);
    background: var(--panel-bot, #1a1f26);
    color: var(--text-muted, #8a95a3);
    font-size: 12px;
    font-weight: 700;
    cursor: pointer;
    transition: background-color 100ms ease, color 100ms ease;
}
.antgen-btn:hover { background: var(--panel-top, #232a33); color: var(--text-primary, #e6ebf2); }
.antgen-btn.is-a.is-on {
    background: var(--accent, #4a9eff);
    color: #ffffff;
    border-color: var(--accent, #4a9eff);
}
.antgen-btn.is-b.is-on {
    background: #2fbf71;
    color: #ffffff;
    border-color: #2fbf71;
}
.antgen-btn.is-on.is-tx {
    background: var(--tx, #e63a2b);
    border-color: var(--tx, #e63a2b);
    color: #ffffff;
    animation: antgen-pulse 1s ease-in-out infinite;
}
.antgen-settings {
    border-top: 1px solid var(--border-mid, #2a323d);
    padding-top: 10px;
    display: flex;
    flex-direction: column;
    gap: 8px;
}
.antgen-settings-title {
    font-size: 12px;
    font-weight: 600;
    letter-spacing: 0.02em;
    color: var(--text-primary, #e6ebf2);
}
.antgen-settings-hint {
    font-size: 11px;
    color: var(--text-muted, #8a95a3);
    line-height: 1.4;
}
.antgen-settings-row {
    display: flex;
    gap: 8px;
}
.antgen-field {
    display: flex;
    flex-direction: column;
    gap: 3px;
    flex: 1;
    font-size: 11px;
    color: var(--text-muted, #8a95a3);
}
.antgen-field-port { flex: 0 0 84px; }
.antgen-field input {
    font-family: ui-monospace, 'JetBrains Mono', monospace;
    font-size: 13px;
    color: var(--text-primary, #e6ebf2);
    background: var(--panel-bot, #1a1f26);
    border: 1px solid var(--border-mid, #2a323d);
    border-radius: 4px;
    padding: 5px 7px;
}
.antgen-field input:focus {
    outline: none;
    border-color: var(--accent, #4a9eff);
}
.antgen-settings-actions {
    display: flex;
    align-items: center;
    gap: 8px;
    flex-wrap: wrap;
}
.antgen-action {
    font-family: 'Archivo Narrow', system-ui, sans-serif;
    font-size: 12px;
    font-weight: 600;
    padding: 5px 12px;
    border-radius: 4px;
    border: 1px solid var(--accent, #4a9eff);
    background: var(--accent, #4a9eff);
    color: #ffffff;
    cursor: pointer;
    transition: opacity 100ms ease;
}
.antgen-action.is-secondary {
    background: var(--panel-bot, #1a1f26);
    color: var(--text-primary, #e6ebf2);
    border-color: var(--border-mid, #2a323d);
}
.antgen-action:disabled {
    opacity: 0.5;
    cursor: default;
}
.antgen-settings-status {
    font-size: 11px;
    color: var(--text-muted, #8a95a3);
}
`;

let stylesInjected = false;
function ensureStyles() {
    if (stylesInjected || typeof document === 'undefined') return;
    const tag = document.createElement('style');
    tag.setAttribute('data-antgen', '1');
    tag.textContent = STYLES;
    document.head.appendChild(tag);
    stylesInjected = true;
}

// ---------------------------------------------------------------------------
// Components
// ---------------------------------------------------------------------------

function ConnIndicator({ on }: { on: boolean }) {
    return (
        <span className={`antgen-conn ${on ? 'is-on' : 'is-off'}`}>
            <span className="antgen-conn-glyph">{on ? '●' : '○'}</span>
            {on ? 'Connected' : 'Disconnected'}
        </span>
    );
}

function PortHeader({
    label,
    portClass,
    band,
    isTransmitting,
}: {
    label: string;
    portClass: 'antgen-port-a' | 'antgen-port-b';
    band: BandInfo | undefined;
    isTransmitting: boolean;
}) {
    return (
        <div className={`antgen-port ${portClass}`}>
            <div className="antgen-port-row">
                <span className="antgen-port-label">{label}</span>
                {isTransmitting && <span className="antgen-tx">TX</span>}
            </div>
            <div className="antgen-port-band">{band?.name ?? 'None'}</div>
        </div>
    );
}

function AntennaRow({
    antenna,
    device,
    onSelectA,
    onSelectB,
}: {
    antenna: AntennaInfo;
    device: AntennaGeniusStatus;
    onSelectA: () => void;
    onSelectB: () => void;
}) {
    const isSelectedA =
        device.portA.rxAntenna === antenna.id || device.portA.txAntenna === antenna.id;
    const isSelectedB =
        device.portB.rxAntenna === antenna.id || device.portB.txAntenna === antenna.id;
    const isTxA = device.portA.isTransmitting && device.portA.txAntenna === antenna.id;
    const isTxB = device.portB.isTransmitting && device.portB.txAntenna === antenna.id;

    const rowClass = `antgen-row${isSelectedA || isSelectedB ? ' is-selected' : ''}`;
    const nameClass = `antgen-row-name${isSelectedA || isSelectedB ? '' : ' is-muted'}`;

    const btnAClass = `antgen-btn is-a${isSelectedA ? ' is-on' : ''}${isTxA ? ' is-tx' : ''}`;
    const btnBClass = `antgen-btn is-b${isSelectedB ? ' is-on' : ''}${isTxB ? ' is-tx' : ''}`;

    return (
        <div className={rowClass}>
            <button className={btnAClass} onClick={onSelectA} title="Select for Radio A">A</button>
            <span className={nameClass}>{antenna.name}</span>
            <button className={btnBClass} onClick={onSelectB} title="Select for Radio B">B</button>
        </div>
    );
}

function ConnectionSettings({ api }: { api: ZeusPluginApi }) {
    const [ip, setIp] = useState('');
    const [port, setPort] = useState(9007);
    const [loaded, setLoaded] = useState(false);
    const [saving, setSaving] = useState(false);
    const [testing, setTesting] = useState(false);
    const [status, setStatus] = useState<string | null>(null);

    useEffect(() => {
        let active = true;
        (async () => {
            try {
                const res = await api.callBackend('GET', '/settings');
                if (!active) return;
                if (res.ok) {
                    const s = (await res.json()) as ManualSettings;
                    setIp(s.manualIpAddress ?? '');
                    setPort(Number.isFinite(s.manualPort) ? s.manualPort : 9007);
                }
            } catch {
                /* leave defaults */
            } finally {
                if (active) setLoaded(true);
            }
        })();
        return () => { active = false; };
    }, [api]);

    const portValid = Number.isFinite(port) && port >= 1 && port <= 65535;
    const canSave = loaded && portValid && !saving && !testing;
    const canTest = loaded && ip.trim().length > 0 && portValid && !saving && !testing;

    const save = async () => {
        setSaving(true);
        setStatus(null);
        try {
            const res = await api.callBackend('POST', '/settings', {
                manualIpAddress: ip.trim(),
                manualPort: port,
            });
            setStatus(res.ok ? 'Saved' : 'Save failed');
        } catch {
            setStatus('Save failed');
        } finally {
            setSaving(false);
        }
    };

    const test = async () => {
        setTesting(true);
        setStatus(null);
        try {
            const res = await api.callBackend('POST', '/test', {
                ipAddress: ip.trim(),
                port,
            });
            if (res.ok) {
                const r = (await res.json()) as TestResult;
                setStatus(
                    r.ok
                        ? `Reached device${r.version ? ` (firmware ${r.version})` : ''}`
                        : `No response: ${r.error ?? 'unreachable'}`,
                );
            } else {
                setStatus('Test failed');
            }
        } catch {
            setStatus('Test failed');
        } finally {
            setTesting(false);
        }
    };

    return (
        <div className="antgen-settings">
            <div className="antgen-settings-title">Connection settings</div>
            <div className="antgen-settings-hint">
                If your switch blocks discovery broadcasts, enter the Antenna Genius IP
                and port to connect directly. Leave the IP blank to use auto-discovery.
            </div>
            <div className="antgen-settings-row">
                <label className="antgen-field">
                    <span>IP address</span>
                    <input
                        type="text"
                        placeholder="192.168.10.24"
                        value={ip}
                        onChange={(e) => setIp(e.target.value)}
                        disabled={!loaded}
                    />
                </label>
                <label className="antgen-field antgen-field-port">
                    <span>Port</span>
                    <input
                        type="number"
                        min={1}
                        max={65535}
                        value={Number.isFinite(port) ? port : ''}
                        onChange={(e) => setPort(parseInt(e.target.value, 10))}
                        disabled={!loaded}
                    />
                </label>
            </div>
            <div className="antgen-settings-actions">
                <button className="antgen-action" onClick={save} disabled={!canSave}>
                    {saving ? 'Saving…' : 'Save'}
                </button>
                <button
                    className="antgen-action is-secondary"
                    onClick={test}
                    disabled={!canTest}
                >
                    {testing ? 'Testing…' : 'Test connection'}
                </button>
                {status && <span className="antgen-settings-status">{status}</span>}
            </div>
        </div>
    );
}

function AntennaGeniusPanel({ api }: { api: ZeusPluginApi }) {
    const [devices, setDevices] = useState<AntennaGeniusStatus[] | null>(null);

    useEffect(() => { ensureStyles(); }, []);

    useEffect(() => {
        let active = true;
        const tick = async () => {
            try {
                const res = await api.callBackend('GET', '/status');
                if (!active) return;
                if (res.ok) {
                    const body = await res.json();
                    setDevices(Array.isArray(body) ? body : []);
                } else {
                    setDevices([]);
                }
            } catch {
                if (active) setDevices([]);
            }
        };
        tick();
        const t = setInterval(tick, 1000);
        return () => { active = false; clearInterval(t); };
    }, [api]);

    const selectAntenna = (serial: string, portId: number, antennaId: number) => {
        void api.callBackend('POST', '/select-antenna', { serial, portId, antennaId });
    };

    if (devices === null) {
        return <div className="antgen-panel">Connecting…</div>;
    }

    if (devices.length === 0) {
        return (
            <div className="antgen-panel">
                <div className="antgen-empty">
                    <div className="antgen-empty-glyph">○</div>
                    <div>No Antenna Genius devices found</div>
                    <div className="antgen-empty-hint">Waiting for discovery on UDP port 9007…</div>
                </div>
                <ConnectionSettings api={api} />
            </div>
        );
    }

    // For v1 we surface the first device, matching the Log4YM widget. Multi-
    // device support can come later (tabs / selector).
    const device = devices[0];
    const portABand = device.bands.find(b => b.id === device.portA.band);
    const portBBand = device.bands.find(b => b.id === device.portB.band);

    return (
        <div className="antgen-panel">
            <div className="antgen-header">
                <span className="antgen-header-title">{device.deviceName || 'Antenna Genius'}</span>
                <span className="antgen-header-meta">
                    <span>v{device.version || '?'}</span>
                    <span className="antgen-sep">|</span>
                    <span>{device.ipAddress}</span>
                    <span className="antgen-sep">|</span>
                    <ConnIndicator on={device.isConnected} />
                </span>
            </div>

            <div className="antgen-ports">
                <PortHeader
                    label="Radio A"
                    portClass="antgen-port-a"
                    band={portABand}
                    isTransmitting={device.portA.isTransmitting}
                />
                <PortHeader
                    label="Radio B"
                    portClass="antgen-port-b"
                    band={portBBand}
                    isTransmitting={device.portB.isTransmitting}
                />
            </div>

            <div className="antgen-list">
                {device.antennas.map((antenna) => (
                    <AntennaRow
                        key={antenna.id}
                        antenna={antenna}
                        device={device}
                        onSelectA={() => selectAntenna(device.deviceSerial, 1, antenna.id)}
                        onSelectB={() => selectAntenna(device.deviceSerial, 2, antenna.id)}
                    />
                ))}
            </div>

            <ConnectionSettings api={api} />
        </div>
    );
}

// ---------------------------------------------------------------------------
// Module entrypoint
// ---------------------------------------------------------------------------

export default function register(api: ZeusPluginApi) {
    api.registerPanel({
        id: 'antennagenius.main',
        component: (props: object) => <AntennaGeniusPanel api={api} {...props} />,
    });
}

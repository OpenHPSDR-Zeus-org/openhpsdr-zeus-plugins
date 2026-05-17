// SPDX-License-Identifier: GPL-2.0-or-later
//
// Openhpsdr-Zeus plugin UI for the 4O3A Tuner Genius XL.
// Source-only — iter 5 of the plugin-system rebuild emits tgxl.es.js
// alongside this file. The module is self-contained: no imports from the
// Zeus web tree (useAppStore, GlassPanel, etc. are NOT in the sandbox).

import { useEffect, useState } from 'react';

interface ZeusPluginApi {
    registerPanel(spec: { id: string; component: React.ComponentType }): void;
    callBackend(method: string, path: string, body?: unknown): Promise<Response>;
}

interface TgxlPortStatus {
    portId: number;
    auto: boolean;
    band: string;
    frequencyMhz: number;
    swrX10: number;
    isTuning: boolean;
    isTransmitting: boolean;
    tuneResult: string;
}

interface TgxlStatus {
    deviceSerial: string;
    deviceName: string;
    ipAddress: string;
    version: string;
    model: string;
    isConnected: boolean;
    isOperating: boolean;
    isBypassed: boolean;
    isTuning: boolean;
    activeRadio: number;
    forwardPowerWatts: number;
    swr: number;
    l: number;
    c1: number;
    c2: number;
    freqAMhz: number;
    freqBMhz: number;
    portA: TgxlPortStatus | null;
    portB: TgxlPortStatus | null;
}

const styles = `
.tgxl-panel { font-family: 'Archivo Narrow', system-ui, sans-serif; color: #d8dde6; padding: 12px; }
.tgxl-panel h3 { margin: 0 0 10px 0; font-size: 14px; letter-spacing: 0.04em; text-transform: uppercase; color: #b6c0d0; }
.tgxl-empty { text-align: center; padding: 32px 12px; color: #8a93a3; }
.tgxl-empty strong { color: #d8dde6; display: block; margin-bottom: 4px; }
.tgxl-info { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; font-size: 11px; color: #8a93a3; margin-bottom: 10px; }
.tgxl-info .tgxl-info-name { color: #c8cfdb; font-weight: 600; }
.tgxl-info .sep { margin: 0 6px; opacity: 0.5; }

.tgxl-controls { display: flex; gap: 6px; margin-bottom: 12px; }
.tgxl-btn { flex: 1; padding: 6px 8px; font-size: 11px; font-weight: 600; letter-spacing: 0.02em;
  background: #2a3140; color: #c8cfdb; border: 1px solid #353c4c; border-radius: 6px;
  cursor: pointer; transition: background 120ms ease, border-color 120ms ease; }
.tgxl-btn:hover:not(:disabled) { background: #333b4d; }
.tgxl-btn:disabled { opacity: 0.45; cursor: not-allowed; }
.tgxl-btn.active-op  { background: rgba(74, 158, 255, 0.18); color: #4a9eff; border-color: rgba(74, 158, 255, 0.35); }
.tgxl-btn.active-byp { background: rgba(255, 201, 58, 0.18); color: #ffc93a; border-color: rgba(255, 201, 58, 0.35); }
.tgxl-btn.tune       { background: rgba(74, 158, 255, 0.18); color: #4a9eff; border-color: rgba(74, 158, 255, 0.35); }
.tgxl-btn.tune:hover:not(:disabled) { background: rgba(74, 158, 255, 0.28); }

.tgxl-meters { display: grid; grid-template-columns: 1fr 1fr 1.4fr; gap: 6px; margin-bottom: 12px; }
.tgxl-meter { background: #20262f; border: 1px solid #2c333f; border-radius: 6px; padding: 6px 8px; }
.tgxl-meter-label { font-size: 9px; letter-spacing: 0.12em; text-transform: uppercase; color: #7e879a; margin-bottom: 2px; }
.tgxl-meter-value { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; font-size: 16px; font-weight: 700; color: #e6ebf3; }
.tgxl-meter-value.swr-good  { color: #5cd479; }
.tgxl-meter-value.swr-warn  { color: #ffc93a; }
.tgxl-meter-value.swr-bad   { color: #e63a2b; }
.tgxl-meter-value.dim       { color: #6c7484; }

.tgxl-match { display: flex; flex-direction: column; gap: 3px; }
.tgxl-lc-row { display: flex; align-items: center; gap: 6px; }
.tgxl-lc-label { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; font-size: 10px; color: #8a93a3; width: 16px; text-align: right; }
.tgxl-lc-bar { flex: 1; height: 4px; background: #1a1f27; border-radius: 2px; overflow: hidden; }
.tgxl-lc-bar-fill { height: 100%; background: rgba(74, 158, 255, 0.7); transition: width 200ms ease; }
.tgxl-lc-value { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; font-size: 10px; color: #8a93a3; width: 24px; text-align: right; }

.tgxl-radios { display: flex; flex-direction: column; gap: 6px; }
.tgxl-radio { background: #20262f; border: 1px solid #2c333f; border-radius: 6px; padding: 8px 10px; }
.tgxl-radio.active { border-color: rgba(74, 158, 255, 0.45); }
.tgxl-radio-head { display: flex; justify-content: space-between; align-items: center; }
.tgxl-radio-left { display: flex; align-items: center; gap: 6px; }
.tgxl-radio-dot { width: 10px; height: 10px; border-radius: 50%; border: 2px solid #4c5566; background: transparent; cursor: pointer; padding: 0; }
.tgxl-radio-dot.on { background: #4a9eff; border-color: #4a9eff; }
.tgxl-radio-label { font-size: 11px; font-weight: 600; color: #c8cfdb; }
.tgxl-radio-label.b { color: #5cd479; }
.tgxl-radio-active-tag { font-size: 9px; color: #7e879a; }
.tgxl-radio-right { display: flex; align-items: center; gap: 6px; }
.tgxl-tx-tag { font-size: 9px; color: #e63a2b; font-weight: 700; letter-spacing: 0.06em; animation: tgxlPulse 1s ease-in-out infinite; }
.tgxl-radio-band { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; font-size: 10px; color: #8a93a3; }
.tgxl-radio-freq { margin-top: 4px; font-family: ui-monospace, SFMono-Regular, Menlo, monospace; font-size: 14px; font-weight: 700; color: #d8dde6; }

.tgxl-spin { display: inline-block; width: 9px; height: 9px; border: 1.5px solid currentColor;
  border-right-color: transparent; border-radius: 50%; animation: tgxlSpin 600ms linear infinite;
  vertical-align: -1px; margin-right: 4px; }

@keyframes tgxlSpin { to { transform: rotate(360deg); } }
@keyframes tgxlPulse { 50% { opacity: 0.45; } }
`;

function swrClass(swr: number, hasPower: boolean): string {
    if (!hasPower || swr <= 0 || swr >= 99) return 'dim';
    if (swr <= 1.5) return 'swr-good';
    if (swr <= 2.0) return 'swr-warn';
    return 'swr-bad';
}

function LCGauge({ label, value }: { label: string; value: number }) {
    const pct = Math.max(0, Math.min(100, Math.round((value / 255) * 100)));
    return (
        <div className="tgxl-lc-row">
            <span className="tgxl-lc-label">{label}</span>
            <div className="tgxl-lc-bar"><div className="tgxl-lc-bar-fill" style={{ width: `${pct}%` }} /></div>
            <span className="tgxl-lc-value">{value}</span>
        </div>
    );
}

function RadioRow({
    label, isB, freq, band, isActive, isTx, isSO2R, onActivate,
}: {
    label: string;
    isB: boolean;
    freq: number;
    band: string;
    isActive: boolean;
    isTx: boolean;
    isSO2R: boolean;
    onActivate: () => void;
}) {
    return (
        <div className={`tgxl-radio${isActive ? ' active' : ''}`}>
            <div className="tgxl-radio-head">
                <div className="tgxl-radio-left">
                    {isSO2R && (
                        <button
                            type="button"
                            className={`tgxl-radio-dot${isActive ? ' on' : ''}`}
                            onClick={onActivate}
                            title={isActive ? 'Active radio input' : 'Activate this radio'}
                        />
                    )}
                    <span className={`tgxl-radio-label${isB ? ' b' : ''}`}>{label}</span>
                    {isActive && isSO2R && <span className="tgxl-radio-active-tag">(Active)</span>}
                </div>
                <div className="tgxl-radio-right">
                    {isTx && <span className="tgxl-tx-tag">⚡ TX</span>}
                    <span className="tgxl-radio-band">{band || '--'}</span>
                </div>
            </div>
            <div className="tgxl-radio-freq">{freq > 0 ? `${freq.toFixed(3)} MHz` : 'N/A'}</div>
        </div>
    );
}

function TgxlPanel({ api }: { api: ZeusPluginApi }) {
    const [devices, setDevices] = useState<TgxlStatus[] | null>(null);

    useEffect(() => {
        let active = true;
        const tick = async () => {
            try {
                const res = await api.callBackend('GET', '/status');
                if (active && res.ok) setDevices(await res.json());
            } catch {
                if (active) setDevices([]);
            }
        };
        tick();
        const t = setInterval(tick, 500);
        return () => { active = false; clearInterval(t); };
    }, [api]);

    const tune     = (serial: string) => { void api.callBackend('POST', `/devices/${encodeURIComponent(serial)}/tune`); };
    const bypass   = (serial: string, bypass: boolean) =>
        { void api.callBackend('POST', `/devices/${encodeURIComponent(serial)}/bypass`, { bypass }); };
    const operate  = (serial: string, operate: boolean) =>
        { void api.callBackend('POST', `/devices/${encodeURIComponent(serial)}/operate`, { operate }); };
    const activate = (serial: string, channel: 1 | 2) =>
        { void api.callBackend('POST', `/devices/${encodeURIComponent(serial)}/activate`, { channel }); };

    if (devices === null) {
        return (
            <div className="tgxl-panel">
                <style>{styles}</style>
                <h3>Tuner Genius XL</h3>
                <div className="tgxl-empty">Connecting…</div>
            </div>
        );
    }

    if (devices.length === 0) {
        return (
            <div className="tgxl-panel">
                <style>{styles}</style>
                <h3>Tuner Genius XL</h3>
                <div className="tgxl-empty">
                    <strong>No Tuner Genius devices found</strong>
                    Waiting for device discovery on UDP port 9010…
                </div>
            </div>
        );
    }

    const d = devices[0];
    const isSO2R = !!d.portB;
    const hasPower = d.forwardPowerWatts >= 1;

    return (
        <div className="tgxl-panel">
            <style>{styles}</style>
            <h3>Tuner Genius XL · {d.isConnected ? 'Connected' : 'Disconnected'}</h3>

            <div className="tgxl-info">
                <span className="tgxl-info-name">{d.deviceName}</span>
                <span className="sep">|</span>
                <span>v{d.version || '?'}</span>
                <span className="sep">|</span>
                <span>{d.ipAddress}</span>
                <span className="sep">|</span>
                <span>{d.model}</span>
            </div>

            <div className="tgxl-controls">
                <button
                    type="button"
                    className={`tgxl-btn${d.isOperating ? ' active-op' : ''}`}
                    onClick={() => operate(d.deviceSerial, !d.isOperating)}
                >
                    {d.isOperating ? 'Operate' : 'Standby'}
                </button>
                <button
                    type="button"
                    className={`tgxl-btn${d.isBypassed ? ' active-byp' : ''}`}
                    onClick={() => bypass(d.deviceSerial, !d.isBypassed)}
                    title={d.isBypassed ? 'Tuner is bypassed — click to enable' : 'Click to bypass tuner'}
                >
                    {d.isBypassed ? 'Bypassed' : 'Tuner Inline'}
                </button>
                <button
                    type="button"
                    className="tgxl-btn tune"
                    onClick={() => tune(d.deviceSerial)}
                    disabled={d.isTuning || !d.isOperating}
                >
                    {d.isTuning ? <><span className="tgxl-spin" />Tuning…</> : 'Auto Tune'}
                </button>
            </div>

            <div className="tgxl-meters">
                <div className="tgxl-meter">
                    <div className="tgxl-meter-label">Fwd PWR</div>
                    <div className="tgxl-meter-value">{hasPower ? `${d.forwardPowerWatts.toFixed(0)} W` : '--'}</div>
                </div>
                <div className="tgxl-meter">
                    <div className="tgxl-meter-label">SWR</div>
                    <div className={`tgxl-meter-value ${swrClass(d.swr, hasPower)}`}>
                        {hasPower && d.swr > 0 && d.swr < 99 ? `${d.swr.toFixed(1)}:1` : '--'}
                    </div>
                </div>
                <div className="tgxl-meter">
                    <div className="tgxl-meter-label">Match</div>
                    <div className="tgxl-match">
                        <LCGauge label="C1" value={d.c1} />
                        <LCGauge label="L"  value={d.l} />
                        <LCGauge label="C2" value={d.c2} />
                    </div>
                </div>
            </div>

            <div className="tgxl-radios">
                <RadioRow
                    label="Radio A"
                    isB={false}
                    freq={d.freqAMhz}
                    band={d.portA?.band ?? ''}
                    isActive={d.activeRadio === 1}
                    isTx={d.portA?.isTransmitting ?? false}
                    isSO2R={isSO2R}
                    onActivate={() => activate(d.deviceSerial, 1)}
                />
                {isSO2R && (
                    <RadioRow
                        label="Radio B"
                        isB={true}
                        freq={d.freqBMhz}
                        band={d.portB?.band ?? ''}
                        isActive={d.activeRadio === 2}
                        isTx={d.portB?.isTransmitting ?? false}
                        isSO2R={true}
                        onActivate={() => activate(d.deviceSerial, 2)}
                    />
                )}
            </div>
        </div>
    );
}

export default function register(api: ZeusPluginApi) {
    api.registerPanel({
        id: 'tgxl.main',
        component: (props: object) => <TgxlPanel api={api} {...props} />,
    });
}

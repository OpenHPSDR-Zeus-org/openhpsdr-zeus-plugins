// SPDX-License-Identifier: GPL-2.0-or-later
//
// Power Genius XL plugin UI — self-contained TSX module.
//
// Receives a ZeusPluginApi from the host and registers a single panel
// (id `pgxl.main`) that polls /api/plugins/com.openhpsdr.zeus.plugins.pgxl/status
// once a second and renders the first discovered device. No imports from
// Zeus's web tree (those don't exist in the plugin sandbox) — inline SVGs,
// inline styles, and bare React only.
//
// Source-only for now; iter 5 of the plugin-system rebuild wires the Vite
// build that emits `pgxl.es.js` at install time. The manifest declares the
// emitted module path.

import { useEffect, useState } from 'react';

interface ZeusPluginApi {
    registerPanel(spec: { id: string; component: React.ComponentType }): void;
    callBackend(method: string, path: string, body?: unknown): Promise<Response>;
}

interface PgxlMeters {
    forwardPowerDbm: number;
    forwardPowerWatts: number;
    returnLossDb: number;
    swrRatio: number;
    drivePowerDbm: number;
    paCurrent: number;
    temperatureC: number;
}

interface PgxlSetup {
    bandSource: string;
    selectedAntenna: number;
    attenuatorEnabled: boolean;
    biasOffset: number;
    pttDelay: number;
    keyDelay: number;
    highSwr: boolean;
    overTemp: boolean;
    overCurrent: boolean;
}

interface PgxlStatus {
    serial: string;
    ipAddress: string;
    isConnected: boolean;
    isOperating: boolean;
    isTransmitting: boolean;
    band: string;
    biasA: string;
    biasB: string;
    firmwareVersion: string;
    meters: PgxlMeters;
    setup: PgxlSetup;
}

// ---------------------------------------------------------------------
// Inline icons (the plugin sandbox can't import lucide-react)
// ---------------------------------------------------------------------

const IconZap = ({ size = 16 }: { size?: number }) => (
    <svg width={size} height={size} viewBox="0 0 24 24" fill="none"
         stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
        <polygon points="13 2 3 14 12 14 11 22 21 10 12 10 13 2" />
    </svg>
);
const IconWifi = ({ size = 14, connected = true }: { size?: number; connected?: boolean }) => (
    <svg width={size} height={size} viewBox="0 0 24 24" fill="none"
         stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"
         style={{ opacity: connected ? 1 : 0.55 }}>
        <path d="M5 12.55a11 11 0 0 1 14.08 0" />
        <path d="M1.42 9a16 16 0 0 1 21.16 0" />
        <path d="M8.53 16.11a6 6 0 0 1 6.95 0" />
        <line x1="12" y1="20" x2="12.01" y2="20" />
        {!connected && <line x1="2" y1="2" x2="22" y2="22" />}
    </svg>
);
const IconSettings = ({ size = 14 }: { size?: number }) => (
    <svg width={size} height={size} viewBox="0 0 24 24" fill="none"
         stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
        <circle cx="12" cy="12" r="3" />
        <path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z" />
    </svg>
);

// ---------------------------------------------------------------------
// Styles (token-driven — fall back to literal values if not loaded)
// ---------------------------------------------------------------------

const colors = {
    panel:    'var(--panel-top, #20232a)',
    panelBot: 'var(--panel-bot, #14161a)',
    border:   'var(--panel-border, rgba(255,255,255,0.08))',
    text:     'var(--text-primary, #e4e7ec)',
    muted:    'var(--text-muted, #8b94a3)',
    accent:   'var(--accent, #4a9eff)',
    tx:       'var(--tx, #e63a2b)',
    power:    'var(--power, #ffc93a)',
    ok:       'var(--ok, #4ade80)',
};

// ---------------------------------------------------------------------
// Segmented meter
// ---------------------------------------------------------------------

interface Segment { start: number; end: number; color: string; }
interface Marker  { value: number; label: string; }

function SegmentedMeter({
    label, value, segments, markers, min = 0, max, valueDisplay,
}: {
    label: string; value: number; segments: Segment[]; markers: Marker[];
    min?: number; max: number; valueDisplay?: string;
}) {
    const range = max - min;
    return (
        <div style={{ marginBottom: 6 }}>
            <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between',
                          marginBottom: 2, fontSize: 11 }}>
                <span style={{ color: colors.muted, width: 56 }}>{label}</span>
                <div style={{ flex: 1, position: 'relative', height: 14 }}>
                    {markers.map((m) => (
                        <span key={m.value}
                              style={{ position: 'absolute', left: `${((m.value - min) / range) * 100}%`,
                                       transform: 'translateX(-50%)', fontFamily: 'monospace',
                                       fontSize: 10, color: colors.muted }}>
                            {m.label}
                        </span>
                    ))}
                </div>
                {valueDisplay && (
                    <span style={{ minWidth: 56, textAlign: 'right', color: colors.power,
                                   fontVariantNumeric: 'tabular-nums' }}>
                        {valueDisplay}
                    </span>
                )}
            </div>
            <div style={{ display: 'flex', height: 10, background: '#0d0f12',
                          borderRadius: 3, overflow: 'hidden' }}>
                {segments.map((seg, i) => {
                    const segStart = ((seg.start - min) / range) * 100;
                    const segEnd   = ((seg.end - min) / range) * 100;
                    const segWidth = segEnd - segStart;
                    const valPct   = ((Math.max(min, Math.min(value, max)) - min) / range) * 100;
                    const fillStart = Math.max(0, valPct - segStart);
                    const fillWidth = Math.min(fillStart, segWidth);
                    const fillPct   = segWidth > 0 ? (fillWidth / segWidth) * 100 : 0;
                    return (
                        <div key={i} style={{ position: 'relative', height: '100%', width: `${segWidth}%` }}>
                            <div style={{ position: 'absolute', inset: 0,
                                          background: seg.color, opacity: 0.18 }} />
                            {fillPct > 0 && (
                                <div style={{ position: 'absolute', top: 0, bottom: 0, left: 0,
                                              width: `${fillPct}%`, background: seg.color }} />
                            )}
                        </div>
                    );
                })}
            </div>
        </div>
    );
}

// ---------------------------------------------------------------------
// Operating / Standby display
// ---------------------------------------------------------------------

function StandbyDisplay() {
    return (
        <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'center',
                      minHeight: 120 }}>
            <span style={{ fontSize: 36, fontWeight: 700, letterSpacing: 4,
                           fontStyle: 'italic', color: colors.accent }}>
                STANDBY
            </span>
        </div>
    );
}

function OperatingDisplay({ meters }: { meters: PgxlMeters }) {
    return (
        <div style={{ padding: 10 }}>
            <SegmentedMeter
                label="Fwd Pwr"
                value={meters.forwardPowerWatts}
                segments={[
                    { start: 0,    end: 500,  color: colors.ok },
                    { start: 500,  end: 1500, color: colors.accent },
                    { start: 1500, end: 2000, color: colors.tx },
                ]}
                markers={[
                    { value: 0,    label: '0' },
                    { value: 500,  label: '500' },
                    { value: 1500, label: '1.5k' },
                    { value: 2000, label: '2k' },
                ]}
                max={2000}
                valueDisplay={`${meters.forwardPowerWatts.toFixed(0)} W`}
            />
            <SegmentedMeter
                label="SWR"
                value={meters.swrRatio}
                segments={[
                    { start: 1,   end: 1.5, color: colors.ok },
                    { start: 1.5, end: 2.5, color: colors.accent },
                    { start: 2.5, end: 3,   color: colors.tx },
                ]}
                markers={[
                    { value: 1,   label: '1' },
                    { value: 1.5, label: '1.5' },
                    { value: 2.5, label: '2.5' },
                    { value: 3,   label: '3' },
                ]}
                min={1}
                max={3}
                valueDisplay={meters.swrRatio > 0 ? meters.swrRatio.toFixed(2) : '—'}
            />
            <SegmentedMeter
                label="Id"
                value={meters.paCurrent}
                segments={[
                    { start: 0,  end: 40, color: colors.ok },
                    { start: 40, end: 60, color: colors.accent },
                    { start: 60, end: 70, color: colors.tx },
                ]}
                markers={[
                    { value: 10, label: '10' },
                    { value: 30, label: '30' },
                    { value: 50, label: '50' },
                    { value: 70, label: '70' },
                ]}
                max={70}
                valueDisplay={`${meters.paCurrent.toFixed(1)} A`}
            />
        </div>
    );
}

// ---------------------------------------------------------------------
// Slice row + status badge
// ---------------------------------------------------------------------

function SliceRow({ side, band, mode, ptt }: {
    side: string; band: string; mode: string; ptt: boolean;
}) {
    return (
        <div style={{ display: 'flex', alignItems: 'center', gap: 8, fontSize: 12 }}>
            <span style={{ width: 14, color: colors.muted, fontWeight: 600 }}>{side}</span>
            <span style={{ padding: '2px 6px', borderRadius: 3, fontSize: 10,
                           background: ptt ? colors.tx : '#0d0f12',
                           color: ptt ? '#0d0f12' : colors.muted }}>
                PTT
            </span>
            <span style={{ padding: '2px 6px', borderRadius: 3, fontFamily: 'monospace',
                           minWidth: 32, textAlign: 'center',
                           background: band !== 'N/A' ? colors.accent : '#0d0f12',
                           color: band !== 'N/A' ? '#0d0f12' : colors.muted }}>
                {band}
            </span>
            <span style={{ padding: '2px 6px', borderRadius: 3, fontSize: 11,
                           minWidth: 36, textAlign: 'center',
                           background: '#0d0f12', color: colors.text }}>
                {mode || 'N/A'}
            </span>
        </div>
    );
}

function StatusBadge({ label, tone }: { label: string; tone: 'danger' | 'warn' }) {
    const colour = tone === 'danger' ? colors.tx : colors.power;
    return (
        <span style={{ padding: '2px 6px', borderRadius: 3, fontSize: 10, fontWeight: 600,
                       border: `1px solid ${colour}`, color: colour,
                       background: `${colour}22`, letterSpacing: 0.5 }}>
            {label}
        </span>
    );
}

// ---------------------------------------------------------------------
// Settings panel (inline; toggled in-place rather than a modal so we
// don't have to worry about portal targets inside the plugin sandbox)
// ---------------------------------------------------------------------

function SettingsPanel({ device, api, onClose }: {
    device: PgxlStatus;
    api: ZeusPluginApi;
    onClose: () => void;
}) {
    const [busy, setBusy] = useState<string | null>(null);

    const disablePairing = async (slice: 'A' | 'B') => {
        setBusy(slice);
        try {
            await api.callBackend('POST',
                `/devices/${encodeURIComponent(device.serial)}/flexradio/disable`,
                { slice });
        } finally {
            setBusy(null);
        }
    };

    return (
        <div style={{ padding: 10, borderTop: `1px solid ${colors.border}` }}>
            <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center',
                          marginBottom: 8 }}>
                <strong style={{ fontSize: 12, color: colors.text }}>PGXL Settings</strong>
                <button onClick={onClose}
                        style={{ background: 'transparent', color: colors.muted,
                                 border: 'none', cursor: 'pointer', fontSize: 14 }}>✕</button>
            </div>

            <div style={{ background: '#0d0f12', borderRadius: 4, padding: 8, marginBottom: 8,
                          border: `1px solid ${colors.border}` }}>
                <div style={{ fontSize: 10, color: colors.muted, textTransform: 'uppercase',
                              letterSpacing: 1, marginBottom: 4 }}>Device</div>
                <div style={{ fontSize: 11, display: 'grid',
                              gridTemplateColumns: 'auto 1fr', columnGap: 8, rowGap: 2,
                              color: colors.text }}>
                    <span style={{ color: colors.muted }}>Serial</span>
                    <span style={{ fontFamily: 'monospace' }}>{device.serial}</span>
                    <span style={{ color: colors.muted }}>IP</span>
                    <span style={{ fontFamily: 'monospace' }}>{device.ipAddress}</span>
                    <span style={{ color: colors.muted }}>Firmware</span>
                    <span style={{ fontFamily: 'monospace' }}>{device.firmwareVersion || '—'}</span>
                    <span style={{ color: colors.muted }}>Band src</span>
                    <span>{device.setup.bandSource}</span>
                    <span style={{ color: colors.muted }}>Antenna</span>
                    <span>ANT{device.setup.selectedAntenna}</span>
                </div>
            </div>

            <div style={{ background: '#0d0f12', borderRadius: 4, padding: 8,
                          border: `1px solid ${colors.border}` }}>
                <div style={{ fontSize: 10, color: colors.muted, textTransform: 'uppercase',
                              letterSpacing: 1, marginBottom: 4 }}>FlexRadio pairing</div>
                <div style={{ fontSize: 11, color: colors.muted, marginBottom: 6 }}>
                    PGXL paired with a FlexRadio only accepts PTT/band data from that radio.
                    Disable pairing to use with Zeus.
                </div>
                <div style={{ display: 'flex', gap: 6 }}>
                    <button onClick={() => disablePairing('A')} disabled={busy === 'A'}
                            style={btnSecondary}>
                        {busy === 'A' ? '…' : 'Unpair A'}
                    </button>
                    <button onClick={() => disablePairing('B')} disabled={busy === 'B'}
                            style={btnSecondary}>
                        {busy === 'B' ? '…' : 'Unpair B'}
                    </button>
                </div>
            </div>
        </div>
    );
}

const btnSecondary: React.CSSProperties = {
    flex: 1, padding: '5px 10px', fontSize: 11, fontWeight: 500,
    background: `${colors.accent}22`, color: colors.accent,
    border: `1px solid ${colors.accent}55`, borderRadius: 4, cursor: 'pointer',
};

// ---------------------------------------------------------------------
// Main panel
// ---------------------------------------------------------------------

function PgxlPanel({ api }: { api: ZeusPluginApi }) {
    const [devices, setDevices] = useState<PgxlStatus[] | null>(null);
    const [error, setError] = useState<string | null>(null);
    const [showSettings, setShowSettings] = useState(false);
    const [busy, setBusy] = useState(false);

    useEffect(() => {
        let active = true;
        const tick = async () => {
            try {
                const res = await api.callBackend('GET', '/status');
                if (!active) return;
                if (res.ok) {
                    setDevices(await res.json());
                    setError(null);
                } else {
                    setError(`HTTP ${res.status}`);
                }
            } catch (e) {
                if (active) setError(String(e));
            }
        };
        tick();
        const t = setInterval(tick, 1000);
        return () => { active = false; clearInterval(t); };
    }, [api]);

    const device = devices && devices.length > 0 ? devices[0] : null;

    const toggleOperate = async () => {
        if (!device) return;
        setBusy(true);
        try {
            await api.callBackend('POST',
                `/devices/${encodeURIComponent(device.serial)}/operate`,
                { operate: !device.isOperating });
        } finally {
            setBusy(false);
        }
    };

    const cardStyle: React.CSSProperties = {
        background: `linear-gradient(180deg, ${colors.panel} 0%, ${colors.panelBot} 100%)`,
        border: `1px solid ${colors.border}`,
        borderRadius: 6,
        color: colors.text,
        fontFamily: '"Archivo Narrow", system-ui, sans-serif',
        overflow: 'hidden',
        minWidth: 320,
    };

    const headerStyle: React.CSSProperties = {
        display: 'flex', alignItems: 'center', justifyContent: 'space-between',
        padding: '8px 12px', borderBottom: `1px solid ${colors.border}`,
    };

    if (devices === null) {
        return (
            <div style={cardStyle}>
                <div style={headerStyle}>
                    <span style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
                        <IconZap /> <strong>Power Genius XL</strong>
                    </span>
                </div>
                <div style={{ padding: 16, color: colors.muted, fontSize: 12 }}>
                    {error ? `Connecting… (${error})` : 'Connecting…'}
                </div>
            </div>
        );
    }

    if (!device) {
        return (
            <div style={cardStyle}>
                <div style={headerStyle}>
                    <span style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
                        <IconZap /> <strong>Power Genius XL</strong>
                    </span>
                </div>
                <div style={{ padding: 24, textAlign: 'center', color: colors.muted, fontSize: 12 }}>
                    <div style={{ marginBottom: 6, opacity: 0.55 }}>
                        <IconWifi size={32} connected={false} />
                    </div>
                    No PGXL amplifiers found.<br />
                    Waiting for discovery on UDP :9008.
                </div>
            </div>
        );
    }

    const tempA = device.meters.temperatureC;
    const vdd = device.isOperating ? 52.0 : 0.0; // PGXL doesn't expose this field via REST yet.

    return (
        <div style={cardStyle}>
            <div style={headerStyle}>
                <span style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
                    <IconZap /> <strong>Power Genius XL</strong>
                </span>
                <span style={{ display: 'flex', alignItems: 'center', gap: 4, fontSize: 11,
                               color: device.isConnected ? colors.ok : colors.muted }}>
                    <IconWifi connected={device.isConnected} />
                    {device.isConnected ? 'Connected' : 'Disconnected'}
                </span>
            </div>

            <div style={{ margin: '8px 10px 0', background: '#0d0f12',
                          borderRadius: 4, overflow: 'hidden' }}>
                {device.isOperating
                    ? <OperatingDisplay meters={device.meters} />
                    : <StandbyDisplay />}
            </div>

            <div style={{ display: 'flex', gap: 12, padding: '10px 12px' }}>
                <div style={{ flex: 1, display: 'flex', flexDirection: 'column', gap: 4 }}>
                    <SliceRow side="A" band={device.band.replace('m', '')}
                              mode={device.biasA} ptt={device.isTransmitting} />
                    <SliceRow side="B" band="N/A" mode={device.biasB} ptt={false} />
                </div>
                <div style={{ textAlign: 'right', fontSize: 11, color: colors.muted, minWidth: 90,
                              lineHeight: 1.6 }}>
                    <div><span style={{ color: colors.text,
                                        fontVariantNumeric: 'tabular-nums' }}>{tempA.toFixed(1)}</span> °C</div>
                    <div>Vdd <span style={{ color: colors.text,
                                            fontVariantNumeric: 'tabular-nums' }}>{vdd.toFixed(1)}</span> V</div>
                </div>
            </div>

            <div style={{ display: 'flex', gap: 8, padding: '0 12px 10px' }}>
                <button onClick={() => setShowSettings(s => !s)}
                        style={{
                            padding: '6px 12px', fontSize: 12,
                            background: '#0d0f12', color: colors.text,
                            border: `1px solid ${colors.border}`, borderRadius: 4,
                            cursor: 'pointer', display: 'flex', alignItems: 'center', gap: 4,
                        }}>
                    <IconSettings /> Settings
                </button>
                <div style={{ flex: 1 }} />
                <button onClick={toggleOperate} disabled={busy || !device.isConnected}
                        style={{
                            padding: '6px 18px', fontSize: 12, fontWeight: 700,
                            background: device.isOperating ? colors.ok : colors.power,
                            color: '#0d0f12',
                            border: 'none', borderRadius: 4, cursor: 'pointer',
                            opacity: busy || !device.isConnected ? 0.5 : 1,
                        }}>
                    {device.isOperating ? 'Standby' : 'Operate'}
                </button>
            </div>

            {(device.setup.highSwr || device.setup.overTemp || device.setup.overCurrent) && (
                <div style={{ display: 'flex', gap: 6, padding: '0 12px 10px', flexWrap: 'wrap' }}>
                    {device.setup.highSwr     && <StatusBadge label="HIGH SWR"     tone="danger" />}
                    {device.setup.overTemp    && <StatusBadge label="OVER TEMP"    tone="danger" />}
                    {device.setup.overCurrent && <StatusBadge label="OVER CURRENT" tone="danger" />}
                </div>
            )}

            {showSettings && (
                <SettingsPanel device={device} api={api}
                               onClose={() => setShowSettings(false)} />
            )}
        </div>
    );
}

export default function register(api: ZeusPluginApi) {
    api.registerPanel({
        id: 'pgxl.main',
        component: (props: object) => <PgxlPanel api={api} {...props} />,
    });
}

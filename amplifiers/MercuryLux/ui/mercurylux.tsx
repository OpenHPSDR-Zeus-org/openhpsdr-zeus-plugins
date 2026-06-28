// SPDX-License-Identifier: GPL-2.0-or-later
//
// KM3KM Mercury LUX amplifier plugin — UI module.
//
// Operator console for the KM3KM Mercury LUX HF amplifier. Visual identity is
// translated from the amp's own 7-inch touchscreen / KM3KM PC software (ARRL
// QST Nov-2025 review, Figs 1, 2 & 5) into the Zeus dark "immersive" panel
// system so it sits consistently next to the RF-Kit RF2K-S panel:
//
//   * Signature Mercury-LUX horizontal BAR METERS with safe-limit tick
//     markers (forward / reflected power, drain current, temperature, drain
//     voltage) and magenta lit readouts.
//   * An alternate simulated ANALOG-METER view (the LUX offers both layouts).
//   * Status chips (SWR / band / band-source / antenna / warnings) + ON AIR.
//   * Control row: Operate/Standby, antenna 1-3, fan auto/max, fault reset.
//   * Settings drawer for the connection (serial COM port + baud, or the
//     MercuryXPc WebSocket bridge host:port).
//
// Self-contained: the only import is React. Data flows through the host's
// ZeusPluginApi.callBackend (same contract as the RF2K-S plugin). Until the
// Mercury LUX wire protocol is wired into the backend transport, the panel
// renders sample telemetry (Demo) so the layout is fully visible; no control
// command is ever issued by a disabled/disconnected panel.

import { useEffect, useMemo, useState, type CSSProperties, type ReactNode } from 'react';

// ============================================================================
//  Host contract
// ============================================================================

interface ZeusPluginApi {
  registerPanel(spec: { id: string; component: React.ComponentType }): void;
  callBackend(method: string, path: string, body?: unknown): Promise<Response>;
}

// ============================================================================
//  Wire types — mirror the backend MercuryLuxPlugin.cs DTOs (camelCase).
// ============================================================================

type Reading = { value: number; unit: string | null };
type PeakReading = { value: number; maxValue: number; unit: string | null };

type MluxAntenna = { number: number; active: boolean; label: string | null };

type MluxStatus = {
  enabled: boolean;
  connected: boolean;
  /** true while the real serial/WebSocket transport is not yet implemented */
  protocolPending: boolean;
  transport: string;              // "Serial COM5 @115200" | "WebSocket host:port" | "—"
  model: string | null;           // "Mercury LUX"
  firmware: string | null;
  callsign: string | null;
  forward: PeakReading | null;     // W   (rated ~1500)
  reflected: PeakReading | null;   // W   (protect @125)
  swr: Reading | null;
  drainCurrent: PeakReading | null;// A   (limit ~45)
  drainVoltage: Reading | null;    // V
  temperature: Reading | null;     // °F / °C
  band: string | null;             // "20" | "17-15"
  bandSource: string | null;       // "CAT · ICOM" | "RF SENSE"
  operateMode: string | null;      // OPERATE | STANDBY
  onAir: boolean;
  fanMax: boolean;
  warning: string | null;          // protection text, null = NO WARNING
  antennas: MluxAntenna[] | null;
  error: string | null;
  lastSampleUtc: string | null;
};

type MluxConfig = {
  enabled: boolean;
  link: 'serial' | 'websocket';
  comPort: string;
  baudRate: number;
  host: string;
  port: number;
  pollingIntervalMs: number;
  temperatureUnit: 'F' | 'C';
};

type TestResult = { ok: boolean; error: string | null };

const DEFAULT_CONFIG: MluxConfig = {
  enabled: false,
  link: 'serial',
  comPort: 'COM5',
  baudRate: 115200,
  host: '127.0.0.1',
  port: 8765,
  pollingIntervalMs: 500,
  temperatureUnit: 'F',
};

// Sample telemetry that mirrors the ARRL review screenshots, shown until the
// backend transport is live so the panel is fully visible in "Demo" mode.
const DEMO_STATUS: MluxStatus = {
  enabled: true,
  connected: true,
  protocolPending: true,
  transport: 'Demo',
  model: 'Mercury LUX',
  firmware: '2.1',
  callsign: 'W1XW',
  forward: { value: 1487, maxValue: 1500, unit: 'W' },
  reflected: { value: 6, maxValue: 125, unit: 'W' },
  swr: { value: 1.18, unit: null },
  drainCurrent: { value: 38.0, maxValue: 45, unit: 'A' },
  drainVoltage: { value: 53.7, unit: 'V' },
  temperature: { value: 75, unit: '°F' },
  band: '17-15',
  bandSource: 'ICOM',
  operateMode: 'OPERATE',
  onAir: true,
  fanMax: false,
  warning: null,
  antennas: [
    { number: 1, active: true, label: null },
    { number: 2, active: false, label: null },
    { number: 3, active: false, label: null },
  ],
  error: null,
  lastSampleUtc: null,
};

// ============================================================================
//  REST client
// ============================================================================

async function jsonOr<T>(res: Response, fallback: T): Promise<T> {
  try {
    if (!res.ok) return fallback;
    return (await res.json()) as T;
  } catch {
    return fallback;
  }
}

function makeApi(plugin: ZeusPluginApi) {
  return {
    getStatus: async () => jsonOr<MluxStatus | null>(await plugin.callBackend('GET', '/status'), null),
    getConfig: async () => jsonOr<MluxConfig | null>(await plugin.callBackend('GET', '/config'), null),
    saveConfig: async (cfg: MluxConfig) =>
      jsonOr<MluxStatus | null>(await plugin.callBackend('POST', '/config', cfg), null),
    setOperate: async (mode: 'OPERATE' | 'STANDBY') =>
      jsonOr<MluxStatus | null>(await plugin.callBackend('POST', '/operate', { mode }), null),
    setAntenna: async (number: number) =>
      jsonOr<MluxStatus | null>(await plugin.callBackend('POST', '/antenna', { number }), null),
    setFan: async (max: boolean) =>
      jsonOr<MluxStatus | null>(await plugin.callBackend('POST', '/fan', { max }), null),
    reset: async () => jsonOr<MluxStatus | null>(await plugin.callBackend('POST', '/reset'), null),
    test: async (cfg: MluxConfig) =>
      jsonOr<TestResult>(await plugin.callBackend('POST', '/test', cfg), { ok: false, error: 'request failed' }),
  };
}
type MluxApi = ReturnType<typeof makeApi>;

// ============================================================================
//  Palette — Mercury LUX signature magenta readout + Zeus immersive tokens
//  (hex fallbacks so the panel is readable outside the Zeus shell).
// ============================================================================

const MAGENTA = 'var(--mlux-readout, #e85cff)';
const MAGENTA_GLOW = 'var(--mlux-readout-glow, rgba(232,92,255,0.45))';
const GOOD = 'var(--immersive-good, #5cd49a)';
const WARN = 'var(--immersive-warn, #f4c168)';
const TX = 'var(--immersive-tx, #ef6b54)';
const FG1 = 'var(--fg-1, #e7e7ea)';
const FG2 = 'var(--fg-2, rgba(255,255,255,0.6))';
const FG3 = 'var(--fg-3, rgba(255,255,255,0.4))';
const MONO = 'var(--font-mono, ui-monospace, "SF Mono", monospace)';

// ============================================================================
//  Bolt — Zeus's brand-mark lightning bolt (App.tsx .brand-mark-bolt path),
//  reused as the panel/section accent in Mercury-LUX magenta.
// ============================================================================

function Bolt({ color = MAGENTA, size = 12, glowColor }: { color?: string; size?: number; glowColor?: string }) {
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" aria-hidden="true" focusable="false" style={{ display: 'block', flexShrink: 0, filter: glowColor ? `drop-shadow(0 0 5px ${glowColor})` : undefined }}>
      <path d="M13.4 2.5 7 12.1h4.15l-1.2 9.4L17 10.55h-4.25l.65-8.05Z" fill={color} />
    </svg>
  );
}

// ============================================================================
//  Helpers
// ============================================================================

function fmt(v: number | null | undefined, d = 1): string {
  if (v == null || !Number.isFinite(v)) return '—';
  return v.toFixed(d);
}
function clamp01(x: number): number {
  return Math.max(0, Math.min(1, x));
}
function swrTone(swr: number | null | undefined): 'good' | 'warn' | 'bad' | undefined {
  if (swr == null || !Number.isFinite(swr)) return undefined;
  if (swr >= 2.0) return 'bad';
  if (swr >= 1.5) return 'warn';
  return 'good';
}
function fmtAge(iso: string | null): string {
  if (!iso) return '—';
  const t = Date.parse(iso);
  if (!Number.isFinite(t)) return '—';
  const s = Math.max(0, (Date.now() - t) / 1000);
  if (s < 2) return 'live';
  if (s < 60) return `${Math.round(s)} s ago`;
  return `${Math.round(s / 60)} m ago`;
}

// ============================================================================
//  Bar meter — the Mercury LUX signature. Horizontal track, magenta fill,
//  magenta lit readout, safe-limit tick markers.
// ============================================================================

type Tick = { value: number; label?: string; tone?: 'warn' | 'bad' };

function BarMeter({
  label,
  reading,
  max,
  ticks,
  decimals = 0,
  overTone = 'bad',
  live,
}: {
  label: string;
  reading: Reading | PeakReading | null;
  max: number;
  ticks: Tick[];
  decimals?: number;
  overTone?: 'warn' | 'bad';
  live: boolean;
}) {
  const value = reading && Number.isFinite(reading.value) ? reading.value : null;
  const unit = reading?.unit ?? '';
  const frac = value != null ? clamp01(value / max) : 0;
  // tone: pick the highest crossed tick
  let crossed: 'warn' | 'bad' | undefined;
  if (value != null) {
    for (const t of ticks) {
      if (value >= t.value && (t.tone === 'bad' || (t.tone === 'warn' && crossed !== 'bad'))) crossed = t.tone;
    }
    if (frac >= 1) crossed = overTone;
  }
  const fillColor = crossed === 'bad' ? TX : crossed === 'warn' ? WARN : MAGENTA;
  const fillGlow =
    crossed === 'bad'
      ? 'var(--immersive-tx-glow, rgba(239,107,84,0.5))'
      : crossed === 'warn'
        ? 'var(--immersive-warn-glow, rgba(244,193,104,0.5))'
        : MAGENTA_GLOW;

  return (
    <div style={{ display: 'grid', gridTemplateColumns: '118px 1fr', alignItems: 'center', gap: 12 }}>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 2, minWidth: 0 }}>
        <span style={{ fontSize: 8.5, letterSpacing: '0.14em', textTransform: 'uppercase', color: FG3, fontWeight: 700, whiteSpace: 'nowrap' }}>
          {label}
        </span>
        <span style={{ fontFamily: MONO, fontVariantNumeric: 'tabular-nums', fontSize: 19, fontWeight: 600, lineHeight: 1, color: live && value != null ? fillColor : FG3, textShadow: live && value != null ? `0 0 12px ${fillGlow}` : undefined }}>
          {value != null ? value.toFixed(decimals) : '—'}
          <span style={{ fontSize: 9.5, color: FG3, fontWeight: 500, marginLeft: 3, letterSpacing: '0.04em' }}>{unit}</span>
        </span>
      </div>

      <div style={{ position: 'relative', paddingBottom: 12 }}>
        {/* track */}
        <div style={{ position: 'relative', height: 16, borderRadius: 4, overflow: 'hidden', background: 'linear-gradient(180deg,#0a0a0d 0%, #050507 100%)', border: '1px solid var(--immersive-line, rgba(255,255,255,0.06))', boxShadow: 'inset 0 1px 2px rgba(0,0,0,0.7)' }}>
          {/* segment grooves */}
          <div style={{ position: 'absolute', inset: 0, background: 'repeating-linear-gradient(90deg, transparent 0 11px, rgba(0,0,0,0.55) 11px 13px)', opacity: 0.5, pointerEvents: 'none' }} />
          {/* fill */}
          <div style={{ position: 'absolute', left: 0, top: 0, bottom: 0, width: `${(frac * 100).toFixed(1)}%`, background: `linear-gradient(180deg, ${fillColor}, ${fillColor})`, opacity: live ? 0.92 : 0.25, boxShadow: live ? `0 0 12px ${fillGlow}` : undefined, transition: 'width 0.25s ease, opacity 0.2s' }} />
          {/* fill grooves overlay to keep segmented look on the lit part */}
          <div style={{ position: 'absolute', inset: 0, background: 'repeating-linear-gradient(90deg, transparent 0 11px, rgba(0,0,0,0.45) 11px 13px)', pointerEvents: 'none' }} />
        </div>
        {/* tick markers + labels under the bar */}
        {ticks.map((t, i) => {
          const left = clamp01(t.value / max) * 100;
          const c = t.tone === 'bad' ? TX : t.tone === 'warn' ? WARN : FG3;
          return (
            <div key={i} style={{ position: 'absolute', left: `${left}%`, top: 0, transform: 'translateX(-50%)', pointerEvents: 'none' }}>
              <div style={{ width: 1, height: 18, background: c, opacity: 0.7, margin: '0 auto' }} />
              {t.label && (
                <div style={{ fontFamily: MONO, fontSize: 8, color: c, textAlign: 'center', marginTop: 1, letterSpacing: '0.02em' }}>{t.label}</div>
              )}
            </div>
          );
        })}
      </div>
    </div>
  );
}

// ============================================================================
//  Analog meter — alternate view (LUX Fig 2). Semicircular needle gauge.
// ============================================================================

function AnalogMeter({ label, reading, max, redline, live }: { label: string; reading: Reading | PeakReading | null; max: number; redline?: number; live: boolean }) {
  const value = reading && Number.isFinite(reading.value) ? reading.value : null;
  const unit = reading?.unit ?? 'W';
  const frac = value != null ? clamp01(value / max) : 0;
  const ang = -90 + 180 * frac; // -90..+90
  const W = 200, H = 118, CX = 100, CY = 104, R = 78;
  const pt = (f: number, r: number) => {
    const a = (180 + 180 * f) * (Math.PI / 180);
    return { x: CX + Math.cos(a) * r, y: CY + Math.sin(a) * r };
  };
  const ticks = Array.from({ length: 11 }, (_, i) => i / 10);
  const redFrac = redline != null ? clamp01(redline / max) : null;
  return (
    <div style={{ position: 'relative', borderRadius: 7, padding: '10px 10px 8px', background: 'radial-gradient(80% 95% at 50% 100%, rgba(232,92,255,0.06), transparent 70%), linear-gradient(180deg,#141318 0%, #0a090c 100%)', border: '1px solid var(--immersive-line, rgba(255,255,255,0.06))', boxShadow: 'inset 0 1px 0 rgba(255,255,255,0.04), inset 0 0 40px rgba(0,0,0,0.5)' }}>
      <span style={{ position: 'absolute', top: 8, left: 11, fontSize: 8.5, letterSpacing: '0.16em', textTransform: 'uppercase', color: FG3, fontWeight: 700 }}>{label}</span>
      <svg viewBox={`0 0 ${W} ${H}`} style={{ width: '100%', height: 'auto', display: 'block' }}>
        {/* arc track */}
        <path d={`M ${CX - R} ${CY} A ${R} ${R} 0 0 1 ${CX + R} ${CY}`} fill="none" stroke="rgba(255,255,255,0.08)" strokeWidth={3} />
        {redFrac != null && (
          <path
            d={`M ${pt(redFrac, R).x.toFixed(1)} ${pt(redFrac, R).y.toFixed(1)} A ${R} ${R} 0 0 1 ${pt(1, R).x.toFixed(1)} ${pt(1, R).y.toFixed(1)}`}
            fill="none" stroke={TX} strokeWidth={3} opacity={0.85}
          />
        )}
        {/* ticks */}
        {ticks.map((f, i) => {
          const a = pt(f, R - 3), b = pt(f, R - (i % 5 === 0 ? 12 : 8));
          return <line key={i} x1={a.x.toFixed(1)} y1={a.y.toFixed(1)} x2={b.x.toFixed(1)} y2={b.y.toFixed(1)} stroke="rgba(245,240,210,0.5)" strokeWidth={i % 5 === 0 ? 1.4 : 0.8} />;
        })}
        {/* needle */}
        {live && value != null && (
          <g transform={`rotate(${ang.toFixed(2)} ${CX} ${CY})`}>
            <line x1={CX} y1={CY} x2={CX} y2={CY - R + 8} stroke={MAGENTA} strokeWidth={2.2} strokeLinecap="round" style={{ filter: `drop-shadow(0 0 4px ${MAGENTA_GLOW})` }} />
          </g>
        )}
        <circle cx={CX} cy={CY} r={6} fill="#15151a" stroke="rgba(245,240,210,0.35)" strokeWidth={1.2} />
        <circle cx={CX} cy={CY} r={2.4} fill={MAGENTA} />
      </svg>
      <div style={{ textAlign: 'center', marginTop: -2, fontFamily: MONO, fontVariantNumeric: 'tabular-nums', fontSize: 17, fontWeight: 600, color: live && value != null ? MAGENTA : FG3, textShadow: live && value != null ? `0 0 12px ${MAGENTA_GLOW}` : undefined }}>
        {value != null ? Math.round(value) : '—'}<span style={{ fontSize: 9, color: FG3, marginLeft: 3 }}>{unit}</span>
      </div>
    </div>
  );
}

// ============================================================================
//  Chrome atoms (Zeus immersive, mirrors the RF2K-S panel family)
// ============================================================================

function Section({ title, meta, right, children, padding = 14 }: { title?: string; meta?: string; right?: ReactNode; children: ReactNode; padding?: number | string }) {
  return (
    <section style={{ background: 'linear-gradient(180deg, var(--immersive-panel-2, #161620) 0%, var(--immersive-well, #0b0b10) 100%)', border: '1px solid var(--immersive-line, rgba(255,255,255,0.06))', borderRadius: 8, padding, boxShadow: 'inset 0 1px 0 rgba(255,255,255,0.04), inset 0 0 30px rgba(0,0,0,0.25)', position: 'relative' }}>
      {(title || meta || right) && (
        <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginBottom: title ? 12 : 0 }}>
          {title && (
            <div style={{ fontSize: 9.5, letterSpacing: '0.20em', textTransform: 'uppercase', color: FG2, fontWeight: 700, display: 'flex', alignItems: 'center', gap: 8 }}>
              <Bolt color={MAGENTA} glowColor={MAGENTA_GLOW} size={12} />
              {title}
            </div>
          )}
          {(meta || right) && (
            <div style={{ fontFamily: MONO, fontSize: 9.5, color: FG3, letterSpacing: '0.06em', display: 'flex', gap: 10, alignItems: 'center' }}>
              {meta}{right}
            </div>
          )}
        </div>
      )}
      {children}
    </section>
  );
}

function Chip({ k, v, tone, dim }: { k: string; v: string; tone?: 'good' | 'warn' | 'bad'; dim?: boolean }) {
  let color: string = 'var(--immersive-lamp-chip-text, #fbf3d3)';
  let glow: string | undefined = undefined;
  if (tone === 'good') { color = GOOD; glow = '0 0 8px var(--immersive-good-glow, rgba(92,212,154,0.5))'; }
  else if (tone === 'warn') { color = WARN; glow = '0 0 8px var(--immersive-warn-glow, rgba(244,193,104,0.5))'; }
  else if (tone === 'bad') { color = TX; glow = '0 0 8px var(--immersive-tx-glow, rgba(239,107,84,0.5))'; }
  else if (dim) { color = FG2; }
  return (
    <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 10, padding: '8px 11px', borderRadius: 6, background: 'linear-gradient(180deg, #161618 0%, #0c0c0e 100%)', border: '1px solid var(--immersive-line, rgba(255,255,255,0.06))', boxShadow: 'inset 0 1px 0 rgba(255,255,255,0.04)' }}>
      <span style={{ fontSize: 8.5, letterSpacing: '0.16em', textTransform: 'uppercase', color: FG3, fontWeight: 700 }}>{k}</span>
      <span style={{ fontFamily: MONO, fontSize: 12, color, fontWeight: 600, letterSpacing: '0.02em', textShadow: glow, whiteSpace: 'nowrap' }}>{v}</span>
    </div>
  );
}

function OnAir({ on }: { on: boolean }) {
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: 7, padding: '7px 13px', borderRadius: 6, border: `1px solid ${on ? 'rgba(239,107,84,0.6)' : 'var(--immersive-line, rgba(255,255,255,0.08))'}`, background: on ? 'linear-gradient(180deg,#3a1813,#2a100c)' : 'linear-gradient(180deg,#141416,#0c0c0e)', boxShadow: on ? `0 0 16px var(--immersive-tx-glow, rgba(239,107,84,0.5))` : undefined, transition: '0.2s' }}>
      <span style={{ width: 8, height: 8, borderRadius: '50%', background: on ? TX : FG3, boxShadow: on ? `0 0 8px ${TX}` : undefined }} />
      <span style={{ fontSize: 10.5, fontWeight: 800, letterSpacing: '0.18em', color: on ? '#ffd0c5' : FG3 }}>ON AIR</span>
    </div>
  );
}

type BtnTone = 'default' | 'magenta' | 'warn-active' | 'ghost' | 'ghost-danger';
function PanelBtn({ tone = 'default', small, disabled, onClick, title, type = 'button', children }: { tone?: BtnTone; small?: boolean; disabled?: boolean; onClick?: () => void; title?: string; type?: 'button' | 'submit'; children: ReactNode }) {
  const palette: Record<BtnTone, CSSProperties> = {
    default: { color: FG1, background: 'linear-gradient(180deg,#171c27 0%, #11151d 100%)', borderColor: 'var(--immersive-line-2, rgba(255,255,255,0.1))' },
    magenta: { color: '#fbe9ff', background: 'linear-gradient(180deg,#2a1330,#1d0d22)', borderColor: 'rgba(232,92,255,0.55)', boxShadow: `0 0 14px ${MAGENTA_GLOW}, inset 0 1px 0 rgba(232,92,255,0.3)` },
    'warn-active': { color: WARN, background: 'linear-gradient(180deg,#3a2a14,#2a1d0d)', borderColor: 'rgba(244,193,104,0.7)', boxShadow: '0 0 14px var(--immersive-warn-glow, rgba(244,193,104,0.5))' },
    ghost: { color: FG2, background: 'transparent', borderColor: 'var(--immersive-line, rgba(255,255,255,0.06))' },
    'ghost-danger': { color: TX, background: 'transparent', borderColor: 'rgba(239,107,84,0.35)' },
  };
  const p = palette[tone];
  return (
    <button type={type} onClick={onClick} disabled={disabled} title={title} style={{ fontFamily: 'inherit', fontSize: small ? 10 : 11, fontWeight: 700, letterSpacing: '0.13em', textTransform: 'uppercase', padding: small ? '6px 12px' : '9px 15px', borderRadius: 6, border: '1px solid', cursor: disabled ? 'not-allowed' : 'pointer', transition: '0.15s', opacity: disabled ? 0.5 : 1, boxShadow: (p as CSSProperties).boxShadow ?? 'inset 0 1px 0 rgba(255,255,255,0.04), 0 1px 0 rgba(0,0,0,0.4)', ...p }}>
      {children}
    </button>
  );
}

function SegState({ isOperate, connected, onSelect }: { isOperate: boolean; connected: boolean; onSelect: (v: 'OPERATE' | 'STANDBY') => void }) {
  const seg = (active: boolean, variant: 'op' | 'stb', label: string, v: 'OPERATE' | 'STANDBY') => {
    const activeStyle: CSSProperties = variant === 'op'
      ? { color: '#fbe9ff', background: 'linear-gradient(180deg,#2a1330,#1d0d22)', boxShadow: `0 0 0 1px rgba(232,92,255,0.4), 0 0 16px ${MAGENTA_GLOW}` }
      : { color: '#ffd0c5', background: 'linear-gradient(180deg,#3a1a14,#2a110d)', boxShadow: '0 0 0 1px rgba(239,107,84,0.45), 0 0 12px var(--immersive-tx-glow, rgba(239,107,84,0.5))' };
    return (
      <button type="button" disabled={!connected} onClick={() => onSelect(v)} style={{ fontFamily: 'inherit', fontSize: 11, fontWeight: 700, letterSpacing: '0.13em', textTransform: 'uppercase', padding: '9px 18px', borderRadius: 5, border: 0, cursor: connected ? 'pointer' : 'not-allowed', background: 'transparent', color: FG2, transition: '0.15s', opacity: connected ? 1 : 0.55, ...(active ? activeStyle : null) }}>
        {label}
      </button>
    );
  };
  return (
    <div style={{ display: 'inline-flex', padding: 3, borderRadius: 7, background: 'linear-gradient(180deg,#0b0b10,#060608)', border: '1px solid var(--immersive-line, rgba(255,255,255,0.06))', boxShadow: 'inset 0 1px 2px rgba(0,0,0,0.6)' }}>
      {seg(isOperate, 'op', 'Operate', 'OPERATE')}
      {seg(!isOperate, 'stb', 'Standby', 'STANDBY')}
    </div>
  );
}

function Pill({ active, disabled, onClick, children, title }: { active: boolean; disabled?: boolean; onClick: () => void; children: ReactNode; title?: string }) {
  const activeStyle: CSSProperties = active
    ? { color: '#fbe9ff', background: 'linear-gradient(180deg,#2a1330,#1d0d22)', borderColor: 'rgba(232,92,255,0.5)', boxShadow: `inset 0 1px 0 rgba(232,92,255,0.25), 0 0 10px ${MAGENTA_GLOW}` }
    : {};
  return (
    <button type="button" onClick={onClick} disabled={disabled} title={title} style={{ fontFamily: MONO, fontSize: 11, fontWeight: 600, letterSpacing: '0.08em', padding: '7px 13px', borderRadius: 5, background: 'linear-gradient(180deg,#171c27,#11151d)', border: '1px solid var(--immersive-line-2, rgba(255,255,255,0.1))', color: FG2, cursor: disabled ? 'not-allowed' : 'pointer', transition: '0.12s', opacity: disabled ? 0.5 : 1, boxShadow: 'inset 0 1px 0 rgba(255,255,255,0.04)', ...activeStyle }}>
      {children}
    </button>
  );
}

function Toast({ ok, tone, children }: { ok: boolean; tone?: 'idle'; children: ReactNode }) {
  const color = tone === 'idle' ? WARN : ok ? '#d8f5e4' : '#ffd7cc';
  const bg = tone === 'idle' ? 'rgba(244,193,104,0.10)' : ok ? 'rgba(92,212,154,0.10)' : 'rgba(239,107,84,0.12)';
  const bd = tone === 'idle' ? 'rgba(244,193,104,0.45)' : ok ? 'rgba(92,212,154,0.45)' : 'rgba(239,107,84,0.45)';
  return <div style={{ padding: 9, fontSize: 11.5, color, background: bg, border: `1px solid ${bd}`, borderRadius: 6, lineHeight: 1.5 }}>{children}</div>;
}

function Field({ label, children, help }: { label: string; children: ReactNode; help?: ReactNode }) {
  return (
    <label style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
      <span style={{ fontSize: 10, letterSpacing: '0.16em', textTransform: 'uppercase', color: FG2, fontWeight: 700 }}>{label}</span>
      {children}
      {help && <span style={{ fontSize: 11, color: FG3, lineHeight: 1.5 }}>{help}</span>}
    </label>
  );
}
function Input({ value, onChange, type = 'text', placeholder }: { value: string; onChange: (v: string) => void; type?: 'text' | 'number' | 'password'; placeholder?: string }) {
  return <input type={type} value={value} placeholder={placeholder} spellCheck={false} onChange={(e) => onChange(e.target.value)} style={{ font: 'inherit', fontFamily: MONO, fontSize: 13, background: 'linear-gradient(180deg,#0b0b10,#060608)', border: '1px solid var(--immersive-line-2, rgba(255,255,255,0.1))', color: 'var(--fg-0,#fff)', borderRadius: 6, padding: '9px 11px', boxShadow: 'inset 0 1px 2px rgba(0,0,0,0.6)', outline: 'none', minWidth: 0 }} />;
}
function Check({ checked, onChange, children }: { checked: boolean; onChange: (v: boolean) => void; children: ReactNode }) {
  return (
    <label style={{ display: 'inline-flex', alignItems: 'center', gap: 9, cursor: 'pointer', userSelect: 'none', fontSize: 13, color: FG1 }}>
      <input type="checkbox" checked={checked} onChange={(e) => onChange(e.target.checked)} style={{ display: 'none' }} />
      <span aria-hidden style={{ width: 18, height: 18, borderRadius: 4, background: checked ? 'linear-gradient(180deg,#e85cff,#b23cf0)' : 'linear-gradient(180deg,#0b0b10,#060608)', border: checked ? '1.5px solid #e85cff' : '1.5px solid var(--immersive-line-2, rgba(255,255,255,0.1))', display: 'grid', placeItems: 'center', boxShadow: checked ? `0 0 10px ${MAGENTA_GLOW}` : 'inset 0 1px 2px rgba(0,0,0,0.5)' }}>
        {checked && <span style={{ width: 9, height: 5, borderLeft: '2px solid #0b0d12', borderBottom: '2px solid #0b0d12', transform: 'rotate(-45deg) translate(1px,-1px)' }} />}
      </span>
      {children}
    </label>
  );
}

// ============================================================================
//  Main panel
// ============================================================================

const shellStyle: CSSProperties = { display: 'flex', flexDirection: 'column', height: '100%', minHeight: 0, background: 'var(--immersive-panel, #0e0e14)' };
const bodyStyle: CSSProperties = { padding: 14, display: 'flex', flexDirection: 'column', gap: 12, background: 'var(--immersive-panel, #0e0e14)', boxSizing: 'border-box', flex: 1, minHeight: 0, overflow: 'auto' };

function MercuryLuxPanel({ api }: { api: MluxApi }) {
  const [config, setConfig] = useState<MluxConfig>(DEFAULT_CONFIG);
  const [status, setStatus] = useState<MluxStatus | null>(null);
  const [loaded, setLoaded] = useState(false);
  const [showSettings, setShowSettings] = useState(false);
  const [view, setView] = useState<'bars' | 'analog'>('bars');

  useEffect(() => {
    let dead = false;
    (async () => {
      const cfg = await api.getConfig();
      if (dead) return;
      if (cfg) setConfig({ ...DEFAULT_CONFIG, ...cfg });
      const s = await api.getStatus();
      if (!dead && s) setStatus(s);
      setLoaded(true);
    })();
    return () => { dead = true; };
  }, [api]);

  useEffect(() => {
    if (!config.enabled) return;
    let dead = false;
    const tick = async () => { const s = await api.getStatus(); if (!dead && s) setStatus(s); };
    const t = setInterval(tick, Math.max(200, config.pollingIntervalMs));
    return () => { dead = true; clearInterval(t); };
  }, [api, config.enabled, config.pollingIntervalMs]);

  // Until the backend transport is live (or when disabled), fall back to DEMO so
  // the panel is fully visible. A real connected backend overrides this.
  const usingDemo = !status || status.protocolPending || !status.connected;
  const s = usingDemo ? DEMO_STATUS : status;
  const live = !usingDemo;                    // true only on a real connected link
  const demoLive = usingDemo;                  // demo renders "lit" so the layout reads

  const isOperate = s.operateMode === 'OPERATE';
  const tempUnit = config.temperatureUnit === 'C' ? '°C' : '°F';
  const tempMax = config.temperatureUnit === 'C' ? 70 : 160;
  const tempTicks: Tick[] = config.temperatureUnit === 'C'
    ? [{ value: 50, label: '50', tone: 'warn' }, { value: 65, label: '65', tone: 'bad' }]
    : [{ value: 122, label: '122', tone: 'warn' }, { value: 150, label: '150', tone: 'bad' }];

  if (showSettings) {
    return (
      <div style={shellStyle}>
        <div style={bodyStyle} aria-label="Mercury LUX settings">
          <MercuryLuxSettings api={api} config={config} status={status}
            onSave={async (next) => { const r = await api.saveConfig(next); setConfig(next); if (r) setStatus(r); return r; }}
            onClose={() => setShowSettings(false)} />
        </div>
      </div>
    );
  }

  return (
    <div style={shellStyle}>
      <div style={bodyStyle} aria-label="Mercury LUX amplifier — output, status, controls">

        {/* Header: wordmark, callsign, ON AIR, view toggle, settings */}
        <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 10, flexWrap: 'wrap' }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: 9, flexShrink: 0 }}>
            <Bolt color={MAGENTA} glowColor={MAGENTA_GLOW} size={17} />
            <span style={{ fontSize: 13, fontWeight: 800, letterSpacing: '0.14em', color: FG1, whiteSpace: 'nowrap' }}>
              MERCURY <span style={{ color: MAGENTA, textShadow: `0 0 14px ${MAGENTA_GLOW}` }}>LUX</span>
            </span>
            {s.callsign && <span style={{ fontFamily: MONO, fontSize: 11, color: MAGENTA, letterSpacing: '0.08em', textShadow: `0 0 10px ${MAGENTA_GLOW}` }}>{s.callsign}</span>}
          </div>
          <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
            <OnAir on={demoLive ? s.onAir : (live && s.onAir)} />
            <div style={{ display: 'inline-flex', borderRadius: 6, overflow: 'hidden', border: '1px solid var(--immersive-line-2, rgba(255,255,255,0.1))' }}>
              {(['bars', 'analog'] as const).map((m) => (
                <button key={m} type="button" onClick={() => setView(m)} style={{ fontFamily: 'inherit', fontSize: 9.5, fontWeight: 700, letterSpacing: '0.1em', textTransform: 'uppercase', padding: '7px 10px', border: 0, cursor: 'pointer', background: view === m ? 'linear-gradient(180deg,#2a1330,#1d0d22)' : 'transparent', color: view === m ? '#fbe9ff' : FG3, transition: '0.15s' }}>{m === 'bars' ? 'Bars' : 'Meters'}</button>
              ))}
            </div>
            <PanelBtn small tone="ghost" onClick={() => setShowSettings(true)} title="Settings">Setup</PanelBtn>
          </div>
        </div>

        {/* Hero: bar meters OR analog meters */}
        <Section title="Output" meta={`${s.model ?? 'Mercury LUX'} · FW ${s.firmware ?? '—'}`}>
          {view === 'bars' ? (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 13 }}>
              <BarMeter label="Forward Power" reading={s.forward} max={s.forward?.maxValue ?? 1500} ticks={[{ value: 1200, label: '1200' }, { value: 1500, label: '1500' }]} live={demoLive || live} />
              <BarMeter label="Reflected Power" reading={s.reflected} max={150} ticks={[{ value: 75, label: '75', tone: 'warn' }, { value: 125, label: '125', tone: 'bad' }]} live={demoLive || live} />
              <BarMeter label="Drain Current" reading={s.drainCurrent} max={50} ticks={[{ value: 38, label: '38' }, { value: 45, label: '45', tone: 'bad' }]} decimals={1} live={demoLive || live} />
              <BarMeter label="Temperature" reading={s.temperature ? { ...s.temperature, unit: tempUnit } : null} max={tempMax} ticks={tempTicks} live={demoLive || live} />
              <BarMeter label="Drain Voltage" reading={s.drainVoltage} max={60} ticks={[{ value: 50, label: '50' }, { value: 55, label: '55' }]} decimals={1} live={demoLive || live} />
            </div>
          ) : (
            <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 10 }}>
              <AnalogMeter label="Forward · W" reading={s.forward} max={s.forward?.maxValue ?? 1500} live={demoLive || live} />
              <AnalogMeter label="Reflected · W" reading={s.reflected} max={150} redline={125} live={demoLive || live} />
            </div>
          )}
        </Section>

        {/* Status chips */}
        <Section padding="12px 14px">
          <div style={{ display: 'grid', gridTemplateColumns: 'repeat(4, minmax(0,1fr))', gap: 8 }}>
            <Chip k="SWR" v={fmt(s.swr?.value, 2)} tone={swrTone(s.swr?.value)} />
            <Chip k="Band" v={s.band ?? '—'} />
            <Chip k="Source" v={s.bandSource ?? '—'} dim={!s.bandSource} />
            <Chip k="Warning" v={s.warning ?? 'None'} tone={s.warning ? 'bad' : 'good'} />
          </div>
        </Section>

        {/* Control row */}
        <Section padding="14px">
          <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 12, flexWrap: 'wrap' }}>
            <SegState isOperate={isOperate} connected={live} onSelect={(v) => void api.setOperate(v).then((r) => r && setStatus(r))} />
            <div style={{ display: 'flex', alignItems: 'center', gap: 12, flexWrap: 'wrap' }}>
              <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
                <span style={{ fontSize: 8.5, letterSpacing: '0.16em', textTransform: 'uppercase', color: FG3, fontWeight: 700 }}>Ant</span>
                {(s.antennas ?? []).map((a) => (
                  <Pill key={a.number} active={a.active} disabled={!live} onClick={() => void api.setAntenna(a.number).then((r) => r && setStatus(r))} title={`Antenna ${a.number}`}>{a.label ?? a.number}</Pill>
                ))}
              </div>
              <PanelBtn small tone={s.fanMax ? 'warn-active' : 'default'} disabled={!live} onClick={() => void api.setFan(!s.fanMax).then((r) => r && setStatus(r))} title="Toggle fan maximum">Fan {s.fanMax ? 'Max' : 'Auto'}</PanelBtn>
              <PanelBtn small tone="ghost-danger" disabled={!live || !s.warning} onClick={() => void api.reset().then((r) => r && setStatus(r))}>Reset</PanelBtn>
            </div>
          </div>
        </Section>

        {/* Footer */}
        <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', padding: '6px 4px 2px', fontSize: 9.5, color: FG3, letterSpacing: '0.12em', textTransform: 'uppercase', gap: 12, flexWrap: 'wrap' }}>
          <div style={{ display: 'flex', gap: 16, alignItems: 'center', flexWrap: 'wrap' }}>
            <span style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
              <Bolt size={12} color={live ? GOOD : usingDemo ? MAGENTA : FG3} glowColor={usingDemo ? MAGENTA_GLOW : undefined} />
              {live ? 'Live' : 'Demo'}
            </span>
            <span>Link <span style={{ fontFamily: MONO, color: FG1, textTransform: 'none' }}>{s.transport}</span></span>
            {status?.lastSampleUtc && <span>Sample <span style={{ fontFamily: MONO, color: FG1, textTransform: 'none' }}>{fmtAge(status.lastSampleUtc)}</span></span>}
          </div>
        </div>

        {usingDemo && loaded && (
          <Toast ok tone="idle">
            <strong style={{ color: WARN }}>Demo data.</strong> Live monitoring is pending the Mercury LUX wire protocol. The panel cannot key, switch, or tune the amplifier in this build — controls activate once the serial/WebSocket transport is wired and bench-verified.
          </Toast>
        )}
      </div>
    </div>
  );
}

// ============================================================================
//  Settings drawer
// ============================================================================

function MercuryLuxSettings({ api, config, status, onSave, onClose }: { api: MluxApi; config: MluxConfig; status: MluxStatus | null; onSave: (cfg: MluxConfig) => Promise<MluxStatus | null>; onClose: () => void }) {
  const [enabled, setEnabled] = useState(config.enabled);
  const [link, setLink] = useState<'serial' | 'websocket'>(config.link);
  const [comPort, setComPort] = useState(config.comPort);
  const [baud, setBaud] = useState(String(config.baudRate));
  const [host, setHost] = useState(config.host);
  const [port, setPort] = useState(String(config.port));
  const [tempUnit, setTempUnit] = useState<'F' | 'C'>(config.temperatureUnit);
  const [saving, setSaving] = useState(false);
  const [test, setTest] = useState<TestResult | null>(null);
  const [testing, setTesting] = useState(false);

  const next = useMemo<MluxConfig>(() => ({
    enabled, link, comPort: comPort.trim() || 'COM5', baudRate: Number(baud) || 115200,
    host: host.trim() || '127.0.0.1', port: Number(port) || 8765,
    pollingIntervalMs: config.pollingIntervalMs, temperatureUnit: tempUnit,
  }), [enabled, link, comPort, baud, host, port, tempUnit, config.pollingIntervalMs]);

  return (
    <form onSubmit={async (e) => { e.preventDefault(); setSaving(true); try { await onSave(next); } finally { setSaving(false); } }} style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
      <div style={{ display: 'flex', alignItems: 'baseline', justifyContent: 'space-between' }}>
        <span style={{ fontSize: 13, fontWeight: 800, letterSpacing: '0.22em', color: FG1 }}>MERCURY <span style={{ color: MAGENTA }}>LUX</span> · SETUP</span>
        <PanelBtn small tone="ghost" onClick={onClose}>Back</PanelBtn>
      </div>

      <Section title="Connection" meta={link === 'serial' ? 'USB Serial' : 'WebSocket bridge'} padding="14px">
        <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
          <Check checked={enabled} onChange={setEnabled}>Enabled <span style={{ color: FG3 }}>(poll the amplifier)</span></Check>

          <div style={{ display: 'flex', gap: 5 }}>
            <Pill active={link === 'serial'} onClick={() => setLink('serial')} title="Direct USB serial to the amp">Serial · USB</Pill>
            <Pill active={link === 'websocket'} onClick={() => setLink('websocket')} title="MercuryXPc TLS WebSocket bridge over LAN">WebSocket</Pill>
          </div>

          {link === 'serial' ? (
            <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 12 }}>
              <Field label="COM Port" help="The amp's USB virtual COM port (Windows Device Manager → Ports).">
                <Input value={comPort} onChange={setComPort} placeholder="COM5" />
              </Field>
              <Field label="Baud Rate">
                <Input value={baud} onChange={setBaud} type="number" />
              </Field>
            </div>
          ) : (
            <div style={{ display: 'grid', gridTemplateColumns: '2fr 1fr', gap: 12 }}>
              <Field label="Host" help="Where the MercuryXPc companion app's bridge is reachable.">
                <Input value={host} onChange={setHost} placeholder="127.0.0.1" />
              </Field>
              <Field label="Port"><Input value={port} onChange={setPort} type="number" /></Field>
            </div>
          )}

          <Field label="Temperature Unit">
            <div style={{ display: 'flex', gap: 5 }}>
              <Pill active={tempUnit === 'F'} onClick={() => setTempUnit('F')}>°F</Pill>
              <Pill active={tempUnit === 'C'} onClick={() => setTempUnit('C')}>°C</Pill>
            </div>
          </Field>

          <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
            <PanelBtn small onClick={async () => { setTesting(true); setTest(null); try { setTest(await api.test(next)); } finally { setTesting(false); } }} disabled={testing}>
              {testing ? 'Testing…' : 'Test Link'}
            </PanelBtn>
            <div style={{ display: 'flex', gap: 8 }}>
              <PanelBtn small tone="ghost" onClick={onClose}>Cancel</PanelBtn>
              <PanelBtn small tone="magenta" type="submit" disabled={saving}>{saving ? 'Saving…' : 'Save'}</PanelBtn>
            </div>
          </div>

          {test && <Toast ok={test.ok}>{test.ok ? '✓ Link reachable' : `✗ ${test.error ?? 'unreachable'}`}</Toast>}
          {status?.protocolPending && (
            <Toast ok tone="idle">
              The Mercury LUX command set is undocumented; this build ships the console and connection settings only. Control activates once the wire protocol is captured and bench-verified — see the plugin README.
            </Toast>
          )}
          {status?.error && <Toast ok={false}>{status.error}</Toast>}
        </div>
      </Section>
    </form>
  );
}

// ============================================================================
//  Module entry
// ============================================================================

export default function register(api: ZeusPluginApi) {
  const mluxApi = makeApi(api);
  api.registerPanel({
    id: 'mercurylux.main',
    component: (props: object) => <MercuryLuxPanel api={mluxApi} {...props} />,
  });
}

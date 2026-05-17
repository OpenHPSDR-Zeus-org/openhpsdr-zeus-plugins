// SPDX-License-Identifier: GPL-2.0-or-later
//
// RF-Kit RF2K-S amplifier plugin — UI module.
//
// Self-contained relocation of the in-tree Rf2kPanel.tsx. The original
// imported BigArc (Zeus's immersive-meters arc gauge), the `useRf2kStore`
// zustand store, and the `/api/rf2k/*` REST client. None of those are
// available in the plugin sandbox, so this module inlines:
//
//   * A reduced-fidelity BigArc clone (forward-power and SWR modes only,
//     same lamp-glow palette via the existing `--immersive-*` tokens with
//     hardcoded fallbacks).
//   * Local React state for the config/status polling that the store used
//     to own.
//   * A `callBackend` wrapper around the host's `ZeusPluginApi` for every
//     /status, /config, /operate, /interface, /antenna, /reset, /test,
//     /tune, /bypass, /click round-trip.
//
// Visual contract: hero arcs (forward W + SWR ratio), status chip strip
// (band/freq/antenna/SWR/tuner/temp/V/A), state row (Operate/Standby
// segment, status pill, Tune/Bypass), antenna picker, settings drawer
// (connection + control-source + VNC click calibration). The maintainer
// is strict about visual fidelity, so chrome/colours/typography mirror
// the original 1:1.

import { useEffect, useState, type CSSProperties, type ReactNode } from 'react';

// ============================================================================
//  ZeusPluginApi — contract from the host (the same shape as the sample
//  Amplifier plugin in samples/Amplifier/ui/amplifier.tsx).
// ============================================================================

interface ZeusPluginApi {
  registerPanel(spec: { id: string; component: React.ComponentType }): void;
  callBackend(method: string, path: string, body?: unknown): Promise<Response>;
}

// ============================================================================
//  Wire types — mirror the backend's Rf2kPlugin.cs DTOs (camelCase by
//  ASP.NET default). Field names are unchanged from the legacy
//  /api/rf2k/* surface to preserve operator muscle memory.
// ============================================================================

type Rf2kReading = { value: number; unit: string | null };
type Rf2kPeakReading = { value: number; maxValue: number; unit: string | null };

type Rf2kInfo = {
  device: string | null;
  softwareVersion: { gui: number | null; controller: number | null } | null;
  customDeviceName: string | null;
};

type Rf2kData = {
  band: Rf2kReading | null;
  frequency: Rf2kReading | null;
  status: string | null;
};

type Rf2kPower = {
  temperature: Rf2kReading | null;
  voltage: Rf2kReading | null;
  current: Rf2kReading | null;
  forward: Rf2kPeakReading | null;
  reflected: Rf2kPeakReading | null;
  swr: Rf2kPeakReading | null;
};

type Rf2kTuner = {
  mode: string | null;
  setup: string | null;
  l: number | null;
  c: number | null;
  tunedFrequency: number | null;
};

type Rf2kAntenna = {
  type: string | null;
  number: number | null;
  state: string | null;
};

type Rf2kActiveAntenna = {
  type: string | null;
  number: number | null;
};

type Rf2kStatus = {
  enabled: boolean;
  connected: boolean;
  host: string;
  port: number;
  info: Rf2kInfo | null;
  data: Rf2kData | null;
  power: Rf2kPower | null;
  tuner: Rf2kTuner | null;
  operateMode: string | null;
  operationalInterface: string | null;
  operationalInterfaceError: string | null;
  activeAntenna: Rf2kActiveAntenna | null;
  antennas: Rf2kAntenna[] | null;
  error: string | null;
  lastSampleUtc: string | null;
};

type Rf2kConfig = {
  enabled: boolean;
  host: string;
  port: number;
  vncPort: number;
  vncPassword: string;
  pollingIntervalMs: number;
  tuneClickX: number;
  tuneClickY: number;
  bypassClickX: number;
  bypassClickY: number;
};

type Rf2kTestResult = { ok: boolean; error: string | null };

type InterfaceMode = 'UNIV' | 'CAT' | 'UDP' | 'TCI';
const INTERFACE_MODES: InterfaceMode[] = ['UNIV', 'CAT', 'UDP', 'TCI'];

const DEFAULT_CONFIG: Rf2kConfig = {
  enabled: false,
  host: '10.70.120.41',
  port: 8080,
  vncPort: 5900,
  vncPassword: '',
  pollingIntervalMs: 1000,
  tuneClickX: 0,
  tuneClickY: 0,
  bypassClickX: 0,
  bypassClickY: 0,
};

// ============================================================================
//  REST client — wraps api.callBackend so the rest of the module reads
//  like the previous /api/rf2k/* code path.
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
    getStatus: async (): Promise<Rf2kStatus | null> => {
      const r = await plugin.callBackend('GET', '/status');
      return jsonOr<Rf2kStatus | null>(r, null);
    },
    getConfig: async (): Promise<Rf2kConfig | null> => {
      const r = await plugin.callBackend('GET', '/config');
      return jsonOr<Rf2kConfig | null>(r, null);
    },
    saveConfig: async (cfg: Rf2kConfig): Promise<Rf2kStatus | null> => {
      const r = await plugin.callBackend('POST', '/config', cfg);
      return jsonOr<Rf2kStatus | null>(r, null);
    },
    setOperate: async (mode: 'OPERATE' | 'STANDBY'): Promise<Rf2kStatus | null> => {
      const r = await plugin.callBackend('POST', '/operate', { mode });
      return jsonOr<Rf2kStatus | null>(r, null);
    },
    setInterface: async (iface: InterfaceMode): Promise<Rf2kStatus | null> => {
      // Wire field is `interface` for parity with the legacy backend.
      const r = await plugin.callBackend('POST', '/interface', { interface: iface });
      return jsonOr<Rf2kStatus | null>(r, null);
    },
    setAntenna: async (type: 'INTERNAL' | 'EXTERNAL', number: number | null): Promise<Rf2kStatus | null> => {
      const r = await plugin.callBackend('POST', '/antenna', { type, number });
      return jsonOr<Rf2kStatus | null>(r, null);
    },
    reset: async (): Promise<Rf2kStatus | null> => {
      const r = await plugin.callBackend('POST', '/reset');
      return jsonOr<Rf2kStatus | null>(r, null);
    },
    test: async (host: string, port: number): Promise<Rf2kTestResult> => {
      const r = await plugin.callBackend('POST', '/test', { host, port });
      return jsonOr<Rf2kTestResult>(r, { ok: false, error: 'request failed' });
    },
    tune: async (): Promise<Rf2kTestResult> => {
      const r = await plugin.callBackend('POST', '/tune');
      return jsonOr<Rf2kTestResult>(r, { ok: false, error: 'request failed' });
    },
    bypass: async (): Promise<Rf2kTestResult> => {
      const r = await plugin.callBackend('POST', '/bypass');
      return jsonOr<Rf2kTestResult>(r, { ok: false, error: 'request failed' });
    },
    click: async (x: number, y: number): Promise<Rf2kTestResult> => {
      const r = await plugin.callBackend('POST', '/click', { x, y });
      return jsonOr<Rf2kTestResult>(r, { ok: false, error: 'request failed' });
    },
  };
}

type Rf2kApi = ReturnType<typeof makeApi>;

// ============================================================================
//  Inline arc gauge — a reduced-fidelity BigArc replacement.
//
//  Modes: 'watts' (0..maxWatts) and 'swr' (1.0..3.0+). Same lamp-glow
//  chrome as the Zeus immersive meters via `--immersive-*` tokens, with
//  hex fallbacks so the gauge stays readable if the panel mounts in a
//  shell that doesn't supply those tokens.
// ============================================================================

const CX = 120;
const CY = 124;
const R = 92;
const ARC_LEN = Math.PI * R;

function pointAt(fraction: number, radius: number): { x: number; y: number } {
  const angleDeg = 180 + 180 * fraction;
  const a = (angleDeg * Math.PI) / 180;
  return {
    x: CX + Math.cos(a) * radius,
    y: CY + Math.sin(a) * radius,
  };
}

type AxisTick = { frac: number; label: string; highlight?: boolean };

function fmtWattsTick(watts: number, max: number): string {
  if (max <= 0) return '0';
  if (max < 10) return watts.toFixed(1);
  return Math.round(watts).toString();
}

function wattsTicks(maxWatts: number): AxisTick[] {
  const safeMax = isFinite(maxWatts) && maxWatts > 0 ? maxWatts : 100;
  return Array.from({ length: 6 }, (_, i) => {
    const w = (i / 5) * safeMax;
    return {
      frac: i / 5,
      label: fmtWattsTick(w, safeMax),
      highlight: i === 5,
    };
  });
}

const SWR_MIN = 1.0;
const SWR_MAX = 3.0;
const SWR_TICKS: AxisTick[] = [
  { frac: 0.0, label: '1.0' },
  { frac: 0.25, label: '1.5' },
  { frac: 0.5, label: '2.0', highlight: true },
  { frac: 0.75, label: '2.5' },
  { frac: 1.0, label: '3+' },
];

function swrToFrac(ratio: number): number {
  if (!isFinite(ratio) || ratio < SWR_MIN) return 0;
  return Math.max(0, Math.min(1, (ratio - SWR_MIN) / (SWR_MAX - SWR_MIN)));
}

interface ArcWattsProps {
  mode: 'watts';
  watts: number;
  maxWatts: number;
  label: string;
  units?: string;
  defsId: string;
}

interface ArcSwrProps {
  mode: 'swr';
  ratio: number;
  label: string;
  units?: string;
  defsId: string;
}

type ArcProps = ArcWattsProps | ArcSwrProps;

function BigArc(props: ArcProps) {
  let liveFrac = 0;
  let silent = true;
  let over = false;
  let ticks: AxisTick[] = [];
  let readoutText = '—';
  let readoutUnit = 'W';
  if (props.mode === 'watts') {
    const { watts, maxWatts } = props;
    const finite = isFinite(watts) && watts > 0;
    const safeMax = isFinite(maxWatts) && maxWatts > 0 ? maxWatts : 100;
    liveFrac = finite ? Math.max(0, Math.min(1, watts / safeMax)) : 0;
    silent = !finite;
    over = liveFrac >= 1.0;
    ticks = wattsTicks(safeMax);
    const decimals = safeMax < 10 ? 2 : 1;
    readoutText = finite ? watts.toFixed(decimals) : '—';
    readoutUnit = 'W';
  } else {
    const { ratio } = props;
    const finite = isFinite(ratio) && ratio >= SWR_MIN;
    liveFrac = finite ? swrToFrac(ratio) : 0;
    silent = !finite;
    over = finite && ratio >= 2.0;
    ticks = SWR_TICKS;
    readoutText = finite ? ratio.toFixed(2) : '—';
    readoutUnit = ':1';
  }

  const fillLen = ARC_LEN * liveFrac;
  const fillDash = `${fillLen.toFixed(1)} ${(ARC_LEN + 5).toFixed(1)}`;
  const needleAngle = -90 + 180 * liveFrac;

  const fillGradId = `${props.defsId}-fill`;
  const glowGradId = `${props.defsId}-glow`;
  const blurFilterId = `${props.defsId}-blur`;
  const units = props.units ?? readoutUnit;

  const cardStyle: CSSProperties = {
    position: 'relative',
    aspectRatio: '1.55 / 1',
    borderRadius: 7,
    background:
      'radial-gradient(80% 95% at 50% 95%, var(--immersive-lamp-bloom-1, rgba(255,234,170,0.10)), var(--immersive-lamp-bloom-2, rgba(255,220,140,0.05)) 45%, transparent 72%),' +
      ' radial-gradient(60% 60% at 50% 70%, var(--immersive-lamp-bloom-3, rgba(255,220,140,0.03)), transparent 65%),' +
      ' linear-gradient(180deg, var(--immersive-lamp-well-top, #18181c) 0%, var(--immersive-lamp-well-bot, #0a0a0c) 100%)',
    border: '1px solid var(--immersive-lamp-border, rgba(245,240,200,0.10))',
    boxShadow:
      'inset 0 1px 0 var(--immersive-lamp-rim, rgba(255,250,220,0.06)), inset 0 -22px 40px rgba(255,240,180,0.05), inset 0 0 50px rgba(0,0,0,0.55)',
    overflow: 'hidden',
  };
  const cardBloomStyle: CSSProperties = {
    position: 'absolute',
    left: '50%',
    bottom: '-30%',
    width: '120%',
    height: '90%',
    transform: 'translateX(-50%)',
    background:
      'radial-gradient(50% 50% at 50% 50%, var(--immersive-lamp-bloom-blob, rgba(255,220,140,0.08)), transparent 70%)',
    pointerEvents: 'none',
    filter: 'blur(2px)',
  };
  const labelStyle: CSSProperties = {
    position: 'absolute',
    top: 9,
    left: 12,
    fontSize: 9,
    letterSpacing: '0.18em',
    textTransform: 'uppercase',
    color: 'var(--immersive-lamp-label, rgba(245,240,210,0.7))',
    fontWeight: 700,
    display: 'flex',
    alignItems: 'center',
    gap: 6,
    zIndex: 1,
  };
  const pinStyle: CSSProperties = {
    width: 5,
    height: 5,
    borderRadius: '50%',
    background: 'var(--immersive-lamp-pin, #f5e9b8)',
    boxShadow: '0 0 8px var(--immersive-lamp-pin-glow, rgba(245,233,184,0.6))',
  };
  const unitsStyle: CSSProperties = {
    position: 'absolute',
    top: 9,
    right: 12,
    fontFamily: 'var(--font-mono, ui-monospace, "SF Mono", monospace)',
    fontSize: 9,
    color: 'var(--immersive-lamp-units, rgba(245,240,210,0.55))',
    letterSpacing: '0.10em',
    textTransform: 'uppercase',
    zIndex: 1,
  };
  const readoutStyle: CSSProperties = {
    position: 'absolute',
    left: 0,
    right: 0,
    bottom: 10,
    textAlign: 'center',
    fontFamily: 'var(--font-mono, ui-monospace, "SF Mono", monospace)',
    fontSize: 24,
    fontWeight: 600,
    letterSpacing: '-0.01em',
    fontVariantNumeric: 'tabular-nums',
    lineHeight: 1,
    color: over ? '#ffb8a4' : 'var(--immersive-lamp-readout, #fbf3d3)',
    textShadow: over
      ? '0 0 14px var(--immersive-tx-glow, rgba(239,107,84,0.45))'
      : '0 0 14px var(--immersive-lamp-readout-glow, rgba(255,240,180,0.18))',
  };
  const unitSpanStyle: CSSProperties = {
    color: 'var(--immersive-lamp-corner-em, rgba(245,240,210,0.5))',
    fontSize: 10.5,
    fontWeight: 500,
    marginLeft: 4,
    letterSpacing: '0.05em',
  };

  return (
    <div style={cardStyle} aria-hidden="true">
      <div style={cardBloomStyle} />
      <span style={labelStyle}>
        <span style={pinStyle} />
        {props.label}
      </span>
      <span style={unitsStyle}>{units}</span>
      <svg
        viewBox="0 0 240 150"
        preserveAspectRatio="xMidYMid meet"
        style={{ position: 'absolute', inset: 0, width: '100%', height: '100%', display: 'block' }}
      >
        <defs>
          <linearGradient id={fillGradId} x1="0" x2="1" y1="0" y2="0">
            <stop offset="0" stopColor="var(--immersive-good, #5cd49a)" />
            <stop offset="0.55" stopColor="var(--immersive-good, #5cd49a)" />
            <stop offset="0.78" stopColor="var(--immersive-warn, #f4c168)" />
            <stop offset="1" stopColor="var(--immersive-tx, #ef6b54)" />
          </linearGradient>
          <radialGradient id={glowGradId} cx="50%" cy="100%" r="80%">
            <stop offset="0" stopColor="#ffffff" stopOpacity="0.10" />
            <stop offset="1" stopColor="#ffffff" stopOpacity="0" />
          </radialGradient>
          <filter id={blurFilterId} x="-40%" y="-40%" width="180%" height="180%">
            <feGaussianBlur stdDeviation="3" />
          </filter>
        </defs>
        <ellipse cx={CX} cy={135} rx={110} ry={40} fill={`url(#${glowGradId})`} />
        <path
          d={`M 28 ${CY} A ${R} ${R} 0 0 1 212 ${CY}`}
          fill="none"
          stroke="var(--immersive-arc-track-rim, rgba(255,255,255,0.06))"
          strokeWidth={14}
          strokeLinecap="round"
        />
        <path
          d={`M 28 ${CY} A ${R} ${R} 0 0 1 212 ${CY}`}
          fill="none"
          stroke="var(--immersive-arc-track-shadow, rgba(0,0,0,0.4))"
          strokeWidth={10}
        />
        <path
          d={`M 28 ${CY} A ${R} ${R} 0 0 1 212 ${CY}`}
          fill="none"
          stroke={`url(#${fillGradId})`}
          strokeWidth={9}
          strokeLinecap="round"
          strokeDasharray={fillDash}
          filter={`url(#${blurFilterId})`}
          opacity={0.85}
        />
        <path
          d={`M 28 ${CY} A ${R} ${R} 0 0 1 212 ${CY}`}
          fill="none"
          stroke={`url(#${fillGradId})`}
          strokeWidth={6}
          strokeLinecap="round"
          strokeDasharray={fillDash}
        />
        <g strokeWidth={1}>
          {ticks.map((t, i) => {
            const inner = pointAt(t.frac, R - 9);
            const outer = pointAt(t.frac, R + 5);
            const stroke = t.highlight
              ? 'var(--immersive-tx, #ef6b54)'
              : 'var(--immersive-lamp-tick, rgba(245,240,210,0.55))';
            const sw = t.highlight ? 1.6 : 1;
            return (
              <line
                key={`t-${i}`}
                x1={inner.x.toFixed(1)}
                y1={inner.y.toFixed(1)}
                x2={outer.x.toFixed(1)}
                y2={outer.y.toFixed(1)}
                stroke={stroke}
                strokeWidth={sw}
              />
            );
          })}
        </g>
        <g
          fontFamily="var(--font-mono, ui-monospace, 'SF Mono', monospace)"
          fontSize={8}
          textAnchor="middle"
        >
          {ticks
            .filter((t) => t.label !== '')
            .map((t, i) => {
              const lp = pointAt(t.frac, R + 15);
              return (
                <text
                  key={`tl-${i}`}
                  x={lp.x.toFixed(1)}
                  y={(lp.y + 3).toFixed(1)}
                  fill={t.highlight ? 'var(--immersive-tx, #ef6b54)' : 'var(--immersive-lamp-label, rgba(245,240,210,0.7))'}
                >
                  {t.label}
                </text>
              );
            })}
        </g>
        {!silent && (
          <g transform={`rotate(${needleAngle.toFixed(2)} ${CX} ${CY})`}>
            <line
              x1={CX}
              y1={CY}
              x2={CX}
              y2={36}
              stroke="var(--immersive-lamp-needle, #f5e9b8)"
              strokeWidth={2}
              strokeLinecap="round"
            />
            <line
              x1={CX}
              y1={CY}
              x2={CX}
              y2={50}
              stroke="var(--immersive-lamp-needle-bri, #fff7d6)"
              strokeWidth={0.8}
              opacity={0.65}
            />
          </g>
        )}
        <circle cx={CX} cy={CY} r={9} fill="#15151a" stroke="rgba(245,240,210,0.38)" strokeWidth={1.4} />
        <circle
          cx={CX}
          cy={CY}
          r={3}
          fill="var(--immersive-lamp-needle, #f5e9b8)"
          style={{ filter: 'drop-shadow(0 0 5px var(--immersive-lamp-hub-glow, rgba(245,233,184,0.7)))' }}
        />
      </svg>
      <div style={readoutStyle}>
        {readoutText}
        <span style={unitSpanStyle}>{readoutUnit}</span>
      </div>
    </div>
  );
}

// ============================================================================
//  Section, chip, segment, pill, button, toast — chrome atoms lifted
//  verbatim from the original panel so the visual lift-and-shift stays
//  pixel-faithful.
// ============================================================================

interface MetaItem {
  key?: string;
  value: string;
}

function Section({
  title,
  led,
  meta,
  children,
  padding = 14,
}: {
  title?: string;
  led?: 'on' | 'warm' | 'tx';
  meta?: MetaItem[];
  children: ReactNode;
  padding?: number | string;
}) {
  const sectionStyle: CSSProperties = {
    background:
      'linear-gradient(180deg, var(--immersive-panel-2, #161620) 0%, var(--immersive-well, #0b0b10) 100%)',
    border: '1px solid var(--immersive-line, rgba(255,255,255,0.06))',
    borderRadius: 8,
    padding,
    boxShadow:
      'inset 0 1px 0 var(--immersive-rim, rgba(255,255,255,0.04)), inset 0 0 30px rgba(0,0,0,0.25)',
    position: 'relative',
  };
  return (
    <section style={sectionStyle}>
      {(title || meta) && <SecHeader title={title} led={led} meta={meta} />}
      {children}
    </section>
  );
}

function SecHeader({
  title,
  led,
  meta,
}: {
  title?: string;
  led?: 'on' | 'warm' | 'tx';
  meta?: MetaItem[];
}) {
  const dotColor =
    led === 'on'
      ? 'var(--immersive-accent, #4a9eff)'
      : led === 'warm'
        ? 'var(--immersive-warn, #f4c168)'
        : led === 'tx'
          ? 'var(--immersive-tx, #ef6b54)'
          : 'var(--fg-3, rgba(255,255,255,0.4))';
  const dotGlow =
    led === 'on'
      ? '0 0 6px var(--immersive-accent-glow, rgba(74,158,255,0.5))'
      : led === 'warm'
        ? '0 0 6px var(--immersive-warn-glow, rgba(244,193,104,0.5))'
        : led === 'tx'
          ? '0 0 6px var(--immersive-tx-glow, rgba(239,107,84,0.5))'
          : undefined;
  return (
    <div
      style={{
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'space-between',
        marginBottom: title ? 12 : 0,
      }}
    >
      {title && (
        <div
          style={{
            fontSize: 9.5,
            letterSpacing: '0.20em',
            textTransform: 'uppercase',
            color: 'var(--fg-2, rgba(255,255,255,0.6))',
            fontWeight: 700,
            display: 'flex',
            alignItems: 'center',
            gap: 9,
          }}
        >
          <span
            style={{
              width: 5,
              height: 5,
              borderRadius: '50%',
              background: dotColor,
              boxShadow: dotGlow,
            }}
          />
          {title}
        </div>
      )}
      {meta && meta.length > 0 && (
        <div
          style={{
            fontFamily: 'var(--font-mono, ui-monospace, "SF Mono", monospace)',
            fontSize: 9.5,
            color: 'var(--fg-3, rgba(255,255,255,0.4))',
            letterSpacing: '0.06em',
            display: 'flex',
            gap: 10,
            alignItems: 'center',
          }}
        >
          {meta.map((m, i) => (
            <span key={i}>
              {m.key && <span style={{ color: 'var(--fg-2, rgba(255,255,255,0.6))' }}>{m.key} </span>}
              <span>{m.value}</span>
            </span>
          ))}
        </div>
      )}
    </div>
  );
}

function Chip({
  k,
  v,
  tone,
  dim = false,
}: {
  k: string;
  v: string;
  tone?: 'good' | 'warn' | 'bad';
  dim?: boolean;
}) {
  let valueColor = 'var(--immersive-lamp-chip-text, #fbf3d3)';
  let glow: string | undefined = '0 0 8px var(--immersive-lamp-chip-text-glow, rgba(255,240,180,0.18))';
  if (tone === 'good') {
    valueColor = 'var(--immersive-good, #5cd49a)';
    glow = '0 0 8px var(--immersive-good-glow, rgba(92,212,154,0.5))';
  } else if (tone === 'warn') {
    valueColor = 'var(--immersive-warn, #f4c168)';
    glow = '0 0 8px var(--immersive-warn-glow, rgba(244,193,104,0.5))';
  } else if (tone === 'bad') {
    valueColor = 'var(--immersive-tx, #ef6b54)';
    glow = '0 0 8px var(--immersive-tx-glow, rgba(239,107,84,0.5))';
  } else if (dim) {
    valueColor = 'var(--fg-2, rgba(255,255,255,0.6))';
    glow = undefined;
  }
  return (
    <div
      style={{
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'space-between',
        gap: 10,
        padding: '8px 11px',
        borderRadius: 6,
        background:
          'radial-gradient(80% 100% at 50% 100%, var(--immersive-lamp-chip-bloom, rgba(255,220,140,0.06)), transparent 70%),' +
          ' linear-gradient(180deg, #161618 0%, #0c0c0e 100%)',
        border: '1px solid var(--immersive-lamp-border, rgba(245,240,200,0.10))',
        boxShadow:
          'inset 0 1px 0 var(--immersive-lamp-rim, rgba(255,250,220,0.06)), inset 0 -8px 14px rgba(255,240,180,0.03)',
      }}
    >
      <span
        style={{
          fontSize: 9,
          letterSpacing: '0.18em',
          textTransform: 'uppercase',
          color: 'var(--fg-3, rgba(255,255,255,0.4))',
          fontWeight: 700,
        }}
      >
        {k}
      </span>
      <span
        style={{
          fontFamily: 'var(--font-mono, ui-monospace, "SF Mono", monospace)',
          fontSize: 12,
          color: valueColor,
          fontWeight: 600,
          letterSpacing: '0.02em',
          textShadow: glow,
        }}
      >
        {v}
      </span>
    </div>
  );
}

function SegState({
  isOperate,
  isStandby,
  connected,
  onSelect,
}: {
  isOperate: boolean;
  isStandby: boolean;
  connected: boolean;
  onSelect: (v: 'operate' | 'standby') => void;
}) {
  const segStyle: CSSProperties = {
    display: 'inline-flex',
    padding: 3,
    borderRadius: 7,
    background:
      'linear-gradient(180deg, var(--immersive-well, #0b0b10) 0%, var(--immersive-well-2, #060608) 100%)',
    border: '1px solid var(--immersive-line, rgba(255,255,255,0.06))',
    boxShadow: 'inset 0 1px 2px rgba(0,0,0,0.6)',
  };
  return (
    <div style={segStyle}>
      <SegBtn
        active={isOperate}
        variant="operate"
        disabled={!connected}
        onClick={() => onSelect('operate')}
      >
        Operate
      </SegBtn>
      <SegBtn
        active={isStandby || (!isOperate && !isStandby)}
        variant="standby"
        disabled={!connected}
        onClick={() => onSelect('standby')}
      >
        Standby
      </SegBtn>
    </div>
  );
}

function SegBtn({
  active,
  variant,
  disabled,
  onClick,
  children,
}: {
  active: boolean;
  variant: 'operate' | 'standby';
  disabled?: boolean;
  onClick: () => void;
  children: ReactNode;
}) {
  const activeStyle: CSSProperties =
    variant === 'operate'
      ? {
          color: '#fbf8ec',
          background: 'linear-gradient(180deg,#33332e,#1f1f1c)',
          boxShadow:
            'inset 0 1px 0 rgba(255,250,220,0.26), 0 0 0 1px rgba(245,240,200,0.32), 0 0 16px var(--immersive-lamp-active-glow, rgba(255,240,180,0.4))',
        }
      : {
          color: '#ffd0c5',
          background: 'linear-gradient(180deg,#3a1a14,#2a110d)',
          boxShadow:
            'inset 0 1px 0 rgba(239,107,84,0.4), 0 0 0 1px rgba(239,107,84,0.45), 0 0 14px var(--immersive-tx-glow, rgba(239,107,84,0.5))',
        };
  const baseStyle: CSSProperties = {
    fontFamily: 'inherit',
    fontSize: 11.5,
    fontWeight: 700,
    letterSpacing: '0.14em',
    textTransform: 'uppercase',
    padding: '9px 18px',
    borderRadius: 5,
    border: 0,
    cursor: disabled ? 'not-allowed' : 'pointer',
    background: 'transparent',
    color: 'var(--fg-2, rgba(255,255,255,0.6))',
    transition: '0.15s',
    opacity: disabled ? 0.55 : 1,
    ...(active ? activeStyle : null),
  };
  return (
    <button type="button" style={baseStyle} onClick={onClick} disabled={disabled}>
      {children}
    </button>
  );
}

type BtnTone = 'default' | 'warn' | 'warn-active' | 'ghost' | 'ghost-danger' | 'primary';

function PanelBtn({
  tone = 'default',
  small = false,
  disabled,
  onClick,
  title,
  type = 'button',
  children,
}: {
  tone?: BtnTone;
  small?: boolean;
  disabled?: boolean;
  onClick?: () => void;
  title?: string;
  type?: 'button' | 'submit';
  children: ReactNode;
}) {
  const palette: Record<BtnTone, CSSProperties> = {
    default: {
      color: 'var(--fg-1, #e7e7ea)',
      background: 'linear-gradient(180deg,#171c27 0%, #11151d 100%)',
      borderColor: 'var(--immersive-line-2, rgba(255,255,255,0.1))',
    },
    warn: {
      color: 'var(--immersive-warn, #f4c168)',
      background: 'linear-gradient(180deg,#171c27 0%, #11151d 100%)',
      borderColor: 'rgba(244,193,104,0.35)',
    },
    'warn-active': {
      color: 'var(--immersive-warn, #f4c168)',
      background: 'linear-gradient(180deg,#3a2a14,#2a1d0d)',
      borderColor: 'rgba(244,193,104,0.7)',
      boxShadow:
        '0 0 16px var(--immersive-warn-glow, rgba(244,193,104,0.5)), inset 0 1px 0 rgba(244,193,104,0.4)',
    },
    ghost: {
      color: 'var(--fg-2, rgba(255,255,255,0.6))',
      background: 'transparent',
      borderColor: 'var(--immersive-line, rgba(255,255,255,0.06))',
    },
    'ghost-danger': {
      color: 'var(--immersive-tx, #ef6b54)',
      background: 'transparent',
      borderColor: 'rgba(239,107,84,0.35)',
    },
    primary: {
      color: '#0b0d12',
      background: 'linear-gradient(180deg,#f1f2f5,#cfd1d6)',
      borderColor: '#dadbe0',
      boxShadow:
        'inset 0 1px 0 rgba(255,255,255,0.45), 0 0 14px rgba(255,255,255,0.18)',
    },
  };
  const p = palette[tone];
  const style: CSSProperties = {
    fontFamily: 'inherit',
    fontSize: small ? 10 : 11.5,
    fontWeight: 700,
    letterSpacing: '0.14em',
    textTransform: 'uppercase',
    padding: small ? '6px 12px' : '10px 16px',
    borderRadius: 6,
    border: '1px solid',
    cursor: disabled ? 'not-allowed' : 'pointer',
    transition: '0.15s',
    opacity: disabled ? 0.5 : 1,
    boxShadow:
      tone === 'warn-active'
        ? p.boxShadow
        : 'inset 0 1px 0 var(--immersive-rim, rgba(255,255,255,0.04)), 0 1px 0 rgba(0,0,0,0.4)',
    ...p,
  };
  return (
    <button type={type} style={style} onClick={onClick} disabled={disabled} title={title}>
      {children}
    </button>
  );
}

function BtnGroup({ children }: { children: ReactNode }) {
  return (
    <div
      style={{
        display: 'inline-flex',
        gap: 0,
        borderRadius: 6,
        overflow: 'hidden',
        border: '1px solid var(--immersive-line-2, rgba(255,255,255,0.1))',
        boxShadow:
          'inset 0 1px 0 var(--immersive-rim, rgba(255,255,255,0.04)), 0 1px 0 rgba(0,0,0,0.4)',
      }}
    >
      {children}
    </div>
  );
}

function Pill({
  active,
  ext = false,
  disabled,
  onClick,
  children,
  title,
}: {
  active: boolean;
  ext?: boolean;
  disabled?: boolean;
  onClick: () => void;
  children: ReactNode;
  title?: string;
}) {
  const baseStyle: CSSProperties = {
    fontFamily: 'var(--font-mono, ui-monospace, "SF Mono", monospace)',
    fontSize: 11,
    fontWeight: 600,
    letterSpacing: '0.10em',
    padding: '7px 12px',
    borderRadius: 5,
    cursor: disabled ? 'not-allowed' : 'pointer',
    background: 'linear-gradient(180deg,#171c27 0%, #11151d 100%)',
    border: '1px solid var(--immersive-line-2, rgba(255,255,255,0.1))',
    color: 'var(--fg-2, rgba(255,255,255,0.6))',
    boxShadow: 'inset 0 1px 0 var(--immersive-rim, rgba(255,255,255,0.04))',
    transition: '0.12s',
    opacity: disabled ? 0.5 : 1,
  };
  const activeStyle: CSSProperties =
    ext && active
      ? {
          color: '#ffd0c5',
          background: 'linear-gradient(180deg,#3a1a14,#2a110d)',
          borderColor: 'rgba(239,107,84,0.5)',
          boxShadow:
            'inset 0 1px 0 rgba(239,107,84,0.3), 0 0 10px var(--immersive-tx-glow, rgba(239,107,84,0.5))',
        }
      : active
        ? {
            color: '#fbf8ec',
            background: 'linear-gradient(180deg,#33332f,#1f1f1d)',
            borderColor: 'rgba(245,240,200,0.45)',
            boxShadow:
              'inset 0 1px 0 rgba(255,250,220,0.20), 0 0 12px var(--immersive-lamp-active-glow, rgba(255,240,180,0.4))',
          }
        : {};
  return (
    <button
      type="button"
      style={{ ...baseStyle, ...activeStyle }}
      onClick={onClick}
      disabled={disabled}
      title={title}
    >
      {children}
    </button>
  );
}

const cornerEmStyle: CSSProperties = {
  fontStyle: 'normal',
  color: 'var(--fg-2, rgba(255,255,255,0.6))',
  fontWeight: 600,
  marginRight: 4,
  fontSize: 10,
  letterSpacing: '0.04em',
};

function ArcCorner({ children }: { children: ReactNode }) {
  return (
    <div
      style={{
        position: 'absolute',
        left: 12,
        bottom: 10,
        fontFamily: 'var(--font-mono, ui-monospace, "SF Mono", monospace)',
        fontSize: 10,
        color: 'var(--fg-3, rgba(255,255,255,0.4))',
        letterSpacing: '0.04em',
        pointerEvents: 'none',
      }}
    >
      {children}
    </div>
  );
}

const footValueStyle: CSSProperties = {
  fontFamily: 'var(--font-mono, ui-monospace, "SF Mono", monospace)',
  color: 'var(--fg-1, #e7e7ea)',
  letterSpacing: '0.04em',
  textTransform: 'none',
  fontWeight: 600,
};

const rowSplitStyle: CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  justifyContent: 'space-between',
  gap: 10,
};

function Foot({ children }: { children: ReactNode }) {
  return (
    <div
      style={{
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'space-between',
        padding: '10px 4px 2px',
        fontSize: 9.5,
        color: 'var(--fg-3, rgba(255,255,255,0.4))',
        letterSpacing: '0.14em',
        textTransform: 'uppercase',
        gap: 12,
        flexWrap: 'wrap',
      }}
    >
      {children}
    </div>
  );
}

function FootGrp({ children }: { children: ReactNode }) {
  return <div style={{ display: 'flex', alignItems: 'center', gap: 14, flexWrap: 'wrap' }}>{children}</div>;
}

function FootStat({
  ledTone,
  children,
}: {
  ledTone?: 'good' | 'warn' | 'idle';
  children: ReactNode;
}) {
  const ledStyle: CSSProperties | null = ledTone
    ? {
        width: 6,
        height: 6,
        borderRadius: '50%',
        background:
          ledTone === 'good'
            ? 'var(--immersive-good, #5cd49a)'
            : ledTone === 'warn'
              ? 'var(--immersive-warn, #f4c168)'
              : 'var(--fg-3, rgba(255,255,255,0.4))',
        boxShadow:
          ledTone === 'good'
            ? '0 0 6px var(--immersive-good-glow, rgba(92,212,154,0.5))'
            : ledTone === 'warn'
              ? '0 0 6px var(--immersive-warn-glow, rgba(244,193,104,0.5))'
              : undefined,
      }
    : null;
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
      {ledStyle && <span style={ledStyle} />}
      {children}
    </div>
  );
}

function Toast({
  ok,
  tone,
  children,
}: {
  ok: boolean;
  tone?: 'tx' | 'idle';
  children: ReactNode;
}) {
  const baseColor =
    tone === 'idle'
      ? 'var(--immersive-warn, #f4c168)'
      : ok
        ? '#d8f5e4'
        : '#ffd7cc';
  const baseBg =
    tone === 'idle'
      ? 'rgba(244,193,104,0.10)'
      : ok
        ? 'rgba(92,212,154,0.10)'
        : 'rgba(239,107,84,0.12)';
  const borderColor =
    tone === 'idle'
      ? 'rgba(244,193,104,0.45)'
      : ok
        ? 'rgba(92,212,154,0.45)'
        : 'rgba(239,107,84,0.45)';
  const glow =
    tone === 'idle'
      ? '0 0 8px var(--immersive-warn-glow, rgba(244,193,104,0.5))'
      : ok
        ? '0 0 8px var(--immersive-good-glow, rgba(92,212,154,0.5))'
        : '0 0 8px var(--immersive-tx-glow, rgba(239,107,84,0.5))';
  return (
    <div
      style={{
        padding: 8,
        fontSize: 11,
        color: baseColor,
        background: baseBg,
        border: `1px solid ${borderColor}`,
        borderRadius: 6,
        textShadow: glow,
        lineHeight: 1.45,
      }}
    >
      {children}
    </div>
  );
}

function PickerLabel({ children, hint }: { children: ReactNode; hint?: string }) {
  return (
    <div
      style={{
        fontSize: 9.5,
        letterSpacing: '0.20em',
        textTransform: 'uppercase',
        color: 'var(--fg-2, rgba(255,255,255,0.6))',
        fontWeight: 700,
        marginBottom: 8,
        display: 'flex',
        alignItems: 'center',
        gap: 8,
      }}
    >
      {children}
      {hint && (
        <span
          style={{
            marginLeft: 'auto',
            fontFamily: 'var(--font-mono, ui-monospace, "SF Mono", monospace)',
            fontSize: 9,
            color: 'var(--fg-3, rgba(255,255,255,0.4))',
            letterSpacing: '0.06em',
            textTransform: 'none',
            fontWeight: 500,
          }}
        >
          {hint}
        </span>
      )}
    </div>
  );
}

// ============================================================================
//  Form atoms
// ============================================================================

function Field({
  label,
  children,
  help,
}: {
  label: string;
  children: ReactNode;
  help?: ReactNode;
}) {
  return (
    <label style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
      <span
        style={{
          fontSize: 10,
          letterSpacing: '0.16em',
          textTransform: 'uppercase',
          color: 'var(--fg-2, rgba(255,255,255,0.6))',
          fontWeight: 700,
        }}
      >
        {label}
      </span>
      {children}
      {help && (
        <span
          style={{
            fontSize: 11,
            color: 'var(--fg-3, rgba(255,255,255,0.4))',
            lineHeight: 1.55,
            letterSpacing: 0,
            textTransform: 'none',
          }}
        >
          {help}
        </span>
      )}
    </label>
  );
}

function Input({
  value,
  onChange,
  placeholder,
  type = 'text',
}: {
  value: string;
  onChange: (v: string) => void;
  placeholder?: string;
  type?: 'text' | 'number' | 'password';
}) {
  return (
    <input
      type={type}
      value={value}
      placeholder={placeholder}
      onChange={(e) => onChange(e.target.value)}
      spellCheck={false}
      style={{
        font: 'inherit',
        fontFamily: 'var(--font-mono, ui-monospace, "SF Mono", monospace)',
        fontSize: 13,
        background:
          'linear-gradient(180deg, var(--immersive-well, #0b0b10) 0%, var(--immersive-well-2, #060608) 100%)',
        border: '1px solid var(--immersive-line-2, rgba(255,255,255,0.1))',
        color: 'var(--fg-0, #fff)',
        borderRadius: 6,
        padding: '9px 11px',
        boxShadow: 'inset 0 1px 2px rgba(0,0,0,0.6)',
        outline: 'none',
        minWidth: 0,
      }}
    />
  );
}

function Check({
  checked,
  onChange,
  children,
}: {
  checked: boolean;
  onChange: (v: boolean) => void;
  children: ReactNode;
}) {
  return (
    <label
      style={{
        display: 'inline-flex',
        alignItems: 'center',
        gap: 9,
        cursor: 'pointer',
        userSelect: 'none',
        fontSize: 13,
        color: 'var(--fg-1, #e7e7ea)',
      }}
    >
      <input
        type="checkbox"
        checked={checked}
        onChange={(e) => onChange(e.target.checked)}
        style={{ display: 'none' }}
      />
      <span
        aria-hidden="true"
        style={{
          width: 18,
          height: 18,
          borderRadius: 4,
          background: checked
            ? 'linear-gradient(180deg,#e8eaef,#c4c6cc)'
            : 'linear-gradient(180deg, var(--immersive-well, #0b0b10) 0%, var(--immersive-well-2, #060608) 100%)',
          border: checked
            ? '1.5px solid #dadbe0'
            : '1.5px solid var(--immersive-line-2, rgba(255,255,255,0.1))',
          display: 'grid',
          placeItems: 'center',
          transition: '0.15s',
          boxShadow: checked
            ? '0 0 10px rgba(255,255,255,0.18)'
            : 'inset 0 1px 2px rgba(0,0,0,0.5)',
        }}
      >
        {checked && (
          <span
            style={{
              width: 9,
              height: 5,
              borderLeft: '2px solid #0b0d12',
              borderBottom: '2px solid #0b0d12',
              transform: 'rotate(-45deg) translate(1px,-1px)',
            }}
          />
        )}
      </span>
      <span>{children}</span>
    </label>
  );
}

// ============================================================================
//  Helpers
// ============================================================================

function fmtNum(v: number | null | undefined, digits = 1): string {
  if (v == null || !Number.isFinite(v)) return '—';
  return v.toFixed(digits);
}

function fmtUnitInline(
  r: { value?: number; unit?: string | null } | null | undefined,
  digits: number,
  fallbackUnit: string,
): string {
  if (!r || !Number.isFinite(r.value)) return '—';
  const n = (r.value as number).toFixed(digits);
  return `${n} ${r.unit ?? fallbackUnit}`;
}

function fmtBandUnit(r: { value?: number; unit?: string | null } | null | undefined): string {
  if (!r || !Number.isFinite(r.value)) return '—';
  return `${r.value}${r.unit ?? ''}`;
}

function fmtFreq(r: { value?: number; unit?: string | null } | null | undefined): string {
  if (!r || !Number.isFinite(r.value)) return '—';
  const v = r.value as number;
  if (v >= 1000) return `${(v / 1000).toFixed(3)} MHz`;
  return `${v.toFixed(0)} kHz`;
}

function fmtAntenna(a: { type?: string | null; number?: number | null } | null | undefined): string {
  if (!a) return '—';
  if (a.type === 'EXTERNAL') return 'EXT';
  return `INT-${a.number ?? '?'}`;
}

function fmtAge(iso: string): string {
  const t = Date.parse(iso);
  if (!Number.isFinite(t)) return '—';
  const ageS = Math.max(0, (Date.now() - t) / 1000);
  if (ageS < 2) return 'live';
  if (ageS < 60) return `${Math.round(ageS)} s ago`;
  return `${Math.round(ageS / 60)} m ago`;
}

function formatFw(controller: number | null, gui: number | null): string {
  const c = controller != null && Number.isFinite(controller) ? controller.toFixed(2) : null;
  const g = gui != null && Number.isFinite(gui) ? gui.toFixed(2) : null;
  if (c && g) return `${c}/${g}`;
  return c ?? g ?? '—';
}

function swrTone(swr: number | null | undefined): 'good' | 'warn' | 'bad' | undefined {
  if (swr == null || !Number.isFinite(swr)) return undefined;
  if (swr >= 2.0) return 'bad';
  if (swr >= 1.5) return 'warn';
  return 'good';
}

interface StatusBadge {
  text: string;
  color: string;
  glow?: string;
}

function resolveStatusBadge({
  connected,
  tuning,
  fault,
  operating,
  swr,
}: {
  connected: boolean;
  tuning: boolean;
  fault: string | null;
  operating: boolean;
  swr: number;
}): StatusBadge {
  if (!connected) {
    return { text: 'Offline', color: 'var(--fg-3, rgba(255,255,255,0.4))' };
  }
  if (tuning) {
    return {
      text: 'Tuning',
      color: 'var(--immersive-warn, #f4c168)',
      glow: 'var(--immersive-warn-glow, rgba(244,193,104,0.5))',
    };
  }
  if (fault) {
    return {
      text: fault,
      color: 'var(--immersive-tx, #ef6b54)',
      glow: 'var(--immersive-tx-glow, rgba(239,107,84,0.5))',
    };
  }
  if (Number.isFinite(swr) && swr > 2.5) {
    return {
      text: 'High SWR',
      color: 'var(--immersive-tx, #ef6b54)',
      glow: 'var(--immersive-tx-glow, rgba(239,107,84,0.5))',
    };
  }
  if (operating) {
    return {
      text: 'Transmit',
      color: 'var(--immersive-accent, #4a9eff)',
      glow: 'var(--immersive-accent-glow, rgba(74,158,255,0.5))',
    };
  }
  return {
    text: 'OK',
    color: 'var(--immersive-good, #5cd49a)',
    glow: 'var(--immersive-good-glow, rgba(92,212,154,0.5))',
  };
}

function validPort(p: number): boolean {
  return Number.isFinite(p) && p > 0 && p < 65536;
}

// ============================================================================
//  Main panel
// ============================================================================

function Rf2kPanel({ api }: { api: Rf2kApi }) {
  const [config, setConfig] = useState<Rf2kConfig>(DEFAULT_CONFIG);
  const [status, setStatus] = useState<Rf2kStatus | null>(null);
  const [configLoaded, setConfigLoaded] = useState(false);
  const [lastClickResult, setLastClickResult] = useState<Rf2kTestResult | null>(null);
  const [showSettings, setShowSettings] = useState(false);
  const [tuning, setTuning] = useState(false);

  // Initial config load.
  useEffect(() => {
    let cancelled = false;
    (async () => {
      const cfg = await api.getConfig();
      if (cancelled) return;
      if (cfg) setConfig({ ...DEFAULT_CONFIG, ...cfg });
      setConfigLoaded(true);
      const initialStatus = await api.getStatus();
      if (!cancelled && initialStatus) setStatus(initialStatus);
    })();
    return () => {
      cancelled = true;
    };
  }, [api]);

  // Status poll loop — only when enabled, mirroring the original store.
  useEffect(() => {
    if (!config.enabled) return;
    let cancelled = false;
    const tick = async () => {
      const s = await api.getStatus();
      if (!cancelled && s) setStatus(s);
    };
    const t = setInterval(tick, 1000);
    return () => {
      cancelled = true;
      clearInterval(t);
    };
  }, [api, config.enabled]);

  const connected = !!status?.connected;
  const enabled = config.enabled;
  const isOperate = status?.operateMode === 'OPERATE';
  const isStandby = status?.operateMode === 'STANDBY';

  const fwd = status?.power?.forward;
  const refl = status?.power?.reflected;
  const swrReading = status?.power?.swr;
  const fwdValue = fwd?.value ?? 0;
  const reflValue = refl?.value ?? 0;
  const swrValue = swrReading?.value ?? 1.0;
  const ratedW = Math.max(1, fwd?.maxValue ?? 1500);

  const tuneCfgd = config.tuneClickX > 0 || config.tuneClickY > 0;
  const bypassCfgd = config.bypassClickX > 0 || config.bypassClickY > 0;

  async function saveConfig(next: Rf2kConfig): Promise<Rf2kStatus | null> {
    const s = await api.saveConfig(next);
    setConfig(next);
    if (s) setStatus(s);
    return s;
  }

  async function doSetOperate(mode: 'OPERATE' | 'STANDBY') {
    const s = await api.setOperate(mode);
    if (s) setStatus(s);
  }

  async function doSetAntenna(type: 'INTERNAL' | 'EXTERNAL', number: number | null) {
    const s = await api.setAntenna(type, number);
    if (s) setStatus(s);
  }

  async function doReset() {
    const s = await api.reset();
    if (s) setStatus(s);
  }

  async function doTune() {
    if (!connected || !tuneCfgd || tuning) return;
    setTuning(true);
    try {
      const r = await api.tune();
      setLastClickResult(r);
    } finally {
      setTimeout(() => setTuning(false), 1200);
    }
  }

  async function doBypass() {
    const r = await api.bypass();
    setLastClickResult(r);
  }

  const shellStyle: CSSProperties = {
    display: 'flex',
    flexDirection: 'column',
    height: '100%',
    minHeight: 0,
    background: 'var(--immersive-panel, #0e0e14)',
  };
  const bodyStyle: CSSProperties = {
    padding: 14,
    display: 'flex',
    flexDirection: 'column',
    gap: 12,
    background: 'var(--immersive-panel, #0e0e14)',
    boxSizing: 'border-box',
    flex: 1,
    minHeight: 0,
    overflow: 'auto',
  };

  if (showSettings) {
    return (
      <div style={shellStyle}>
        <div style={bodyStyle} aria-label="RF2K-S amplifier settings">
          <Rf2kSettings
            api={api}
            config={config}
            status={status}
            onSave={saveConfig}
            onClose={() => setShowSettings(false)}
            lastClickResult={lastClickResult}
            setLastClickResult={setLastClickResult}
          />
        </div>
      </div>
    );
  }

  const fwInfo = status?.info;
  const fwString = fwInfo?.softwareVersion
    ? formatFw(fwInfo.softwareVersion.controller, fwInfo.softwareVersion.gui)
    : '—';
  const modelString = fwInfo?.device ?? 'RF2K-S';

  const statusBadge = resolveStatusBadge({
    connected,
    tuning,
    fault: status?.data?.status ?? null,
    operating: isOperate,
    swr: swrValue,
  });

  return (
    <div style={shellStyle}>
      <div style={bodyStyle} aria-label="RF2K-S amplifier — output, status, controls">

        {/* Header strip with gear toggle */}
        <div style={{ display: 'flex', justifyContent: 'flex-end' }}>
          <PanelBtn small tone="ghost" onClick={() => setShowSettings(true)} title="Settings">
            Settings
          </PanelBtn>
        </div>

        {/* ── Hero meters ─────────────────────────────────────────────── */}
        <Section
          title="Output"
          led="on"
          meta={[
            { key: 'MODEL', value: modelString },
            { value: '·' },
            { key: 'FW', value: fwString },
          ]}
        >
          <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 10 }}>
            <div style={{ position: 'relative' }}>
              <BigArc
                mode="watts"
                watts={connected ? fwdValue : 0}
                maxWatts={ratedW}
                label="Forward Power"
                units="Watts"
                defsId="rf2k-arc-fwd"
              />
              <ArcCorner>
                <em style={cornerEmStyle}>refl</em>
                {Math.round(reflValue)} W
              </ArcCorner>
            </div>
            <div style={{ position: 'relative' }}>
              <BigArc
                mode="swr"
                ratio={connected && Number.isFinite(swrValue) ? swrValue : 1.0}
                label="SWR"
                units="Ratio · :1"
                defsId="rf2k-arc-swr"
              />
              <ArcCorner>
                <em style={cornerEmStyle}>limit</em>2.0
              </ArcCorner>
            </div>
          </div>
        </Section>

        {/* ── Status chip strip ───────────────────────────────────────── */}
        <Section padding="12px 14px">
          <div
            style={{
              display: 'grid',
              gridTemplateColumns: 'repeat(4, minmax(0, 1fr))',
              gap: 8,
            }}
          >
            <Chip k="Band" v={fmtBandUnit(status?.data?.band)} />
            <Chip k="Freq" v={fmtFreq(status?.data?.frequency)} />
            <Chip k="Antenna" v={fmtAntenna(status?.activeAntenna)} />
            <Chip k="SWR" v={fmtNum(swrReading?.value, 2)} tone={swrTone(swrReading?.value)} />

            <Chip k="Tuner" v={status?.tuner?.mode ?? 'Off'} dim={!status?.tuner?.mode} />
            <Chip k="Temp" v={fmtUnitInline(status?.power?.temperature, 0, '°C')} />
            <Chip k="Voltage" v={fmtUnitInline(status?.power?.voltage, 1, 'V')} />
            <Chip k="Current" v={fmtUnitInline(status?.power?.current, 1, 'A')} />
          </div>
        </Section>

        {/* ── State row: Operate/Standby + status text + Tune/Bypass ──── */}
        <Section padding="14px">
          <div style={rowSplitStyle}>
            <div style={{ display: 'flex', alignItems: 'center', gap: 14 }}>
              <SegState
                isOperate={isOperate}
                isStandby={isStandby}
                connected={connected}
                onSelect={(v) => void doSetOperate(v === 'operate' ? 'OPERATE' : 'STANDBY')}
              />
              <div style={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
                <span
                  style={{
                    fontSize: 9,
                    letterSpacing: '0.20em',
                    textTransform: 'uppercase',
                    color: 'var(--fg-3, rgba(255,255,255,0.4))',
                    fontWeight: 700,
                  }}
                >
                  Status
                </span>
                <span
                  style={{
                    fontFamily: 'var(--font-mono, ui-monospace, "SF Mono", monospace)',
                    fontSize: 13,
                    fontWeight: 600,
                    color: statusBadge.color,
                    textShadow: statusBadge.glow ? `0 0 8px ${statusBadge.glow}` : undefined,
                  }}
                >
                  {statusBadge.text}
                </span>
              </div>
            </div>
            <BtnGroup>
              <PanelBtn
                tone={tuning ? 'warn-active' : 'warn'}
                disabled={!connected || !tuneCfgd}
                onClick={doTune}
                title={
                  !tuneCfgd
                    ? 'Tune button coordinates not calibrated. Open Settings.'
                    : "Send a VNC mouse-click at the amp's on-screen Tune button."
                }
              >
                {tuning ? 'Tuning…' : 'Tune'}
              </PanelBtn>
              <PanelBtn
                disabled={!connected || !bypassCfgd}
                onClick={doBypass}
                title={
                  !bypassCfgd
                    ? 'Bypass button coordinates not calibrated. Open Settings.'
                    : "Send a VNC mouse-click at the amp's on-screen Bypass button."
                }
              >
                Bypass
              </PanelBtn>
            </BtnGroup>
          </div>
        </Section>

        {/* ── Antenna picker ──────────────────────────────────────────── */}
        {status?.antennas && status.antennas.length > 0 && (
          <Section padding="14px">
            <PickerLabel hint={`${status.antennas.length} ports`}>Antenna</PickerLabel>
            <div style={{ display: 'flex', flexWrap: 'wrap', gap: 5 }}>
              {status.antennas.map((ant, i) => (
                <Pill
                  key={`${ant.type}-${ant.number ?? 'ext'}-${i}`}
                  active={ant.state === 'ACTIVE'}
                  ext={ant.type === 'EXTERNAL'}
                  disabled={!connected || ant.state === 'NOT_AVAILABLE'}
                  onClick={() =>
                    void doSetAntenna(
                      ant.type === 'EXTERNAL' ? 'EXTERNAL' : 'INTERNAL',
                      ant.number ?? null,
                    )
                  }
                >
                  {ant.type === 'EXTERNAL' ? 'EXT' : `INT-${ant.number ?? '?'}`}
                </Pill>
              ))}
            </div>
          </Section>
        )}

        {/* ── Footer ──────────────────────────────────────────────────── */}
        <Foot>
          <FootGrp>
            <FootStat ledTone={!enabled ? 'idle' : connected ? 'good' : 'warn'}>
              {!enabled ? 'Disabled' : connected ? 'Polling' : 'No Link'}
            </FootStat>
            <FootStat>
              <span>Host</span>
              <span style={footValueStyle}>{config.host || '—'}</span>
            </FootStat>
            {status?.lastSampleUtc && (
              <FootStat>
                <span>Sample</span>
                <span style={footValueStyle}>{fmtAge(status.lastSampleUtc)}</span>
              </FootStat>
            )}
          </FootGrp>
          <FootGrp>
            <PanelBtn
              tone="ghost-danger"
              small
              onClick={() => void doReset()}
              disabled={!connected || !status?.data?.status}
            >
              Reset Fault
            </PanelBtn>
          </FootGrp>
        </Foot>

        {lastClickResult && (
          <Toast ok={lastClickResult.ok}>
            {lastClickResult.ok
              ? '✓ Click sent successfully'
              : `✗ ${lastClickResult.error ?? 'Click failed'}`}
          </Toast>
        )}

        {(!enabled || !connected) && configLoaded && (
          <Toast ok={!enabled} tone={enabled ? 'tx' : 'idle'}>
            {!enabled
              ? 'RF2K-S integration disabled. Open Settings to enable polling.'
              : `Not connected: ${status?.error ?? 'awaiting first poll'}`}
          </Toast>
        )}
      </div>
    </div>
  );
}

// ============================================================================
//  Settings drawer
// ============================================================================

function Rf2kSettings({
  api,
  config,
  status,
  onSave,
  onClose,
  lastClickResult,
  setLastClickResult,
}: {
  api: Rf2kApi;
  config: Rf2kConfig;
  status: Rf2kStatus | null;
  onSave: (cfg: Rf2kConfig) => Promise<Rf2kStatus | null>;
  onClose: () => void;
  lastClickResult: Rf2kTestResult | null;
  setLastClickResult: (r: Rf2kTestResult) => void;
}) {
  const [enabled, setEnabled] = useState(config.enabled);
  const [host, setHost] = useState(config.host);
  const [port, setPort] = useState(String(config.port));
  const [vncPort, setVncPort] = useState(String(config.vncPort));
  const [vncPassword, setVncPassword] = useState(config.vncPassword);
  const [tuneX, setTuneX] = useState(String(config.tuneClickX));
  const [tuneY, setTuneY] = useState(String(config.tuneClickY));
  const [bypassX, setBypassX] = useState(String(config.bypassClickX));
  const [bypassY, setBypassY] = useState(String(config.bypassClickY));
  const [calibX, setCalibX] = useState('512');
  const [calibY, setCalibY] = useState('300');
  const [saving, setSaving] = useState(false);
  const [testInFlight, setTestInFlight] = useState(false);
  const [lastTestResult, setLastTestResult] = useState<Rf2kTestResult | null>(null);

  useEffect(() => {
    setEnabled(config.enabled);
    setHost(config.host);
    setPort(String(config.port));
    setVncPort(String(config.vncPort));
    setVncPassword(config.vncPassword);
    setTuneX(String(config.tuneClickX));
    setTuneY(String(config.tuneClickY));
    setBypassX(String(config.bypassClickX));
    setBypassY(String(config.bypassClickY));
  }, [config]);

  async function onSubmit(e: React.FormEvent) {
    e.preventDefault();
    setSaving(true);
    try {
      const portNum = Number(port);
      const vncPortNum = Number(vncPort);
      if (!validPort(portNum) || !validPort(vncPortNum)) return;
      const next: Rf2kConfig = {
        enabled,
        host: host.trim() || '10.70.120.41',
        port: portNum,
        vncPort: vncPortNum,
        vncPassword,
        pollingIntervalMs: config.pollingIntervalMs,
        tuneClickX: Number(tuneX) || 0,
        tuneClickY: Number(tuneY) || 0,
        bypassClickX: Number(bypassX) || 0,
        bypassClickY: Number(bypassY) || 0,
      };
      await onSave(next);
    } finally {
      setSaving(false);
    }
  }

  async function onTestConnection() {
    const portNum = Number(port);
    if (!validPort(portNum)) return;
    setTestInFlight(true);
    setLastTestResult(null);
    try {
      const result = await api.test(host.trim() || '10.70.120.41', portNum);
      setLastTestResult(result);
    } finally {
      setTestInFlight(false);
    }
  }

  async function onCalibClick() {
    const x = Number(calibX);
    const y = Number(calibY);
    if (!Number.isFinite(x) || !Number.isFinite(y)) return;
    const r = await api.click(x, y);
    setLastClickResult(r);
  }

  async function onSetInterface(m: InterfaceMode) {
    await api.setInterface(m);
  }

  return (
    <form onSubmit={onSubmit} style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>

      {/* ── Connection ──────────────────────────────────────────────── */}
      <Section
        title="Connection"
        led="on"
        meta={[{ key: 'REST + VNC', value: '' }]}
        padding="14px"
      >
        <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
          <Check checked={enabled} onChange={setEnabled}>
            Enabled <span style={{ color: 'var(--fg-3, rgba(255,255,255,0.4))' }}>(poll the amp)</span>
          </Check>

          <div style={{ display: 'grid', gridTemplateColumns: '2fr 1fr 1fr', gap: 12 }}>
            <Field label="Host">
              <Input value={host} onChange={setHost} placeholder="10.70.120.41" />
            </Field>
            <Field label="REST Port">
              <Input value={port} onChange={setPort} type="number" />
            </Field>
            <Field label="VNC Port">
              <Input value={vncPort} onChange={setVncPort} type="number" />
            </Field>
          </div>

          <Field
            label="VNC Password"
            help={
              <>
                Required if the amp&apos;s vncserver demands authentication (RFB security
                type 2). Leave blank if VNC is set to allow anonymous connections.
                Truncated to 8 characters per the RFB password protocol; treated as a
                low-value LAN credential, stored unencrypted via the plugin settings
                store.
              </>
            }
          >
            <Input value={vncPassword} onChange={setVncPassword} type="password" />
          </Field>

          <div style={rowSplitStyle}>
            <PanelBtn small onClick={onTestConnection} disabled={testInFlight}>
              {testInFlight ? 'Testing…' : 'Test REST'}
            </PanelBtn>
            <div style={{ display: 'flex', gap: 8 }}>
              <PanelBtn small onClick={onClose} tone="ghost">
                Cancel
              </PanelBtn>
              <button
                type="submit"
                disabled={saving}
                style={{
                  fontFamily: 'inherit',
                  fontSize: 10,
                  fontWeight: 700,
                  letterSpacing: '0.14em',
                  textTransform: 'uppercase',
                  padding: '6px 12px',
                  borderRadius: 6,
                  border: '1px solid #dadbe0',
                  cursor: saving ? 'not-allowed' : 'pointer',
                  color: '#0b0d12',
                  background: 'linear-gradient(180deg,#f1f2f5,#cfd1d6)',
                  boxShadow:
                    'inset 0 1px 0 rgba(255,255,255,0.45), 0 0 14px rgba(255,255,255,0.18)',
                  opacity: saving ? 0.6 : 1,
                }}
              >
                {saving ? 'Saving…' : 'Save'}
              </button>
            </div>
          </div>

          {lastTestResult && (
            <Toast ok={lastTestResult.ok}>
              {lastTestResult.ok
                ? `✓ Reached RF2K-S at ${host}:${port}`
                : `✗ ${lastTestResult.error ?? 'unknown error'}`}
            </Toast>
          )}

          {status?.error && <Toast ok={false}>{status.error}</Toast>}
        </div>
      </Section>

      {/* ── Control Source ──────────────────────────────────────────── */}
      <Section
        title="Control Source"
        led="on"
        meta={[{ key: 'CAT/UDP/TCI link', value: '' }]}
        padding="14px"
      >
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: 5 }}>
          {INTERFACE_MODES.map((m) => {
            const active = status?.operationalInterface === m;
            return (
              <Pill
                key={m}
                active={active}
                onClick={() => void onSetInterface(m)}
                disabled={!status?.connected}
                title={
                  m === 'TCI'
                    ? "Point amp at Zeus's TCI server for auto-band-follow."
                    : `Set amp control source to ${m}.`
                }
              >
                {m}
              </Pill>
            );
          })}
        </div>
        {status?.operationalInterfaceError && (
          <div style={{ marginTop: 8 }}>
            <Toast ok={false}>{status.operationalInterfaceError}</Toast>
          </div>
        )}
      </Section>

      {/* ── VNC Click Calibration ───────────────────────────────────── */}
      <Section
        title="VNC Click Calibration"
        led="warm"
        meta={[{ key: 'PANEL', value: '1024 × 600' }]}
        padding="14px"
      >
        <p
          style={{
            margin: '0 0 14px',
            fontSize: 12,
            color: 'var(--fg-2, rgba(255,255,255,0.6))',
            lineHeight: 1.55,
          }}
        >
          The amp&apos;s REST API doesn&apos;t expose{' '}
          <em style={{ color: 'var(--fg-1, #e7e7ea)', fontStyle: 'normal' }}>Tune</em> or
          tuner-mode toggle. We send a VNC mouse-click at the on-screen button. Use the{' '}
          <em style={{ color: 'var(--fg-1, #e7e7ea)', fontStyle: 'normal' }}>Test Click</em>{' '}
          field to find the right pixel coordinates, then save them as Tune / Bypass.
        </p>
        <div style={{ display: 'grid', gridTemplateColumns: 'repeat(4, 1fr)', gap: 10 }}>
          <Field label="Tune X">
            <Input value={tuneX} onChange={setTuneX} type="number" />
          </Field>
          <Field label="Tune Y">
            <Input value={tuneY} onChange={setTuneY} type="number" />
          </Field>
          <Field label="Bypass X">
            <Input value={bypassX} onChange={setBypassX} type="number" />
          </Field>
          <Field label="Bypass Y">
            <Input value={bypassY} onChange={setBypassY} type="number" />
          </Field>
        </div>
        <div
          style={{
            display: 'grid',
            gridTemplateColumns: '1fr 1fr 200px',
            gap: 10,
            marginTop: 10,
            alignItems: 'end',
          }}
        >
          <Field label="Test X">
            <Input value={calibX} onChange={setCalibX} type="number" />
          </Field>
          <Field label="Test Y">
            <Input value={calibY} onChange={setCalibY} type="number" />
          </Field>
          <PanelBtn onClick={onCalibClick}>Send Test Click</PanelBtn>
        </div>
        {lastClickResult && (
          <div style={{ marginTop: 10 }}>
            <Toast ok={lastClickResult.ok}>
              {lastClickResult.ok
                ? '✓ Click sent — watch the amp screen to confirm it landed on the right button'
                : `✗ ${lastClickResult.error ?? 'click failed'}`}
            </Toast>
          </div>
        )}
      </Section>

      <Foot>
        <FootGrp>
          <FootStat ledTone={status?.connected ? 'good' : 'warn'}>
            {status?.connected ? 'Connected' : 'Offline'}
          </FootStat>
          {status?.connected && (
            <FootStat>
              <span>REST</span>
              <span style={footValueStyle}>200 OK</span>
            </FootStat>
          )}
        </FootGrp>
        <FootGrp>
          <PanelBtn small onClick={onClose}>
            Done
          </PanelBtn>
        </FootGrp>
      </Foot>
    </form>
  );
}

// ============================================================================
//  Module entry — registers the rf2k.main panel with the host.
// ============================================================================

export default function register(api: ZeusPluginApi) {
  const rf2kApi = makeApi(api);
  api.registerPanel({
    id: 'rf2k.main',
    component: (props: object) => <Rf2kPanel api={rf2kApi} {...props} />,
  });
}

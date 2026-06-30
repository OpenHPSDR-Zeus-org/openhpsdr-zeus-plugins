// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see ATTRIBUTIONS.md for provenance.

import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import {
  deleteVoyeurSession,
  getVoyeurSession,
  cancelVoyeurInstall,
  getVoyeurInstallStatus,
  generateVoyeurDigest,
  getVoyeurReport,
  getVoyeurStatus,
  getVoyeurTranscription,
  installVoyeurModel,
  listVoyeurSessions,
  searchVoyeur,
  voyeurSegmentAudioUrl,
  startVoyeur,
  stopVoyeur,
  updateVoyeurSession,
  getVoyeurEngineConfig,
  putVoyeurEngineConfig,
  getVoyeurSegConfig,
  putVoyeurSegConfig,
  getVoyeurAlertConfig,
  putVoyeurAlertConfig,
  testVoyeurAlert,
  getVoyeurCorpus,
  patchVoyeurCorpus,
  type VoyeurInstall,
  type VoyeurReport,
  type VoyeurSearchHit,
  type VoyeurSegment,
  type VoyeurSession,
  type VoyeurSessionDetail,
  type VoyeurStatus,
  type VoyeurEngineConfig,
  type VoyeurSegConfig,
  type VoyeurAlertConfig,
  type VoyeurAlertConfigUpdate,
  type VoyeurAlertTestResult,
  type VoyeurWatchword,
  type VoyeurCorpusStats,
} from './voyeur-api';

// Panel props — the host passes onRemove (was imported from core ../panels).
type PanelComponentProps = { onRemove?: () => void };

// Inline workspace-tile chrome — a self-contained copy of the core TileChrome.
// Uses the host's GLOBAL `.workspace-tile-*` CSS (the plugin renders inside the
// host document) so it matches every other panel 1:1; only the two glyphs are
// inlined as SVG to avoid bundling an icon dependency.
function TileChrome({
  title,
  onRemove,
  rightSlot,
}: {
  title: string;
  onRemove: () => void;
  rightSlot?: ReactNode;
}) {
  return (
    <div className="workspace-tile-header">
      <span className="workspace-tile-drag-handle" aria-hidden="true" title="Drag to reposition">
        <svg width="12" height="12" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
          <circle cx="9" cy="6" r="1.6" /><circle cx="9" cy="12" r="1.6" /><circle cx="9" cy="18" r="1.6" />
          <circle cx="15" cy="6" r="1.6" /><circle cx="15" cy="12" r="1.6" /><circle cx="15" cy="18" r="1.6" />
        </svg>
      </span>
      <span className="workspace-tile-title" title={title}>
        {title}
      </span>
      {rightSlot}
      <button
        type="button"
        className="workspace-tile-close"
        aria-label={`Remove ${title}`}
        title="Remove panel"
        onClick={(e) => { e.stopPropagation(); onRemove(); }}
        onPointerDown={(e) => e.stopPropagation()}
        onMouseDown={(e) => e.stopPropagation()}
      >
        <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" aria-hidden="true">
          <line x1="6" y1="6" x2="18" y2="18" /><line x1="18" y1="6" x2="6" y2="18" />
        </svg>
      </button>
    </div>
  );
}

// Voyeur Mode (zeus-la5) — net-monitor panel. Park the radio on a frequency,
// let the backend capture each transmission ("over") to a log, then review /
// transcribe / summarize / save / delete those logs. The chrome borrows the WAV
// recorder's rack-mount visual language (token-only cards, uppercase section
// headers, dense tables, a per-over transport deck) so it reads as a sibling
// panel; the content (roster, overs, transcript, digest, settings) is Voyeur's.

function fmtFreq(hz: number): string {
  return `${(hz / 1_000_000).toFixed(3)} MHz`;
}
function fmtDur(seconds: number): string {
  const m = Math.floor(seconds / 60);
  const s = Math.round(seconds % 60);
  return m > 0 ? `${m}m ${s}s` : `${s}s`;
}
function fmtWhen(iso: string): string {
  const d = new Date(iso);
  return d.toLocaleString(undefined, {
    month: 'short',
    day: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  });
}
function fmtBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(0)} KB`;
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  return `${(bytes / (1024 * 1024 * 1024)).toFixed(2)} GB`;
}

// Collapsible settings section (recorder-style card with an uppercase header).
function Section({
  title,
  open,
  onToggle,
  children,
}: {
  title: string;
  open: boolean;
  onToggle: () => void;
  children: ReactNode;
}) {
  return (
    <div className={`voyeur-sec${open ? ' is-open' : ''}`}>
      <button type="button" className="voyeur-sec__hdr" aria-expanded={open} onClick={onToggle}>
        <span className="voyeur-sec__chev" aria-hidden="true">{open ? '▾' : '▸'}</span>
        <span className="voyeur-sec__title">{title}</span>
      </button>
      {open && <div className="voyeur-sec__body">{children}</div>}
    </div>
  );
}

// Local, editable mirror of the redacted alert config. Secret fields are kept
// separate (start empty) so the form never echoes a stored secret and only
// transmits one when the operator types a new value.
type AlertDraft = {
  enabled: boolean;
  watchwords: VoyeurWatchword[];
  email: {
    enabled: boolean;
    host: string;
    port: number;
    useSsl: boolean;
    username: string;
    from: string;
    to: string;
    hasPassword: boolean;
    password: string; // typed-only; '' => keep stored
  };
  ntfy: {
    enabled: boolean;
    serverUrl: string;
    topic: string;
    hasToken: boolean;
    token: string; // typed-only; '' => keep stored
  };
  sms: { enabled: boolean; gatewayAddress: string };
  cooldownSeconds: number;
  attachClip: boolean;
  clipMaxSeconds: number;
  zeusBaseUrl: string;
};

function alertDraftFrom(c: VoyeurAlertConfig): AlertDraft {
  return {
    enabled: c.enabled,
    watchwords: c.watchwords.map((w) => ({ ...w })),
    email: { ...c.email, password: '' },
    ntfy: { ...c.ntfy, token: '' },
    sms: { ...c.sms },
    cooldownSeconds: c.cooldownSeconds,
    attachClip: c.attachClip,
    clipMaxSeconds: c.clipMaxSeconds,
    zeusBaseUrl: c.zeusBaseUrl ?? '',
  };
}

export function VoyeurPanel({ onRemove }: PanelComponentProps) {
  const handleRemove = onRemove ?? (() => {});
  const [status, setStatus] = useState<VoyeurStatus | null>(null);
  const [sessions, setSessions] = useState<VoyeurSession[]>([]);
  const [openId, setOpenId] = useState<string | null>(null);
  const [detail, setDetail] = useState<VoyeurSessionDetail | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [asrReady, setAsrReady] = useState<boolean | null>(null);
  const [digestReady, setDigestReady] = useState(false);
  const [digestBusy, setDigestBusy] = useState<string | null>(null);
  const [showHelp, setShowHelp] = useState(false);
  const [showSettings, setShowSettings] = useState(false);
  const [chosenModel, setChosenModel] = useState('medium.en');
  const [install, setInstall] = useState<VoyeurInstall | null>(null);
  const [query, setQuery] = useState('');
  const [hits, setHits] = useState<VoyeurSearchHit[] | null>(null);
  const [reports, setReports] = useState<Record<string, VoyeurReport>>({});
  const [view, setView] = useState<Record<string, 'log' | 'roster'>>({});
  const [playing, setPlaying] = useState<string | null>(null);
  const [playPos, setPlayPos] = useState(0); // 0..1 of the currently-playing over
  const editingRef = useRef<HTMLInputElement | null>(null);
  const audioRef = useRef<HTMLAudioElement | null>(null);

  // ---- settings state ------------------------------------------------------
  const [secOpen, setSecOpen] = useState<Record<string, boolean>>({ engine: true });
  const [engineCfg, setEngineCfg] = useState<VoyeurEngineConfig | null>(null);
  const [segDraft, setSegDraft] = useState<VoyeurSegConfig | null>(null);
  const [segSaved, setSegSaved] = useState<VoyeurSegConfig | null>(null);
  const [alertDraft, setAlertDraft] = useState<AlertDraft | null>(null);
  const [alertDirty, setAlertDirty] = useState(false);
  const [alertSaving, setAlertSaving] = useState(false);
  const [testResults, setTestResults] = useState<Record<string, VoyeurAlertTestResult>>({});
  const [testBusy, setTestBusy] = useState<string | null>(null);
  const [corpus, setCorpus] = useState<VoyeurCorpusStats | null>(null);
  const [corpusMaxDraft, setCorpusMaxDraft] = useState<number | null>(null);

  const toggleSec = (key: string) =>
    setSecOpen((m) => ({ ...m, [key]: !m[key] }));

  const refreshSessions = useCallback(async () => {
    try {
      setSessions(await listVoyeurSessions());
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  }, []);

  // Poll status while mounted (1 Hz — cheap, and shows the live segment count
  // and the safety drop counter climbing during a session).
  useEffect(() => {
    let alive = true;
    const tick = async () => {
      try {
        const s = await getVoyeurStatus();
        if (alive) setStatus(s);
      } catch {
        /* transient */
      }
    };
    void tick();
    const h = setInterval(tick, 1000);
    return () => {
      alive = false;
      clearInterval(h);
    };
  }, []);

  useEffect(() => {
    void refreshSessions();
  }, [refreshSessions]);

  // Stop and release the shared <audio> on unmount so a playing over doesn't
  // keep going after the panel closes.
  useEffect(
    () => () => {
      const el = audioRef.current;
      if (el) {
        el.pause();
        el.src = '';
      }
    },
    [],
  );

  const refreshAsr = useCallback(async () => {
    try {
      const t = await getVoyeurTranscription();
      setAsrReady(t.available);
      setDigestReady(t.digestAvailable);
    } catch {
      /* ignore */
    }
  }, []);

  useEffect(() => {
    void refreshAsr();
    void getVoyeurInstallStatus().then(setInstall).catch(() => {});
  }, [refreshAsr]);

  // Lazily load settings the first time the settings drawer opens (and refresh
  // each time it's reopened so external changes show).
  useEffect(() => {
    if (!showSettings) return;
    void getVoyeurEngineConfig().then(setEngineCfg).catch(() => {});
    void getVoyeurSegConfig()
      .then((s) => {
        setSegDraft(s);
        setSegSaved(s);
      })
      .catch(() => {});
    void getVoyeurAlertConfig()
      .then((c) => {
        setAlertDraft(alertDraftFrom(c));
        setAlertDirty(false);
      })
      .catch(() => {});
    void getVoyeurCorpus()
      .then((c) => {
        setCorpus(c);
        setCorpusMaxDraft(c.maxClips);
      })
      .catch(() => {});
  }, [showSettings]);

  // Poll install progress while a download is running; refresh ASR readiness
  // when it finishes (discovery is dynamic, so no restart needed).
  useEffect(() => {
    if (install?.phase !== 'Downloading') return;
    const h = setInterval(async () => {
      try {
        const s = await getVoyeurInstallStatus();
        setInstall(s);
        if (s.phase === 'Done') void refreshAsr();
      } catch {
        /* ignore */
      }
    }, 1000);
    return () => clearInterval(h);
  }, [install?.phase, refreshAsr]);

  const onInstall = async (id?: string) => {
    try {
      setInstall(await installVoyeurModel(id ?? chosenModel));
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  };
  const onCancelInstall = async () => {
    try {
      setInstall(await cancelVoyeurInstall());
    } catch {
      /* ignore */
    }
  };

  // When a session is active, refresh the list as its segment count grows.
  useEffect(() => {
    if (!status?.active) return;
    const h = setInterval(() => void refreshSessions(), 3000);
    return () => clearInterval(h);
  }, [status?.active, refreshSessions]);

  const onStart = async () => {
    setBusy(true);
    setError(null);
    try {
      setStatus(await startVoyeur(true));
      await refreshSessions();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  const onStop = async () => {
    setBusy(true);
    setError(null);
    try {
      setStatus(await stopVoyeur());
      await refreshSessions();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  const openSession = async (id: string) => {
    if (openId === id) {
      setOpenId(null);
      setDetail(null);
      return;
    }
    setOpenId(id);
    setDetail(null);
    // Load the report too so the AI Summary bar (with any existing digest) is
    // available in the Log view, not just behind the Roster toggle.
    if (!reports[id]) void loadReport(id);
    try {
      setDetail(await getVoyeurSession(id));
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  };

  const onTogglePin = async (s: VoyeurSession) => {
    try {
      await updateVoyeurSession(s.id, { pinned: !s.pinned });
      await refreshSessions();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  };

  const onRename = async (s: VoyeurSession, label: string) => {
    const trimmed = label.trim();
    if (!trimmed || trimmed === s.label) return;
    try {
      await updateVoyeurSession(s.id, { label: trimmed });
      await refreshSessions();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  };

  const onDelete = async (s: VoyeurSession) => {
    if (!window.confirm(`Delete log "${s.label}"? This removes its captured audio too.`)) return;
    try {
      await deleteVoyeurSession(s.id);
      if (openId === s.id) {
        setOpenId(null);
        setDetail(null);
      }
      await refreshSessions();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  };

  // Search across all logs (debounced). Empty query → normal session list.
  useEffect(() => {
    const q = query.trim();
    if (!q) {
      setHits(null);
      return;
    }
    const ctrl = new AbortController();
    const h = setTimeout(() => {
      void searchVoyeur(q, ctrl.signal)
        .then(setHits)
        .catch(() => {});
    }, 250);
    return () => {
      clearTimeout(h);
      ctrl.abort();
    };
  }, [query]);

  const loadReport = useCallback(async (id: string) => {
    try {
      const r = await getVoyeurReport(id);
      setReports((m) => ({ ...m, [id]: r }));
    } catch {
      /* ignore */
    }
  }, []);

  const setSessionView = (id: string, mode: 'log' | 'roster') => {
    setView((v) => ({ ...v, [id]: mode }));
    if (mode === 'roster' && !reports[id]) void loadReport(id);
  };

  const onGenerateDigest = async (id: string) => {
    setDigestBusy(id);
    setError(null);
    try {
      const r = await generateVoyeurDigest(id);
      setReports((m) => ({ ...m, [id]: r }));
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setDigestBusy(null);
    }
  };

  // ---- per-over transport (play / pause / scrub the over's clip) -----------
  const ensureAudio = () => {
    let el = audioRef.current;
    if (!el) {
      el = new Audio();
      el.onended = () => {
        setPlaying(null);
        setPlayPos(0);
      };
      el.ontimeupdate = () => {
        const a = audioRef.current;
        if (a && a.duration > 0 && Number.isFinite(a.duration)) {
          setPlayPos(a.currentTime / a.duration);
        }
      };
      audioRef.current = el;
    }
    return el;
  };

  const playSegment = (segId: string) => {
    const el = ensureAudio();
    if (playing === segId) {
      el.pause();
      setPlaying(null);
      return;
    }
    el.src = voyeurSegmentAudioUrl(segId);
    setPlayPos(0);
    void el
      .play()
      .then(() => setPlaying(segId))
      .catch(() => setPlaying(null));
  };

  const seekSegment = (segId: string, frac: number) => {
    const el = audioRef.current;
    if (!el || playing !== segId || !(el.duration > 0)) return;
    const f = Math.max(0, Math.min(1, frac));
    el.currentTime = f * el.duration;
    setPlayPos(f);
  };

  // ---- settings handlers ---------------------------------------------------
  const saveEngine = async (patch: { engine?: string; provider?: string; gpuEnabled?: boolean }) => {
    try {
      setEngineCfg(await putVoyeurEngineConfig(patch));
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  };

  const saveSeg = async () => {
    if (!segDraft) return;
    try {
      const s = await putVoyeurSegConfig(segDraft);
      setSegDraft(s);
      setSegSaved(s);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  };

  const setSeg = <K extends keyof VoyeurSegConfig>(key: K, val: VoyeurSegConfig[K]) =>
    setSegDraft((d) => (d ? { ...d, [key]: val } : d));

  const segDirty = useMemo(
    () => JSON.stringify(segDraft) !== JSON.stringify(segSaved),
    [segDraft, segSaved],
  );

  const editAlert = (mut: (d: AlertDraft) => AlertDraft) => {
    setAlertDraft((d) => (d ? mut(d) : d));
    setAlertDirty(true);
  };

  const saveAlerts = async () => {
    if (!alertDraft) return;
    setAlertSaving(true);
    setError(null);
    try {
      const update: VoyeurAlertConfigUpdate = {
        enabled: alertDraft.enabled,
        watchwords: alertDraft.watchwords
          .map((w) => ({ ...w, text: w.text.trim() }))
          .filter((w) => w.text.length > 0),
        email: {
          enabled: alertDraft.email.enabled,
          host: alertDraft.email.host,
          port: alertDraft.email.port,
          useSsl: alertDraft.email.useSsl,
          username: alertDraft.email.username,
          from: alertDraft.email.from,
          to: alertDraft.email.to,
          // Secret: only sent when the operator typed something.
          ...(alertDraft.email.password ? { password: alertDraft.email.password } : {}),
        },
        ntfy: {
          enabled: alertDraft.ntfy.enabled,
          serverUrl: alertDraft.ntfy.serverUrl,
          topic: alertDraft.ntfy.topic,
          ...(alertDraft.ntfy.token ? { token: alertDraft.ntfy.token } : {}),
        },
        sms: { enabled: alertDraft.sms.enabled, gatewayAddress: alertDraft.sms.gatewayAddress },
        cooldownSeconds: alertDraft.cooldownSeconds,
        attachClip: alertDraft.attachClip,
        clipMaxSeconds: alertDraft.clipMaxSeconds,
        zeusBaseUrl: alertDraft.zeusBaseUrl.trim() ? alertDraft.zeusBaseUrl.trim() : null,
      };
      const res = await putVoyeurAlertConfig(update);
      setAlertDraft(alertDraftFrom(res)); // re-redacted; clears typed secrets
      setAlertDirty(false);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setAlertSaving(false);
    }
  };

  const onTestChannel = async (channel: string) => {
    setTestBusy(channel);
    try {
      const results = await testVoyeurAlert(channel);
      setTestResults((m) => {
        const next = { ...m };
        for (const r of results) next[r.channel] = r;
        return next;
      });
    } catch (e) {
      setTestResults((m) => ({
        ...m,
        [channel]: { channel, ok: false, detail: e instanceof Error ? e.message : String(e) },
      }));
    } finally {
      setTestBusy(null);
    }
  };

  const saveCorpus = async (patch: { retainCorpus?: boolean; maxClips?: number }) => {
    try {
      const c = await patchVoyeurCorpus(patch);
      setCorpus(c);
      setCorpusMaxDraft(c.maxClips);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  };

  // ---- renderers -----------------------------------------------------------

  // A single "over" — a dense table row plus an inline transport deck (play +
  // scrub) that mirrors the WAV recorder's transport feel.
  const renderOver = (seg: VoyeurSegment) => {
    const state = seg.callsignState ?? 'unknown';
    const isPlaying = playing === seg.id;
    const pos = isPlaying ? playPos : 0;
    return (
      <div key={seg.id} className={`voyeur-over voyeur-over--${state}`}>
        <span className="voyeur-over__time">
          {new Date(seg.startedUtc).toLocaleTimeString([], {
            hour: '2-digit',
            minute: '2-digit',
            second: '2-digit',
          })}
        </span>
        <span className="voyeur-over__dur">{(seg.durationMs / 1000).toFixed(0)}s</span>
        <span className="voyeur-over__station">
          <span className={`voyeur-call voyeur-call--${state}`}>{seg.callsign ?? 'unknown'}</span>
          {seg.callsignName && <span className="voyeur-name">{seg.callsignName}</span>}
        </span>
        <span className="voyeur-over__text">
          {seg.transcript ? (
            <span className="voyeur-text">{seg.transcript}</span>
          ) : (
            <span className="voyeur-text voyeur-text--pending">
              {asrReady ? 'transcribing…' : 'audio captured'}
            </span>
          )}
          {seg.hasAudio && (
            <span className="voyeur-deck">
              <button
                type="button"
                className={`voyeur-deck__btn${isPlaying ? ' is-on' : ''}`}
                onClick={() => playSegment(seg.id)}
                title={isPlaying ? 'Pause' : 'Play this over'}
                aria-label={isPlaying ? 'Pause this over' : 'Play this over'}
              >
                {isPlaying ? '❚❚' : '▶'}
              </button>
              <span
                className="voyeur-deck__track"
                role="slider"
                aria-label="Scrub over"
                aria-valuemin={0}
                aria-valuemax={100}
                aria-valuenow={Math.round(pos * 100)}
                tabIndex={isPlaying ? 0 : -1}
                onClick={(e) => {
                  const r = e.currentTarget.getBoundingClientRect();
                  if (r.width > 0) seekSegment(seg.id, (e.clientX - r.left) / r.width);
                }}
                onKeyDown={(e) => {
                  if (e.key === 'ArrowLeft') seekSegment(seg.id, Math.max(0, pos - 0.05));
                  if (e.key === 'ArrowRight') seekSegment(seg.id, Math.min(1, pos + 0.05));
                }}
              >
                <span className="voyeur-deck__fill" style={{ width: `${pos * 100}%` }} />
              </span>
            </span>
          )}
        </span>
      </div>
    );
  };

  // Overs table (sticky header) — wraps the rows for one log / search hit.
  const renderOvers = (segs: VoyeurSegment[], empty: string) => (
    <div className="voyeur-overtable">
      <div className="voyeur-overtable__head">
        <span>Time</span>
        <span>Dur</span>
        <span>Station</span>
        <span>Transcript</span>
      </div>
      <div className="voyeur-overtable__body">
        {segs.length === 0 ? (
          <div className="voyeur-empty" style={{ padding: '6px 10px' }}>{empty}</div>
        ) : (
          segs.map(renderOver)
        )}
      </div>
    </div>
  );

  // AI Summary bar — surfaced whenever a log is open (both Log and Roster
  // views), so it's never hidden behind the Roster toggle.
  const renderSummary = (id: string) => {
    const digest = reports[id]?.digest;
    return (
      <div className="voyeur-summary">
        <div className="voyeur-digestbar">
          <div className="voyeur-digestbar__text">
            <span className="voyeur-digestbar__label">AI Summary</span>
            <span className="voyeur-digestbar__sub">
              Plain-English recap of who ran the net and what was discussed —
              written locally on your machine from this log’s transcript.
            </span>
          </div>
          {digestReady ? (
            <button
              type="button"
              className="btn sm active"
              disabled={digestBusy === id}
              onClick={() => onGenerateDigest(id)}
              title="Summarize this net’s transcript into a short recap (runs locally, nothing leaves your machine)"
            >
              {digestBusy === id
                ? 'Summarizing…'
                : digest
                  ? 'Regenerate summary'
                  : 'Summarize this net'}
            </button>
          ) : (
            <span className="voyeur-digestbar__hint">
              install the digest model in “How to set up & use” to enable
            </span>
          )}
        </div>
        {digest && <div className="voyeur-digest">{digest}</div>}
      </div>
    );
  };

  const renderRoster = (id: string) => {
    const r = reports[id];
    if (!r) return <div className="voyeur-empty" style={{ padding: '6px 10px' }}>Loading…</div>;
    return (
      <div className="voyeur-roster">
        <div className="voyeur-roster__stats">
          <span className="chip mono"><span className="k">stations</span><span className="v">{r.uniqueStations}</span></span>
          <span className="chip mono"><span className="k">confirmed</span><span className="v">{r.confirmedStations}</span></span>
          <span className="chip mono"><span className="k">overs</span><span className="v">{r.session.segmentCount}</span></span>
          <span className="chip mono"><span className="k">cap</span><span className="v">{fmtDur(r.session.capturedSeconds)}</span></span>
        </div>
        <div className="voyeur-rostertable">
          <div className="voyeur-rostertable__head">
            <span>Station</span>
            <span>Name</span>
            <span className="voyeur-rostertable__num">Overs</span>
          </div>
          <div className="voyeur-rostertable__body">
            {r.roster.length === 0 ? (
              <div className="voyeur-empty" style={{ padding: '4px 10px' }}>No callsigns identified.</div>
            ) : (
              r.roster.map((e) => (
                <div key={e.callsign} className="voyeur-rosteritem">
                  <span className={`voyeur-call voyeur-call--${e.state}`}>{e.callsign}</span>
                  <span className="voyeur-name">{e.name ?? '—'}</span>
                  <span className="voyeur-rosteritem__count">{e.overCount}</span>
                </div>
              ))
            )}
          </div>
        </div>
      </div>
    );
  };

  const legend = (
    <div className="voyeur-legend" aria-label="Roster colour legend">
      <span className="voyeur-legend__item">
        <span className="voyeur-swatch voyeur-swatch--confirmed" aria-hidden="true" />
        Confirmed — QRZ-verified licensee
      </span>
      <span className="voyeur-legend__item">
        <span className="voyeur-swatch voyeur-swatch--tentative" aria-hidden="true" />
        Tentative — heard, unverified
      </span>
      <span className="voyeur-legend__item">
        <span className="voyeur-swatch voyeur-swatch--unknown" aria-hidden="true" />
        Unknown — no decodable callsign
      </span>
    </div>
  );

  // ---- settings drawer -----------------------------------------------------
  const engineIs = (kind: string) =>
    (engineCfg?.engine ?? 'Whisper').toLowerCase() === kind;

  const renderSettings = () => (
    <div className="voyeur-settings">
      {/* Speech engine */}
      <Section title="Speech engine" open={!!secOpen.engine} onToggle={() => toggleSec('engine')}>
        {!engineCfg ? (
          <div className="voyeur-empty">Loading…</div>
        ) : (
          <>
            <div className="voyeur-field">
              <span className="voyeur-field__label">Engine</span>
              <div className="voyeur-segctl" role="group" aria-label="Speech engine">
                <button
                  type="button"
                  className={`btn sm${engineIs('whisper') ? ' active' : ''}`}
                  onClick={() => saveEngine({ engine: 'whisper' })}
                >
                  Whisper
                </button>
                <button
                  type="button"
                  className={`btn sm${engineIs('parakeet') ? ' active' : ''}`}
                  onClick={() => saveEngine({ engine: 'parakeet' })}
                >
                  Parakeet
                </button>
              </div>
            </div>
            <div className="voyeur-field__hint">
              {engineIs('parakeet') && !engineCfg.available.parakeet
                ? 'Parakeet isn’t installed yet — it downloads on first use.'
                : engineIs('whisper')
                  ? 'Whisper is the proven default (recommended).'
                  : 'Parakeet is ready.'}
            </div>
            <label className="voyeur-check">
              <input
                type="checkbox"
                checked={engineCfg.gpuEnabled}
                onChange={(e) => saveEngine({ gpuEnabled: e.target.checked })}
              />
              Use GPU when available (Parakeet only; CPU is the floor)
            </label>
            <div className="voyeur-field__hint">
              Active provider: <code>{engineCfg.resolvedProvider}</code>
            </div>
          </>
        )}
      </Section>

      {/* Segmentation */}
      <Section title="Segmentation" open={!!secOpen.seg} onToggle={() => toggleSec('seg')}>
        {!segDraft ? (
          <div className="voyeur-empty">Loading…</div>
        ) : (
          <>
            <label className="voyeur-check">
              <input
                type="checkbox"
                checked={segDraft.useVad}
                onChange={(e) => setSeg('useVad', e.target.checked)}
              />
              Use Silero VAD (experimental)
            </label>
            <div className="voyeur-field">
              <span className="voyeur-field__label">Open margin (dB)</span>
              <input
                type="number"
                step={0.5}
                value={segDraft.openMarginDb}
                onChange={(e) => setSeg('openMarginDb', Number(e.target.value))}
              />
            </div>
            <div className="voyeur-field">
              <span className="voyeur-field__label">Hang (seconds)</span>
              <input
                type="number"
                step={0.1}
                value={segDraft.hangSeconds}
                onChange={(e) => setSeg('hangSeconds', Number(e.target.value))}
              />
            </div>
            <div className="voyeur-field">
              <span className="voyeur-field__label">Pre-roll (ms)</span>
              <input
                type="number"
                step={50}
                value={segDraft.preRollMs}
                onChange={(e) => setSeg('preRollMs', Number(e.target.value))}
              />
            </div>
            <div className="voyeur-field">
              <span className="voyeur-field__label">Max over (seconds)</span>
              <input
                type="number"
                step={10}
                value={segDraft.maxOverSeconds}
                onChange={(e) => setSeg('maxOverSeconds', Number(e.target.value))}
              />
            </div>
            <div className="voyeur-actions">
              <button type="button" className="btn sm active" disabled={!segDirty} onClick={saveSeg}>
                {segDirty ? 'Apply' : 'Saved'}
              </button>
            </div>
          </>
        )}
      </Section>

      {/* Watchword alerts */}
      <Section title="Watchword alerts" open={!!secOpen.alerts} onToggle={() => toggleSec('alerts')}>
        {!alertDraft ? (
          <div className="voyeur-empty">Loading…</div>
        ) : (
          <>
            <label className="voyeur-check">
              <input
                type="checkbox"
                checked={alertDraft.enabled}
                onChange={(e) => editAlert((d) => ({ ...d, enabled: e.target.checked }))}
              />
              Enable watchword alerts
            </label>

            {/* Watchword list */}
            <div className="voyeur-field__label" style={{ marginTop: 6 }}>Watchwords</div>
            {alertDraft.watchwords.length === 0 && (
              <div className="voyeur-empty" style={{ padding: '2px 0' }}>
                None yet — add your call or a keyword.
              </div>
            )}
            {alertDraft.watchwords.map((w, i) => (
              <div key={i} className="voyeur-wword">
                <input
                  type="checkbox"
                  checked={w.enabled}
                  aria-label="Watchword enabled"
                  onChange={(e) =>
                    editAlert((d) => {
                      const ws = d.watchwords.slice();
                      ws[i] = { ...ws[i]!, enabled: e.target.checked };
                      return { ...d, watchwords: ws };
                    })
                  }
                />
                <input
                  type="text"
                  className="voyeur-wword__text"
                  value={w.text}
                  placeholder="callsign or keyword"
                  onChange={(e) =>
                    editAlert((d) => {
                      const ws = d.watchwords.slice();
                      ws[i] = { ...ws[i]!, text: e.target.value };
                      return { ...d, watchwords: ws };
                    })
                  }
                />
                <select
                  value={w.type.toLowerCase() === 'keyword' ? 'Keyword' : 'Callsign'}
                  aria-label="Watchword type"
                  onChange={(e) =>
                    editAlert((d) => {
                      const ws = d.watchwords.slice();
                      ws[i] = { ...ws[i]!, type: e.target.value };
                      return { ...d, watchwords: ws };
                    })
                  }
                >
                  <option value="Callsign">callsign</option>
                  <option value="Keyword">keyword</option>
                </select>
                <button
                  type="button"
                  className="btn sm tx"
                  aria-label="Remove watchword"
                  onClick={() =>
                    editAlert((d) => ({
                      ...d,
                      watchwords: d.watchwords.filter((_, j) => j !== i),
                    }))
                  }
                >
                  ✕
                </button>
              </div>
            ))}
            <div className="voyeur-actions">
              <button
                type="button"
                className="btn sm"
                onClick={() =>
                  editAlert((d) => ({
                    ...d,
                    watchwords: [...d.watchwords, { text: '', type: 'Callsign', enabled: true }],
                  }))
                }
              >
                + Add watchword
              </button>
            </div>

            {/* Email channel */}
            <div className="voyeur-chan">
              <label className="voyeur-check">
                <input
                  type="checkbox"
                  checked={alertDraft.email.enabled}
                  onChange={(e) =>
                    editAlert((d) => ({ ...d, email: { ...d.email, enabled: e.target.checked } }))
                  }
                />
                Email (your own SMTP)
                <button
                  type="button"
                  className="btn xs"
                  style={{ marginLeft: 'auto' }}
                  disabled={testBusy === 'email'}
                  onClick={() => onTestChannel('email')}
                >
                  {testBusy === 'email' ? 'Testing…' : 'Send test'}
                </button>
              </label>
              {testResults.email && (
                <div className={`voyeur-testres ${testResults.email.ok ? 'is-ok' : 'is-err'}`}>
                  {testResults.email.ok ? '✓ ' : '✕ '}{testResults.email.detail}
                </div>
              )}
              <div className="voyeur-grid2">
                <input
                  type="text"
                  placeholder="SMTP host"
                  value={alertDraft.email.host}
                  onChange={(e) =>
                    editAlert((d) => ({ ...d, email: { ...d.email, host: e.target.value } }))
                  }
                />
                <input
                  type="number"
                  placeholder="Port"
                  value={alertDraft.email.port}
                  onChange={(e) =>
                    editAlert((d) => ({ ...d, email: { ...d.email, port: Number(e.target.value) } }))
                  }
                />
                <input
                  type="text"
                  placeholder="Username"
                  value={alertDraft.email.username}
                  onChange={(e) =>
                    editAlert((d) => ({ ...d, email: { ...d.email, username: e.target.value } }))
                  }
                />
                <input
                  type="password"
                  placeholder={alertDraft.email.hasPassword ? 'leave blank to keep current' : 'Password'}
                  value={alertDraft.email.password}
                  onChange={(e) =>
                    editAlert((d) => ({ ...d, email: { ...d.email, password: e.target.value } }))
                  }
                />
                <input
                  type="text"
                  placeholder="From address"
                  value={alertDraft.email.from}
                  onChange={(e) =>
                    editAlert((d) => ({ ...d, email: { ...d.email, from: e.target.value } }))
                  }
                />
                <input
                  type="text"
                  placeholder="To address"
                  value={alertDraft.email.to}
                  onChange={(e) =>
                    editAlert((d) => ({ ...d, email: { ...d.email, to: e.target.value } }))
                  }
                />
              </div>
              <label className="voyeur-check">
                <input
                  type="checkbox"
                  checked={alertDraft.email.useSsl}
                  onChange={(e) =>
                    editAlert((d) => ({ ...d, email: { ...d.email, useSsl: e.target.checked } }))
                  }
                />
                Use TLS/SSL
              </label>
            </div>

            {/* ntfy channel */}
            <div className="voyeur-chan">
              <label className="voyeur-check">
                <input
                  type="checkbox"
                  checked={alertDraft.ntfy.enabled}
                  onChange={(e) =>
                    editAlert((d) => ({ ...d, ntfy: { ...d.ntfy, enabled: e.target.checked } }))
                  }
                />
                ntfy push
                <button
                  type="button"
                  className="btn xs"
                  style={{ marginLeft: 'auto' }}
                  disabled={testBusy === 'ntfy'}
                  onClick={() => onTestChannel('ntfy')}
                >
                  {testBusy === 'ntfy' ? 'Testing…' : 'Send test'}
                </button>
              </label>
              {testResults.ntfy && (
                <div className={`voyeur-testres ${testResults.ntfy.ok ? 'is-ok' : 'is-err'}`}>
                  {testResults.ntfy.ok ? '✓ ' : '✕ '}{testResults.ntfy.detail}
                </div>
              )}
              <div className="voyeur-grid2">
                <input
                  type="text"
                  placeholder="Server URL (https://ntfy.sh)"
                  value={alertDraft.ntfy.serverUrl}
                  onChange={(e) =>
                    editAlert((d) => ({ ...d, ntfy: { ...d.ntfy, serverUrl: e.target.value } }))
                  }
                />
                <input
                  type="text"
                  placeholder="Topic"
                  value={alertDraft.ntfy.topic}
                  onChange={(e) =>
                    editAlert((d) => ({ ...d, ntfy: { ...d.ntfy, topic: e.target.value } }))
                  }
                />
                <input
                  type="password"
                  placeholder={alertDraft.ntfy.hasToken ? 'token — leave blank to keep current' : 'token (optional)'}
                  value={alertDraft.ntfy.token}
                  onChange={(e) =>
                    editAlert((d) => ({ ...d, ntfy: { ...d.ntfy, token: e.target.value } }))
                  }
                />
              </div>
            </div>

            {/* SMS gateway channel */}
            <div className="voyeur-chan">
              <label className="voyeur-check">
                <input
                  type="checkbox"
                  checked={alertDraft.sms.enabled}
                  onChange={(e) =>
                    editAlert((d) => ({ ...d, sms: { ...d.sms, enabled: e.target.checked } }))
                  }
                />
                SMS via carrier email gateway
                <button
                  type="button"
                  className="btn xs"
                  style={{ marginLeft: 'auto' }}
                  disabled={testBusy === 'sms'}
                  onClick={() => onTestChannel('sms')}
                >
                  {testBusy === 'sms' ? 'Testing…' : 'Send test'}
                </button>
              </label>
              {testResults.sms && (
                <div className={`voyeur-testres ${testResults.sms.ok ? 'is-ok' : 'is-err'}`}>
                  {testResults.sms.ok ? '✓ ' : '✕ '}{testResults.sms.detail}
                </div>
              )}
              <div className="voyeur-grid2">
                <input
                  type="text"
                  placeholder="5551234567@vtext.com"
                  value={alertDraft.sms.gatewayAddress}
                  onChange={(e) =>
                    editAlert((d) => ({ ...d, sms: { ...d.sms, gatewayAddress: e.target.value } }))
                  }
                />
              </div>
              <div className="voyeur-field__hint">SMS reuses the Email SMTP relay above for transport.</div>
            </div>

            {/* Cooldown / deep link / clip attach */}
            <div className="voyeur-field">
              <span className="voyeur-field__label">Cooldown (seconds)</span>
              <input
                type="number"
                step={30}
                value={alertDraft.cooldownSeconds}
                onChange={(e) =>
                  editAlert((d) => ({ ...d, cooldownSeconds: Number(e.target.value) }))
                }
              />
            </div>
            <div className="voyeur-field">
              <span className="voyeur-field__label">Zeus base URL (deep links)</span>
              <input
                type="text"
                placeholder="http://your-zeus:6060"
                value={alertDraft.zeusBaseUrl}
                onChange={(e) => editAlert((d) => ({ ...d, zeusBaseUrl: e.target.value }))}
              />
            </div>
            <label className="voyeur-check">
              <input
                type="checkbox"
                checked={alertDraft.attachClip}
                onChange={(e) => editAlert((d) => ({ ...d, attachClip: e.target.checked }))}
              />
              Attach the over’s audio clip to alerts
            </label>
            {alertDraft.attachClip && (
              <div className="voyeur-warn">
                ⚠ With clip attachments on, the matched recording <strong>leaves this
                computer</strong> — it is emailed / pushed to the address you set above.
                Transcription itself still runs locally; only alert clips are sent off the
                machine, and only when you enable this.
              </div>
            )}
            <div className="voyeur-actions">
              <button
                type="button"
                className="btn sm active"
                disabled={!alertDirty || alertSaving}
                onClick={saveAlerts}
              >
                {alertSaving ? 'Saving…' : alertDirty ? 'Save alerts' : 'Saved'}
              </button>
            </div>
          </>
        )}
      </Section>

      {/* Training corpus */}
      <Section title="Training corpus" open={!!secOpen.corpus} onToggle={() => toggleSec('corpus')}>
        {!corpus ? (
          <div className="voyeur-empty">Loading…</div>
        ) : (
          <>
            <label className="voyeur-check">
              <input
                type="checkbox"
                checked={corpus.retainCorpus}
                onChange={(e) => void saveCorpus({ retainCorpus: e.target.checked })}
              />
              Keep confirmed overs to build a ham-ASR training corpus (opt-in)
            </label>
            <div className="voyeur-roster__stats" style={{ marginTop: 6 }}>
              <span className="chip mono"><span className="k">clips</span><span className="v">{corpus.clipCount}</span></span>
              <span className="chip mono"><span className="k">size</span><span className="v">{fmtBytes(corpus.bytes)}</span></span>
              <span className="chip mono"><span className="k">cap</span><span className="v">{corpus.maxClips}</span></span>
            </div>
            <div className="voyeur-field" style={{ marginTop: 6 }}>
              <span className="voyeur-field__label">Max clips</span>
              <input
                type="number"
                step={500}
                value={corpusMaxDraft ?? corpus.maxClips}
                onChange={(e) => setCorpusMaxDraft(Number(e.target.value))}
              />
              <button
                type="button"
                className="btn sm"
                disabled={corpusMaxDraft === null || corpusMaxDraft === corpus.maxClips}
                onClick={() => void saveCorpus({ maxClips: corpusMaxDraft ?? corpus.maxClips })}
              >
                Apply
              </button>
            </div>
            <div className="voyeur-field__hint">
              Stored locally at <code>{corpus.path}</code>. Oldest clips are pruned past the cap.
            </div>
          </>
        )}
      </Section>
    </div>
  );

  const active = status?.active ?? false;

  return (
    <>
      <TileChrome
        title="Voyeur Mode"
        onRemove={handleRemove}
        rightSlot={
          <button
            type="button"
            className={`btn ${active ? 'tx' : 'active'}`}
            disabled={busy}
            onClick={active ? onStop : onStart}
            aria-label={active ? 'Stop monitoring' : 'Start monitoring'}
          >
            {active ? 'STOP' : 'LISTEN'}
          </button>
        }
      />
      <div className="voyeur">
        <div className="voyeur__controls">
          {/* Live receiver bar */}
          <div className={`voyeur-live ${active ? 'voyeur-live--on' : ''}`}>
            {active && status ? (
              <>
                <span className="voyeur-rec">
                  <span className="voyeur-rec__dot" />
                  Listening
                </span>
                <span className="voyeur-freq">{fmtFreq(status.freqHz)}</span>
                <span className="voyeur-mode">
                  {status.mode} · {status.band}
                </span>
                <span className="status-chips" style={{ marginLeft: 'auto' }}>
                  <span className="chip mono">
                    <span className="k">overs</span>
                    <span className="v">{status.segmentCount}</span>
                  </span>
                  <span className="chip mono">
                    <span className="k">cap</span>
                    <span className="v">{fmtDur(status.capturedSeconds)}</span>
                  </span>
                  {status.droppedSamples > 0 && (
                    <span
                      className="chip mono"
                      title="Samples dropped because the CPU briefly fell behind. RX is unaffected — just a capture gap."
                    >
                      <span className="k">drop</span>
                      <span className="v">{status.droppedSamples}</span>
                    </span>
                  )}
                  {status.degraded && (
                    <span className="chip tx" title="The monitor faulted and detached. RX is unaffected.">
                      <span className="v">degraded</span>
                    </span>
                  )}
                </span>
              </>
            ) : (
              <span className="voyeur-idle">
                Idle — tune to a busy frequency and press <strong>LISTEN</strong> to
                log who’s on and what’s said.
              </span>
            )}
          </div>

          {/* Transcription status + setup / settings toggles */}
          <div className="voyeur-row">
            <span
              className={`voyeur-asr ${asrReady ? 'voyeur-asr--on' : 'voyeur-asr--off'}`}
              title="Transcription runs locally via whisper. Without it, Voyeur Mode still captures overs; it just won't transcribe or identify callsigns."
            >
              <span className="voyeur-asr__dot" />
              {asrReady === null ? 'checking…' : asrReady ? 'transcription on' : 'transcription off'}
            </span>
            <span style={{ flex: 1 }} />
            <button
              type="button"
              className={`btn sm ${showSettings ? 'active' : ''}`}
              onClick={() => setShowSettings((v) => !v)}
              aria-expanded={showSettings}
            >
              {showSettings ? 'Hide settings' : 'Settings'}
            </button>
            <button
              type="button"
              className="btn sm"
              onClick={() => setShowHelp((v) => !v)}
              aria-expanded={showHelp}
            >
              {showHelp ? 'Hide setup' : 'How to set up & use'}
            </button>
          </div>

          {/* Prominent setup control whenever transcription is off. Two
              one-time downloads: the speech engine, then a speech model. */}
          {asrReady === false && (
            <div className="voyeur-dl">
              {install?.phase === 'Downloading' ? (
                <>
                  <div style={{ flex: 1, minWidth: 0 }}>
                    <div className="voyeur-bar">
                      <div className="voyeur-bar__fill" style={{ width: `${install.percent}%` }} />
                    </div>
                    <div className="voyeur-dl__msg">{install.message}</div>
                  </div>
                  <button type="button" className="btn sm tx" onClick={onCancelInstall}>
                    Cancel
                  </button>
                </>
              ) : (
                <div className="voyeur-setup">
                  <div className="voyeur-setup__hdr">
                    Set up transcription — two one-time downloads, no terminal:
                  </div>
                  {/* Step 1 — speech engine */}
                  <div className="voyeur-setup__row">
                    <span className="voyeur-setup__step">
                      {install?.binaryPresent ? '✓' : '1'}
                    </span>
                    <span className="voyeur-setup__name">Speech engine</span>
                    {install?.binaryPresent ? (
                      <span className="voyeur-setup__done">installed</span>
                    ) : (
                      <button
                        type="button"
                        className="btn sm active"
                        onClick={() => onInstall('engine-whisper')}
                        title="Download the whisper.cpp speech engine for your platform (one-time)"
                      >
                        Download engine
                      </button>
                    )}
                  </div>
                  {/* Step 2 — speech model */}
                  <div className="voyeur-setup__row">
                    <span className="voyeur-setup__step">
                      {install?.modelPresent ? '✓' : '2'}
                    </span>
                    <span className="voyeur-setup__name">Speech model</span>
                    {install?.modelPresent ? (
                      <span className="voyeur-setup__done">installed</span>
                    ) : (
                      <>
                        <select
                          value={chosenModel}
                          onChange={(e) => setChosenModel(e.target.value)}
                          aria-label="Speech model"
                        >
                          <option value="medium.en">Medium — recommended</option>
                          <option value="small.en">Small — faster download</option>
                        </select>
                        <button
                          type="button"
                          className="btn sm active"
                          onClick={() => onInstall(chosenModel)}
                        >
                          Download
                        </button>
                      </>
                    )}
                  </div>
                </div>
              )}
            </div>
          )}

          {showSettings && renderSettings()}

          {showHelp && (
            <div className="voyeur-help">
              <h4>What it does</h4>
              Park the radio on a busy frequency (a net, a rag-chew) and press
              LISTEN. Voyeur Mode records each transmission, then — if transcription
              is set up — writes out what was said and who said it. Walk away; come
              back to a log of the activity.
              <h4>Using it</h4>
              <ol>
                <li>Tune to the frequency you want to monitor (USB/LSB as normal).</li>
                <li>
                  Press <strong>LISTEN</strong>. The live bar shows overs being captured.
                </li>
                <li>Leave it running. Open a log anytime to read the transcript and roster.</li>
                <li>
                  ★ <strong>saves</strong> a log (protects it from auto-cleanup); ✕{' '}
                  <strong>deletes</strong> it and its audio. Click a name to rename.
                </li>
              </ol>
              <h4>Transcription (one-time, optional)</h4>
              Runs locally — the speech-to-text transcription happens entirely on your
              machine, and the audio is never uploaded for transcription. It needs two
              one-time downloads (no terminal): the <strong>speech engine</strong>{' '}
              for your platform ({install?.rid ?? 'your OS'}) and a{' '}
              <strong>speech model</strong>. Use the two-step panel above; the
              bigger model is more accurate on noisy SSB, the smaller one downloads
              faster. You only download once.
              {install?.phase === 'Done' && (
                <div style={{ color: 'var(--green-soft)', marginTop: 4 }}>✓ {install.message}</div>
              )}
              {install?.phase === 'Error' && (
                <div className="voyeur-error" style={{ marginTop: 4 }}>
                  Download failed: {install.message}
                </div>
              )}
              <h4>AI summaries (optional)</h4>
              A plain-English recap of each net, written locally by a small
              language model — also two one-time downloads, both optional.
              <div className="voyeur-setup" style={{ marginTop: 6 }}>
                <div className="voyeur-setup__row">
                  <span className="voyeur-setup__step">
                    {install?.digestBinaryPresent ? '✓' : '·'}
                  </span>
                  <span className="voyeur-setup__name">Summary engine</span>
                  {install?.digestBinaryPresent ? (
                    <span className="voyeur-setup__done">installed</span>
                  ) : (
                    <button
                      type="button"
                      className="btn sm active"
                      disabled={install?.phase === 'Downloading'}
                      onClick={() => onInstall('engine-llama')}
                    >
                      Download engine
                    </button>
                  )}
                </div>
                <div className="voyeur-setup__row">
                  <span className="voyeur-setup__step">
                    {install?.digestModelPresent ? '✓' : '·'}
                  </span>
                  <span className="voyeur-setup__name">Summary model</span>
                  {install?.digestModelPresent ? (
                    <span className="voyeur-setup__done">installed</span>
                  ) : (
                    <button
                      type="button"
                      className="btn sm active"
                      disabled={install?.phase === 'Downloading'}
                      onClick={() => onInstall('digest-small')}
                    >
                      Download
                    </button>
                  )}
                </div>
              </div>
              <h4>Watchword alerts &amp; privacy</h4>
              Transcription and summaries stay on your machine. The optional{' '}
              <strong>Watchword alerts</strong> (in Settings) are the one exception:
              if you turn on <strong>“attach the over’s audio clip”</strong>, the
              matched recording is sent off this computer to the email / push
              destination you configure. With clip attachments off, alerts carry only
              text. Nothing is sent unless you set this up.
              <h4>Reading the roster</h4>
              <span style={{ color: 'var(--accent)' }}>Blue</span> = QRZ-confirmed (real
              licensee, name shown). <span style={{ color: 'var(--power)' }}>Amber</span> =
              heard but unverified. Grey = no decodable callsign. HF voice is noisy, so
              expect a useful gist — not a perfect transcript.
            </div>
          )}

          {error && <div className="voyeur-error">{error}</div>}
        </div>

        {/* The intercepted-comms log */}
        <div className="voyeur__log">
          <div className="voyeur-loghdr">
            <span className="voyeur-loghdr__title">Logs · {sessions.length}</span>
            <input
              className="voyeur-search"
              type="search"
              placeholder="search callsign or text…"
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              aria-label="Search logs"
            />
          </div>

          {legend}

          {hits !== null && (
            <>
              {hits.length === 0 && (
                <div className="voyeur-empty">No matches for “{query}”.</div>
              )}
              {hits.map((hit) => (
                <div className="voyeur-card" key={hit.sessionId}>
                  <div className="voyeur-card__meta" style={{ paddingTop: 6 }}>
                    <span className="chip mono"><span className="v">{fmtFreq(hit.freqHz)}</span></span>
                    <span className="chip mono"><span className="k">when</span><span className="v">{fmtWhen(hit.startedUtc)}</span></span>
                    <span className="chip mono"><span className="k">hits</span><span className="v">{hit.matches.length}</span></span>
                    <span style={{ flex: 1 }} />
                    <button
                      type="button"
                      className="btn sm"
                      onClick={() => {
                        setQuery('');
                        void openSession(hit.sessionId);
                      }}
                    >
                      Open log
                    </button>
                  </div>
                  {renderOvers(hit.matches, 'No matching overs.')}
                </div>
              ))}
            </>
          )}

          {hits === null && sessions.length === 0 && (
            <div className="voyeur-empty">No logs yet — press LISTEN.</div>
          )}
          {hits === null &&
            sessions.map((s) => (
            <div className="voyeur-card" key={s.id}>
              <div className="voyeur-card__head">
                <button
                  type="button"
                  className={`voyeur-pin ${s.pinned ? 'voyeur-pin--on' : ''}`}
                  title={s.pinned ? 'Saved (won’t be auto-pruned). Click to unsave.' : 'Save this log'}
                  onClick={() => onTogglePin(s)}
                >
                  {s.pinned ? '★' : '☆'}
                </button>
                <input
                  ref={editingRef}
                  className="voyeur-card__name"
                  defaultValue={s.label}
                  onBlur={(e) => onRename(s, e.currentTarget.value)}
                  onKeyDown={(e) => {
                    if (e.key === 'Enter') e.currentTarget.blur();
                  }}
                  aria-label="Log name"
                />
                {openId === s.id && (
                  <div className="voyeur-viewtoggle">
                    <button
                      type="button"
                      className={`btn sm ${(view[s.id] ?? 'log') === 'log' ? 'active' : ''}`}
                      onClick={() => setSessionView(s.id, 'log')}
                    >
                      Log
                    </button>
                    <button
                      type="button"
                      className={`btn sm ${view[s.id] === 'roster' ? 'active' : ''}`}
                      onClick={() => setSessionView(s.id, 'roster')}
                    >
                      Roster
                    </button>
                  </div>
                )}
                <button type="button" className="btn sm" onClick={() => openSession(s.id)}>
                  {openId === s.id ? 'Hide' : 'Open'}
                </button>
                <button
                  type="button"
                  className="btn sm tx"
                  title="Delete this log and its audio"
                  onClick={() => onDelete(s)}
                >
                  ✕
                </button>
              </div>
              <div className="voyeur-card__meta">
                <span className="chip mono">
                  <span className="v">{fmtFreq(s.freqHz)}</span>
                </span>
                <span className="chip">
                  <span className="v">{s.mode}</span>
                </span>
                <span className="chip mono">
                  <span className="k">overs</span>
                  <span className="v">{s.segmentCount}</span>
                </span>
                <span className="chip mono">
                  <span className="k">when</span>
                  <span className="v">{fmtWhen(s.startedUtc)}</span>
                </span>
                {s.hasAudio && (
                  <span className="chip">
                    <span className="v">audio</span>
                  </span>
                )}
              </div>

              {openId === s.id && (
                <>
                  {renderSummary(s.id)}
                  {view[s.id] === 'roster'
                    ? renderRoster(s.id)
                    : !detail
                      ? <div className="voyeur-empty" style={{ padding: '6px 10px' }}>Loading…</div>
                      : renderOvers(detail.segments, 'No overs captured.')}
                </>
              )}
            </div>
          ))}
        </div>
      </div>
    </>
  );
}

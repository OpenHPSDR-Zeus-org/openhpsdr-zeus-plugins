// SPDX-License-Identifier: GPL-2.0-or-later
//
// Voyeur Mode plugin — REST client. Relocated from the in-tree
// zeus-web/src/api/voyeur.ts. The types are unchanged (the backend wire shape
// is preserved); the only difference is transport: instead of fetch() against
// absolute /api/voyeur/* paths, every call goes through the host-provided
// ZeusPluginApi.callBackend, which routes to /api/plugins/com.kb2uka.voyeur/*.
// The host api is bound once at module load by the plugin entry.

const PLUGIN_ID = 'com.kb2uka.voyeur';

export interface ZeusPluginApi {
  registerPanel(spec: { id: string; component: React.ComponentType<{ onRemove?: () => void }> }): void;
  callBackend(method: string, path: string, body?: unknown): Promise<Response>;
}

let _api: ZeusPluginApi | null = null;
export function bindVoyeurApi(api: ZeusPluginApi) { _api = api; }

async function call<T>(method: string, path: string, body?: unknown): Promise<T> {
  if (_api === null) throw new Error('Voyeur plugin api not bound');
  const res = await _api.callBackend(method, path, body);
  if (!res.ok) {
    let message = `${res.status} ${res.statusText}`;
    try {
      const errBody = (await res.json()) as { error?: string };
      if (errBody?.error) message = errBody.error;
    } catch {
      /* ignore */
    }
    throw new Error(message);
  }
  return (await res.json()) as T;
}

export type VoyeurStatus = {
  active: boolean;
  sessionId: string | null;
  freqHz: number;
  mode: string;
  band: string;
  segmentCount: number;
  capturedSeconds: number;
  droppedSamples: number;
  ringFillPct: number;
  degraded: boolean;
  transcriptionAvailable: boolean;
};

export type VoyeurSession = {
  id: string;
  label: string;
  startedUtc: string;
  endedUtc: string | null;
  freqHz: number;
  mode: string;
  band: string;
  segmentCount: number;
  capturedSeconds: number;
  droppedSamples: number;
  pinned: boolean;
  hasAudio: boolean;
};

export type VoyeurSegment = {
  id: string;
  startedUtc: string;
  durationMs: number;
  peakDbfs: number;
  hasAudio: boolean;
  transcript: string | null;
  callsign: string | null;
  callsignState: string | null;
  callsignName: string | null;
};

export type VoyeurSessionDetail = {
  session: VoyeurSession;
  segments: VoyeurSegment[];
};

export type VoyeurTranscription = { available: boolean; modelDir: string; digestAvailable: boolean };

export type VoyeurModel = { id: string; label: string };

export type VoyeurInstall = {
  phase: 'Idle' | 'Downloading' | 'Done' | 'Error';
  percent: number;
  message: string;
  item: string | null;
  modelPresent: boolean;
  binaryPresent: boolean;
  digestModelPresent: boolean;
  digestBinaryPresent: boolean;
  rid: string;
};

export type VoyeurRosterEntry = {
  callsign: string;
  name: string | null;
  state: 'confirmed' | 'tentative';
  overCount: number;
  firstHeardUtc: string;
  lastHeardUtc: string;
};

export type VoyeurReport = {
  session: VoyeurSession;
  roster: VoyeurRosterEntry[];
  uniqueStations: number;
  confirmedStations: number;
  transcribedOvers: number;
  digest: string | null;
};

export type VoyeurSearchHit = {
  sessionId: string;
  sessionLabel: string;
  freqHz: number;
  startedUtc: string;
  matches: VoyeurSegment[];
};

// signal params are kept for source-compatibility with the panel's polling
// call sites; callBackend has no AbortSignal channel so they're unused (a poll
// in flight at unmount simply completes and is discarded).
export const getVoyeurStatus = (_signal?: AbortSignal) =>
  call<VoyeurStatus>('GET', '/status');

export const getVoyeurTranscription = (_signal?: AbortSignal) =>
  call<VoyeurTranscription>('GET', '/transcription');

export const getVoyeurModels = (_signal?: AbortSignal) =>
  call<VoyeurModel[]>('GET', '/install/models');

export const getVoyeurInstallStatus = (_signal?: AbortSignal) =>
  call<VoyeurInstall>('GET', '/install/status');

export const installVoyeurModel = (model: string) =>
  call<VoyeurInstall>('POST', '/install/model', { model });

export const cancelVoyeurInstall = () =>
  call<VoyeurInstall>('POST', '/install/cancel');

export const startVoyeur = (keepAudio = true) =>
  call<VoyeurStatus>('POST', '/start', { keepAudio });

export const stopVoyeur = () =>
  call<VoyeurStatus>('POST', '/stop');

export const listVoyeurSessions = (_signal?: AbortSignal) =>
  call<VoyeurSession[]>('GET', '/sessions');

export const getVoyeurSession = (id: string, _signal?: AbortSignal) =>
  call<VoyeurSessionDetail>('GET', `/sessions/${encodeURIComponent(id)}`);

export const updateVoyeurSession = (
  id: string,
  patch: { label?: string; pinned?: boolean },
) =>
  call<VoyeurSession>('PATCH', `/sessions/${encodeURIComponent(id)}`, patch);

export const deleteVoyeurSession = (id: string) =>
  call<{ deleted: string }>('DELETE', `/sessions/${encodeURIComponent(id)}`);

export const getVoyeurReport = (id: string, _signal?: AbortSignal) =>
  call<VoyeurReport>('GET', `/sessions/${encodeURIComponent(id)}/report`);

export const generateVoyeurDigest = (id: string) =>
  call<VoyeurReport>('POST', `/sessions/${encodeURIComponent(id)}/digest`);

export const searchVoyeur = (q: string, _signal?: AbortSignal) =>
  call<VoyeurSearchHit[]>('GET', `/search?q=${encodeURIComponent(q)}`);

// Direct browser GET (an <audio src>), so it needs the absolute host path the
// host mounts this plugin's backend under — not a callBackend round-trip.
export const voyeurSegmentAudioUrl = (segmentId: string) =>
  `/api/plugins/${PLUGIN_ID}/segments/${encodeURIComponent(segmentId)}/audio`;

// ---------------------------------------------------------------------------
// Settings — engine selection, segmentation, watchword alerts, training corpus
// All mirror the backend wire shapes (camelCase; string enums). Secrets are
// write-only: GET surfaces only `hasPassword`/`hasToken` booleans; PUT omits a
// secret field unless the operator typed a new value.
// ---------------------------------------------------------------------------

// engine ::= "Whisper" (locked default) | "Parakeet" (opt-in). Compared
// case-insensitively in the UI because the GET echoes the PascalCase enum name.
export type VoyeurEngineConfig = {
  engine: string;
  provider: string;
  gpuEnabled: boolean;
  resolvedProvider: string;
  available: { whisper: boolean; parakeet: boolean };
};

export type VoyeurSegConfig = {
  useVad: boolean;
  openMarginDb: number;
  hangSeconds: number;
  preRollMs: number;
  maxOverSeconds: number;
};

// type ::= "Callsign" | "Keyword" (backend enum name; read case-insensitively).
export type VoyeurWatchword = { text: string; type: string; enabled: boolean };

export type VoyeurAlertEmail = {
  enabled: boolean;
  host: string;
  port: number;
  useSsl: boolean;
  username: string;
  from: string;
  to: string;
  hasPassword: boolean; // a secret is stored — never the secret itself
};
export type VoyeurAlertNtfy = {
  enabled: boolean;
  serverUrl: string;
  topic: string;
  hasToken: boolean;
};
export type VoyeurAlertSms = { enabled: boolean; gatewayAddress: string };

export type VoyeurAlertConfig = {
  enabled: boolean;
  watchwords: VoyeurWatchword[];
  email: VoyeurAlertEmail;
  ntfy: VoyeurAlertNtfy;
  sms: VoyeurAlertSms;
  cooldownSeconds: number;
  globalRateCapPer10Min: number;
  attachClip: boolean;
  clipMaxSeconds: number;
  zeusBaseUrl: string | null;
};

// PUT shape — every field optional (partial update). Secret fields
// (email.password, ntfy.token) are sent ONLY when the operator typed a value.
export type VoyeurAlertConfigUpdate = {
  enabled?: boolean;
  watchwords?: VoyeurWatchword[];
  email?: {
    enabled?: boolean;
    host?: string;
    port?: number;
    useSsl?: boolean;
    username?: string;
    password?: string;
    from?: string;
    to?: string;
  };
  ntfy?: { enabled?: boolean; serverUrl?: string; topic?: string; token?: string };
  sms?: VoyeurAlertSms;
  cooldownSeconds?: number;
  globalRateCapPer10Min?: number;
  attachClip?: boolean;
  clipMaxSeconds?: number;
  zeusBaseUrl?: string | null;
};

export type VoyeurAlertTestResult = { channel: string; ok: boolean; detail: string };

export type VoyeurCorpusStats = {
  retainCorpus: boolean;
  clipCount: number;
  maxClips: number;
  bytes: number;
  path: string;
};

export const getVoyeurEngineConfig = (_signal?: AbortSignal) =>
  call<VoyeurEngineConfig>('GET', '/config/engine');
export const putVoyeurEngineConfig = (body: {
  engine?: string;
  provider?: string;
  gpuEnabled?: boolean;
}) => call<VoyeurEngineConfig>('PUT', '/config/engine', body);

export const getVoyeurSegConfig = (_signal?: AbortSignal) =>
  call<VoyeurSegConfig>('GET', '/config/seg');
export const putVoyeurSegConfig = (body: VoyeurSegConfig) =>
  call<VoyeurSegConfig>('PUT', '/config/seg', body);

export const getVoyeurAlertConfig = (_signal?: AbortSignal) =>
  call<VoyeurAlertConfig>('GET', '/alerts/config');
export const putVoyeurAlertConfig = (body: VoyeurAlertConfigUpdate) =>
  call<VoyeurAlertConfig>('PUT', '/alerts/config', body);
export const testVoyeurAlert = (channel: string) =>
  call<VoyeurAlertTestResult[]>('POST', '/alerts/test', { channel });

export const getVoyeurCorpus = (_signal?: AbortSignal) =>
  call<VoyeurCorpusStats>('GET', '/corpus');
export const patchVoyeurCorpus = (body: { retainCorpus?: boolean; maxClips?: number }) =>
  call<VoyeurCorpusStats>('PATCH', '/corpus', body);

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

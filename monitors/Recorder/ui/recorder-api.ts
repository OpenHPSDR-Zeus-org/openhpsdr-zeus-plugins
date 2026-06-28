// SPDX-License-Identifier: GPL-2.0-or-later
//
// WAV Recorder plugin — host transport shim. Relocated from the in-tree
// /api/wav/* fetch() calls. The wire shape is preserved verbatim; the only
// difference is transport: instead of fetch() against absolute /api/wav/*
// paths, every call goes through the host-provided ZeusPluginApi.callBackend,
// which routes to /api/plugins/com.kb2uka.recorder/*. The host api is bound
// once at module load by the plugin entry.

import type { ComponentType } from 'react';

export interface ZeusPluginApi {
  registerPanel(spec: { id: string; component: ComponentType<{ onRemove?: () => void }> }): void;
  callBackend(method: string, path: string, body?: unknown): Promise<Response>;
}

// The in-core panel used absolute /api/wav/<rest> paths. The host scopes this
// plugin's backend under /api/plugins/<id>, so we strip the legacy prefix and
// hand callBackend the <rest> (it re-prefixes). Keeping the legacy prefix in
// the call sites means the ported panel/modal stay byte-identical apart from
// swapping `fetch` → `wavFetch`.
const LEGACY_PREFIX = '/api/wav';

let _api: ZeusPluginApi | null = null;
export function bindRecorderApi(api: ZeusPluginApi) { _api = api; }

/**
 * Drop-in replacement for the panel's `fetch('/api/wav/...', init?)` calls.
 * Returns the raw {@link Response} so the existing `res.ok` / `res.json()` /
 * `res.text()` handling is unchanged. GET is the default method; a JSON body is
 * passed through as an object (callBackend serialises it), so callers no longer
 * stringify or set Content-Type. callBackend has no AbortSignal channel, so any
 * `signal` a caller passed is ignored — a request in flight at unmount simply
 * completes and is discarded.
 */
export function wavFetch(path: string, init?: { method?: string; body?: unknown }): Promise<Response> {
  if (_api === null) throw new Error('Recorder plugin api not bound');
  const rel = path.startsWith(LEGACY_PREFIX) ? path.slice(LEGACY_PREFIX.length) : path;
  return _api.callBackend(init?.method ?? 'GET', rel, init?.body);
}

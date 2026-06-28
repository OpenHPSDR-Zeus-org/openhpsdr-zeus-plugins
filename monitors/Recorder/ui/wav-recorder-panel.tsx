// SPDX-License-Identifier: GPL-2.0-or-later
//
// WAV Recorder · Tape Deck — UI module entry. Binds the host plugin api,
// injects the panel stylesheet once (imported ?raw and injected as a <style>
// at load — it references the host's global design tokens, resolved on :root),
// and registers the 'wavrecorder' panel. The panel id matches the in-core panel
// so the host's per-tile layout selectors keep working; the two tile-body
// column-flex rules the core all-panels.css used to carry are reproduced inside
// WavRecorderPanel.css so the tile renders byte-identical to today.

import wavCss from './WavRecorderPanel.css?raw';
import { WavRecorderPanel } from './WavRecorderPanel';
import { bindRecorderApi, type ZeusPluginApi } from './recorder-api';

export default function register(api: ZeusPluginApi) {
  bindRecorderApi(api);

  if (typeof document !== 'undefined' && !document.getElementById('wav-recorder-plugin-styles')) {
    const style = document.createElement('style');
    style.id = 'wav-recorder-plugin-styles';
    style.textContent = wavCss;
    document.head.appendChild(style);
  }

  api.registerPanel({ id: 'wavrecorder', component: WavRecorderPanel });
}

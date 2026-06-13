// SPDX-License-Identifier: GPL-2.0-or-later
//
// Voyeur Mode · Net Monitor — UI module entry. Binds the host plugin api,
// injects the panel stylesheet once (existing UI plugins don't ship a separate
// .css; voyeur.css is large, so it's imported ?raw and injected as a <style>
// at load — it references the host's global design tokens, resolved on :root),
// and registers the 'voyeur' panel.

import voyeurCss from './voyeur.css?raw';
import { VoyeurPanel } from './VoyeurPanel';
import { bindVoyeurApi, type ZeusPluginApi } from './voyeur-api';

export default function register(api: ZeusPluginApi) {
  bindVoyeurApi(api);

  if (typeof document !== 'undefined' && !document.getElementById('voyeur-plugin-styles')) {
    const style = document.createElement('style');
    style.id = 'voyeur-plugin-styles';
    style.textContent = voyeurCss;
    document.head.appendChild(style);
  }

  api.registerPanel({ id: 'voyeur', component: VoyeurPanel });
}

# RF-Kit RF2K-S amplifier — Openhpsdr-Zeus plugin

Remote control of the **RF-Kit RF2K-S** 1.5 kW solid-state HF
amplifier from Openhpsdr-Zeus. Surfaces forward / reflected power,
SWR, temperature, PA voltage and current, antenna selection,
operate / standby, operational-interface mode (UNIV / CAT / UDP /
TCI), and remote-trigger of the front-panel **Tune** and **Bypass**
buttons via VNC click injection.

- Plugin id: `com.openhpsdr.zeus.plugins.rf2k`
- Slot: `workspace.amplifier`
- Capabilities requested: `PersistSettings`
- Permissions requested: `network: true`

## Hardware and protocol

The plugin speaks to the amp on two TCP ports — both must be
reachable from the Zeus host:

- **HTTP REST on TCP :8080** — JSON endpoints exposed by the amp's
  on-board firmware. Used for `info`, `data`, `power`, `tuner`,
  `operate-mode`, `operational-interface`, `antennas`. This is the
  amp's documented control surface.
- **RFB (VNC) on TCP :5900** — a single PointerEvent click is
  injected to engage **Tune** and **Bypass**. The REST API does not
  expose endpoints for these two actions; the original firmware
  implements them as local Tk button handlers
  (`CT1IQI/RF2K-S/.../main_screen/tuners.py` and
  `.../operating_buttons.py`). The plugin connects → handshakes →
  sends 2 PointerEvent packets (down, up) → closes. No framebuffer
  decoding, no held session, microsecond CPU impact. RFB 3.3 / 3.7 /
  3.8 supported, with VncAuth or None security.

**Firewall:** allow outbound TCP :8080 and :5900 to the amp's IP
address. The amp itself does not need to reach back into Zeus.

## REST surface (host-mounted)

The plugin runtime mounts every endpoint under
`/api/plugins/com.openhpsdr.zeus.plugins.rf2k/`. The wire format is
preserved 1:1 from the legacy in-tree `/api/rf2k/*` routes — anything
calling those URLs in a pre-extraction Zeus build only needs the
prefix swap:

| Method | Path | Body |
|---|---|---|
| `GET`  | `/status`     | — |
| `GET`  | `/config`     | — |
| `POST` | `/config`     | `Rf2kConfig` |
| `POST` | `/operate`    | `{ "mode": "STANDBY" \| "OPERATE" }` |
| `POST` | `/interface`  | `{ "iface": "UNIV" \| "CAT" \| "UDP" \| "TCI" }` |
| `POST` | `/antenna`    | `{ "type": "INTERNAL" \| "EXTERNAL", "number"?: int }` |
| `POST` | `/reset`      | — |
| `POST` | `/test`       | `{ "host", "port" }` |
| `POST` | `/tune`       | — *(VNC click)* |
| `POST` | `/bypass`     | — *(VNC click)* |
| `POST` | `/click`      | `{ "x", "y" }` — calibration |

## Install

```bash
cd amplifiers/Rf2k
dotnet publish -c Release -o publish/
cd publish && zip -r ../rf2k-1.0.0.zip Rf2k.dll plugin.json ui/
```

Sideload into Zeus via **Settings → Plugins → Install from file…**
(or `POST /api/plugins/install` with the zip), then restart. The
**RF2K-S Amplifier** panel appears under **Add Panel →
workspace.amplifier**.

The panel's UI binds soft to Zeus's `--immersive-*` and `--fg-*`
CSS variables with hex fallbacks, so it inherits whatever theme the
operator has active.

## Known limits

- **Tune and Bypass require VNC click coordinates calibrated for
  your firmware revision.** The amp's Tk UI moves buttons subtly
  between firmware versions, so the on-screen pixel coordinates the
  plugin injects must be set per unit. Use the in-panel
  **Calibrate** workflow: enable VNC mirroring, hover the live amp
  framebuffer, capture (x, y) for the Tune button and again for
  Bypass, save. Until calibrated, `/tune` and `/bypass` will click
  the wrong spot (or harmlessly miss). The plugin exposes
  `POST /click { x, y }` as the calibration primitive.
- **Persisted config does not auto-migrate from the in-tree Zeus
  build.** Operators upgrading from a pre-`acd1a97` Zeus had their
  host / port / VNC settings stored in `zeus-prefs.db` under the
  `rf2k_config` collection. The plugin's `IPluginSettings` store is
  a separate, plugin-scoped collection — first run starts from
  defaults and the operator must re-enter:
    - amp IP / hostname
    - REST port (default 8080) and VNC port (default 5900)
    - VNC password
    - calibration click coordinates
  Wire format on both ports is unchanged, so once re-entered the amp
  responds identically.
- The plugin polls REST endpoints periodically; it does not push
  events. Frequency / SWR updates lag the amp's front panel by the
  poll interval (default 1 s, configurable).

## Provenance

Previously shipped **in-tree** in Zeus core at:

- `Zeus.Server.Hosting/Rf2kService.cs`
- `Zeus.Server.Hosting/Rf2kVncClient.cs`
- `Zeus.Server.Hosting/Rf2kSettingsStore.cs`
- `Zeus.Contracts/Rf2kDtos.cs`
- `zeus-web/src/api/rf2k.ts`
- `zeus-web/src/state/rf2k-store.ts`
- `zeus-web/src/layout/panels/Rf2kPanel.tsx`

Extracted on Zeus branch
[`feature/extract-rf2k-plugin`](https://github.com/Kb2uka/openhpsdr-zeus/tree/feature/extract-rf2k-plugin)
in commit `acd1a97` and re-landed as this plugin in commit
`2ca4ca3`. See `docs/lessons/rf2k-extracted-to-plugin.md` in the
Zeus repo for the full migration note.

## Upstream

- RF-Kit RF2K-S product page: <https://www.rf-kit.de/>
- The amp's firmware exposes a small JSON REST API on :8080; the
  vendor publishes the endpoint catalog in the operator manual
  shipped with each unit. The plugin tracks that document.
- RFB protocol reference (VNC click injection):
  <https://datatracker.ietf.org/doc/html/rfc6143>

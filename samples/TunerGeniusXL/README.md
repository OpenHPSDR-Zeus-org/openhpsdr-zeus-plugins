# Tuner Genius XL (TGXL) — Openhpsdr-Zeus plugin

Remote control of the **4O3A Tuner Genius XL** antenna tuner from
Openhpsdr-Zeus. One L/C network serves up to two radios (SO2R); the
plugin surfaces a status panel and the four operator buttons that
the front-panel UI exposes.

- Plugin id: `com.openhpsdr.zeus.plugins.tgxl`
- Slot: `workspace.tuner`
- Capabilities requested: `PersistSettings`
- Permissions requested: `network: true`

## Hardware and protocol

- TGXL ships a single ASCII line protocol shared with the rest of the
  4O3A "Genius" family. Discovery and control both use **port 9010**:
  - **UDP :9010** — broadcast discovery (plugin listens for the amp's
    announcement and learns its IP / serial).
  - **TCP :9010** — control connection. Commands are
    `C{seq}|{command}\n`; replies are `R{seq}|{hex_result}|{message}`;
    asynchronous status frames are `S{seq}|status key1=val1 key2=val2 …`.
- The plugin polls `status` continuously and forwards the parsed
  key-value frame to the panel.
- Key commands wired: `status`, `autotune`, `bypass set=0|1`,
  `operate set=0|1`, `activate ch=1|2`.

**Firewall:** the host running Zeus must be allowed inbound UDP
broadcast and outbound TCP on :9010 to reach the tuner. Both Radio A
and Radio B run through the same connection.

## REST surface (host-mounted)

The plugin runtime mounts the plugin's endpoints under
`/api/plugins/com.openhpsdr.zeus.plugins.tgxl/`. The panel and any
external automation should call:

| Method | Path | Body |
|---|---|---|
| `GET`  | `/status` | — |
| `POST` | `/devices/{serial}/tune`     | — |
| `POST` | `/devices/{serial}/bypass`   | `{ "bypass": true \| false }` |
| `POST` | `/devices/{serial}/operate`  | `{ "operate": true \| false }` |
| `POST` | `/devices/{serial}/activate` | `{ "channel": 1 \| 2 }` |

`/status` returns the discovered TGXL device list with their latest
parsed status frame (forward power in W, SWR ratio, per-radio band
and freq, PTT, operate/standby state).

## Install

```bash
cd samples/TunerGeniusXL
dotnet publish -c Release -o publish/
cd publish && zip -r ../tunergeniusxl-1.0.0.zip TunerGeniusXL.dll plugin.json ui/
```

Sideload into Zeus via **Settings → Plugins → Install from file…**
(or `POST /api/plugins/install` with the zip), then restart. The
**Tuner Genius XL** panel becomes available under **Add Panel →
workspace.tuner**.

The plugin's persisted settings (manually-entered IP if discovery is
disabled, polling interval) live in the host-managed
`IPluginSettings` store — they survive restart but are scoped to this
plugin only.

## Known limits

- Tested against TGXL firmware that follows the published 4O3A line
  protocol. Vendor firmware updates that change the status key set
  may surface unmapped fields in `/status` until the plugin is
  updated.
- The plugin does not yet expose memory-bank recall or per-band L/C
  presets — operators set those from the front panel or the 4O3A
  desktop tool.
- Discovery uses UDP broadcast on the host's primary interface; on
  multi-homed boxes you may need to set the static IP in the panel
  settings rather than rely on auto-discovery.

## Provenance

Ported from the Log4YM project's in-tree integration —
`src/Log4YM.Server/Services/TunerGeniusService.cs` and
`src/Log4YM.Web/src/plugins/TunerGeniusPlugin.tsx`. The plugin
replaces the SignalR push pipeline used in Log4YM with the Zeus
plugin runtime's REST-poll model (matching the `samples/Amplifier/`
contract). DTOs ported from `Log4YM.Contracts/Events/LogEvents.cs`
(`TunerGenius*` records).

## Upstream

- 4O3A Tuner Genius XL product page:
  <https://4o3a.com/index.php/products/tuner-genius-xl>
- The vendor's "Tuner Genius XL API" PDF documents the
  `C{seq}|command\n` framing and the `status` key set. Operators
  with a TGXL on hand will have received the API doc with their unit
  — Zeus's plugin tracks that document, not a clean-room reverse.

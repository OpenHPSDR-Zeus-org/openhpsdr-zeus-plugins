# Power Genius XL (PGXL) — Openhpsdr-Zeus plugin

Remote control of the **4O3A Power Genius XL** 1.5 kW solid-state HF
amplifier from Openhpsdr-Zeus. The plugin surfaces operate/standby,
A/B band+bias selection, forward power, SWR, drive, PA current and
temperature, and a FlexRadio-pairing override so PGXL plays nicely
with non-Flex radios (HL2, ANAN-class, etc.).

- Plugin id: `com.openhpsdr.zeus.plugins.pgxl`
- Slot: `workspace.amplifier`
- Capabilities requested: `PersistSettings`
- Permissions requested: `network: true`

## Hardware and protocol

PGXL speaks the shared 4O3A "Genius" ASCII line protocol. Discovery
and control both use **port 9008** (mirroring Antenna Genius / Tuner
Genius which sit on adjacent port numbers):

- **UDP :9008** — broadcast discovery. The plugin listens for the
  amp's announcement and learns its IP / serial.
- **TCP :9008** — control connection. Commands are
  `C{seq}|{command}\n`; replies are `R{seq}|{hex_result}|{message}`;
  asynchronous status frames are `S{seq}|status key1=val1 …`.
- The plugin maintains a persistent TCP session per amp, polls
  `status` continuously, and sends keepalives.

**Firewall:** the Zeus host needs inbound UDP broadcast and outbound
TCP on :9008 to reach the amp. PGXL's own network configuration
(static IP / DHCP / pairing mode) is left as the operator set it
from the front panel or 4O3A desktop tool — the plugin does not
reconfigure the amp.

## REST surface (host-mounted)

The plugin runtime mounts the plugin's endpoints under
`/api/plugins/com.openhpsdr.zeus.plugins.pgxl/`. The panel and any
external automation should call:

| Method | Path | Body |
|---|---|---|
| `GET`  | `/status`                              | — |
| `GET`  | `/devices/{serial}`                    | — |
| `POST` | `/devices/{serial}/operate`            | `{ "operate": true \| false }` |
| `POST` | `/devices/{serial}/flexradio/disable`  | `{ "slice": "A" \| "B" }` |

`/status` returns the discovered amplifier list with their latest
status frame (forward W, SWR, drive %, PA current A, temperature °C,
A/B band selection, operate/standby state, FlexRadio pairing flag).
`/devices/{serial}` returns the single-device subset.

## Install

```bash
cd samples/PowerGeniusXL
dotnet publish -c Release -o publish/
cd publish && zip -r ../powergeniusxl-1.0.0.zip PowerGeniusXL.dll plugin.json ui/
```

Sideload into Zeus via **Settings → Plugins → Install from file…**
(or `POST /api/plugins/install` with the zip), then restart. The
**Power Genius XL** panel appears under **Add Panel →
workspace.amplifier**.

Persisted settings (host IP if discovery is bypassed, FlexRadio
pairing override, polling interval) live in the host-managed
`IPluginSettings` store — they survive Zeus restart but are scoped
to this plugin.

## Known limits

- The plugin is a control / monitor surface, not a calibration tool.
  Band-edge tuning, PA-bias trim, and firmware updates still happen
  through 4O3A's own desktop utility or the amp's front panel.
- FlexRadio-pairing mode: PGXL ships configured to auto-pair with a
  FlexRadio over Ethernet. When driving a non-Flex radio (HL2 /
  ANAN-class), the operator must hit the **Disable Flex Pairing**
  button so the amp accepts CAT / RF-sense input from another
  source. The plugin exposes that toggle but does not pick a value
  for you — confirm with your station setup.
- **Vdd readout is a placeholder.** PGXL only publishes its DC rail
  voltage during TX, so the panel shows `52 V` while the amp is in
  operate (the nominal rail) and `0 V` in standby. This mirrors the
  compromise in the original Log4YM panel — the field is a "is the
  amp on?" indicator rather than a metered reading.
- Status keys may change with vendor firmware updates; unmapped
  fields surface in `/status` as raw key=value pairs until the
  plugin is updated.
- Discovery uses UDP broadcast on the host's primary interface; on
  multi-homed boxes, set the static IP in the panel settings rather
  than rely on auto-discovery.

## Provenance

Ported from the Log4YM project's in-tree integration —
`src/Log4YM.Server/Services/PgxlService.cs` (and its `PgxlConnection`
inner class) and `src/Log4YM.Web/src/plugins/PgxlPlugin.tsx`. DTOs
were lifted from `Log4YM.Contracts/Events/LogEvents.cs` (`Pgxl*`
records). The port replaces Log4YM's SignalR push pipeline with the
Zeus plugin runtime's REST-poll model (matching the
`samples/Amplifier/` reference contract).

## Upstream

- 4O3A Power Genius XL product page:
  <https://4o3a.com/index.php/products/power-genius-xl>
- The vendor's "Power Genius XL API" PDF documents the
  `C{seq}|command\n` framing and the `status` key set. Operators
  with a PGXL on hand will have received the API doc with their
  unit — Zeus's plugin tracks that document, not a clean-room
  reverse.

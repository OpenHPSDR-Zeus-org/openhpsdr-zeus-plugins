# 4O3A Antenna Genius — Openhpsdr-Zeus plugin

Discovers [4O3A Antenna Genius](https://www.4o3a.com/index.php/products/antenna-genius) antenna switches on the local network and exposes a per-port antenna selector inside Openhpsdr-Zeus.

## What it does

- Listens for `AG` discovery broadcasts on UDP port **9007**.
- Opens a TCP command session to each discovered device, reads the antenna list, band list, and per-port status.
- Subscribes to live port / relay updates from the device so the UI reflects band-follow and TX/RX state changes.
- Reconnects automatically with a 5-second back-off if the TCP session drops.
- Sends a `ping` every 30 seconds to keep NAT / firewall paths warm.
- **Manual connection (mDNS fallback):** if a manual IP/port is configured, the plugin dials the device directly over TCP and skips the discovery broadcast entirely. Auto-discovery keeps running in parallel, and the plugin will not open a second connection to a device it has already reached.

## Manual connection

Some managed switches filter the UDP discovery broadcast (the same multicast-filtering that breaks mDNS / Bonjour), so the device is never auto-discovered even though it is reachable. The **Connection settings** section at the bottom of the panel lets you pin the device directly:

1. Enter the Antenna Genius **IP address** (e.g. `192.168.10.24`) and **port** (default `9007`, the firmware v4.0+ command port).
2. Use **Test connection** to confirm the address is reachable before committing.
3. Click **Save** — the plugin connects immediately, no broadcast required. The setting persists across restarts.

Leave the IP field blank to use auto-discovery only (the default).

## Capabilities

This plugin declares `NetworkAccess` — required to bind UDP 9007 and open outbound TCP sessions.

## HTTP endpoints

All paths are auto-prefixed under `/api/plugins/com.openhpsdr.zeus.plugins.antennagenius/`.

| Method | Path | Body | Result |
|---|---|---|---|
| `GET`  | `status`         | — | Array of device statuses (serial, name, IP, connected, antennas, bands, port A / B) |
| `POST` | `select-antenna` | `{ "serial": "9A-3A-DC", "portId": 1, "antennaId": 3 }` | Sets RX + TX antenna for the requested port |
| `GET`  | `settings`       | — | Current manual-connection settings `{ "manualIpAddress": "", "manualPort": 9007 }` |
| `POST` | `settings`       | `{ "manualIpAddress": "192.168.10.24", "manualPort": 9007 }` | Persists the manual override and applies it live. Empty `manualIpAddress` reverts to auto-discovery only |
| `POST` | `test`           | `{ "ipAddress": "192.168.10.24", "port": 9007 }` | Probes the address without joining the pool → `{ "ok": true, "version": "4.0.22", "error": null }` |

## UI panel

A single workspace panel `antennagenius.main` is registered at slot `workspace.antennagenius`. The React module lives at `ui/antennagenius.es.js`.

## Hardware reference

- Vendor: [4O3A Signature](https://www.4o3a.com/)
- Discovery: UDP broadcast on port 9007 (`AG ip=… port=9007 v=… serial=… name=… ports=2 antennas=8 mode=master uptime=…`)
- Command protocol: ASCII, line-oriented, `C<seq>|<command>` request, `R<seq>|<hex>|<message>` response, `S0|<event>` async event.

## License

GPL-2.0-or-later — same as Openhpsdr-Zeus.

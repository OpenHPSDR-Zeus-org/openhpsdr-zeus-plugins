# Contributing a plugin to the Openhpsdr-Zeus registry

Quick version:

1. Publish a stable HTTPS download URL for your plugin zip (a GitHub
   Release on your plugin's source repo is the canonical choice).

2. Compute the SHA-256:
   ```bash
   sha256sum yourplugin-1.2.3.zip
   ```

3. Add an entry to `registry.json`. The shape is documented in
   `schema/registry.schema.json`; the minimal valid entry looks like:

   ```json
   {
     "id": "com.example.myplugin",
     "name": "My Plugin",
     "description": "One-line summary.",
     "author": "Your Name <you@example.com>",
     "license": "GPL-2.0-or-later",
     "homepage": "https://github.com/you/myplugin",
     "categories": ["amplifier"],
     "verified": false,
     "versions": [
       {
         "version": "1.0.0",
         "sdkAbi": 1,
         "sdkMinVersion": "1.0.0",
         "platforms": ["any"],
         "downloadUrl": "https://github.com/you/myplugin/releases/download/v1.0.0/myplugin-1.0.0.zip",
         "sha256": "<64 hex chars>"
       }
     ]
   }
   ```

4. Open a pull request. The validation workflow runs:
   - JSON-Schema check of `registry.json` (must validate against
     `schema/registry.schema.json`).
   - SHA-256 verification: the workflow downloads each `downloadUrl`
     and confirms the digest matches.
   - Manifest sanity probe: the workflow extracts `plugin.json` from
     the downloaded zip and validates it against
     `schema/manifest.schema.json`.

5. Once CI is green, the curator (Brian, EI6LF) merges the PR. Zeus
   clients see the new entry within minutes of merge (raw.githubusercontent.com
   propagates quickly).

## What we accept

- Plugins built against the current Openhpsdr-Zeus SDK ABI (currently
  `1`, see the Zeus repo's `Zeus.Plugins.Contracts/AbiVersion.cs`).
- Any FSF/OSI-approved license. Closed-source plugins are allowed
  provided the manifest declares the license honestly and the plugin
  is reachable for users to install.
- Native-bundling plugins (e.g. VST3 hosts) must declare the correct
  `platforms` RIDs so Zeus's catalog filter shows them only on
  compatible hosts.

## What we don't accept

- Plugins that bundle malware, telemetry, or credential exfiltration.
- Plugins that name-squat a popular name without authorship rights.
- Entries that 404 or whose SHA-256 mismatches what's at the URL.
- Plugins whose declared SDK ABI is older than what current Zeus
  supports (they wouldn't load anyway).

## Updating an existing entry

Add a new object to the plugin's `versions[]` array. Keep older
versions — operators may pin a specific version. CI runs the same
checks on every version listed.

## Marking a plugin "verified"

Authors cannot self-verify. The `verified: true` flag is set by the
curator after review of the plugin's source and a sanity run against
a Zeus instance. Self-verification PRs are rejected.

## Removing a plugin

If you need to take a plugin down (security incident, end-of-life):

1. Open a PR removing the entry, with a brief reason.
2. The curator reviews + merges.
3. Operators who have it installed are unaffected (it stays on their
   disk), but the entry is no longer listed in **Settings → Plugins →
   Browse**.

## Reporting a bad listing

Open an issue on this repo. Critical security issues: email
`b@briankeating.net`.

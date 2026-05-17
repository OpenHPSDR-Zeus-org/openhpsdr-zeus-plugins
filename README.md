# Openhpsdr-Zeus plugin registry

This repository hosts the canonical plugin registry consumed by every
[Openhpsdr-Zeus](https://github.com/brianbruff/openhpsdr-zeus) instance.
The Zeus client fetches `registry.json` over HTTPS on demand and lists
its entries in **Settings → Plugins → Browse**.

## Curator

Brian Keating (EI6LF). Will transfer to KB2UKA's GitHub account at a
later date.

## How operators use the registry

Each Zeus client points at:

```
https://raw.githubusercontent.com/brianbruff/openhpsdr-zeus-plugins/main/registry.json
```

The catalog is fetched on Settings → Plugins → Browse and cached for
five minutes. Operators install a registry plugin by clicking
**Install** on its card; Zeus downloads the zip, verifies the SHA-256,
and unpacks it under the platform plugin root.

Operators who don't want to wait for registry inclusion can use Zeus's
**Install from URL…** dialog — paste any HTTPS URL pointing at a Zeus
plugin zip with an optional SHA-256, and the Zeus host installs it
directly. The registry is optional, not the only path.

## How plugin authors get listed

1. Build a Zeus plugin per the [author guide](https://github.com/brianbruff/openhpsdr-zeus/blob/main/docs/plugins/author-guide.md).
   Your `plugin.json` must validate against `schema/manifest.schema.json`.

2. Publish a zip artifact to a stable HTTPS URL — typically a GitHub
   Release on your plugin's source repo. Compute `sha256sum
   yourplugin-1.2.3.zip` and note the digest.

3. Open a pull request against `registry.json` adding (or updating)
   an entry for your plugin under `plugins[]`. The shape is documented
   in `schema/registry.schema.json` and exercised by the validation
   workflow in `.github/workflows/validate.yml`.

4. CI runs:
   - JSON schema validation of `registry.json`
   - SHA-256 verification of every `downloadUrl` (the workflow
     downloads each zip and compares against the declared digest)
   - License/manifest sanity probe on the contained `plugin.json`

5. Once CI is green, the curator (Brian) merges the PR. The new
   entry is live to Zeus clients within minutes (raw.githubusercontent.com
   propagates quickly).

## Versioning

Bump SemVer per the
[plugin author guide](https://github.com/brianbruff/openhpsdr-zeus/blob/main/docs/plugins/author-guide.md#8-versioning-rules):

- **patch** — bug fix, operators auto-update
- **minor** — additive feature, operators see "Update available"
- **major** — breaking, operators must opt-in

The host's SDK ABI (`sdkAbi`) must match the operator's Zeus build.
At time of writing Zeus ships ABI 1 and SDK `1.0.0`; bumping either
requires a coordinated registry update.

## Schemas

- `schema/manifest.schema.json` — `plugin.json` shape (what's inside
  each plugin zip).
- `schema/registry.schema.json` — `registry.json` shape (what this
  repo serves).

Both are JSON Schema 2020-12. Run `npx ajv-cli validate` to check
locally.

## License

Curated content (`registry.json`, schemas, this README) is
[GPL-2.0-or-later](LICENSE), matching Zeus core. Plugins listed in
the registry carry whatever license their authors choose; we list
plugins under any FSF/OSI-approved license. Closed-source plugins
may be added at the curator's discretion provided they declare their
license honestly in `plugin.json`.

## Contact

Bug reports / questions: open an issue on this repo.
Curator: Brian Keating EI6LF — `b@briankeating.net`.

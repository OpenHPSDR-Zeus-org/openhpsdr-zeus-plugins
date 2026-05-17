# Sample plugins

Two reference implementations that demonstrate the Openhpsdr-Zeus
plugin contract. Read them before writing your own.

## What's here

- **`HelloWorld/`** — smallest possible plugin. A single C# file
  implementing `IZeusPlugin`, a plain `plugin.json`. Logs on
  activation and shutdown.

- **`Amplifier/`** — backend + UI sample. Implements `IZeusPlugin` +
  `IBackendPlugin`, exposes three HTTP endpoints under
  `/api/plugins/com.openhpsdr.zeus.samples.amplifier/`:
    - `GET status` — `{ powerWatts, swr, fault? }`
    - `POST power` — body `{ watts: 0..2000 }`
    - `POST reset` — clears the fault state
  In-memory synthetic state — substitute your hardware driver to
  ship a real plugin while keeping the contract identical. Frontend
  TSX in `ui/` shows the panel registration pattern.

## Building

The samples reference `Zeus.Plugins.Contracts` via a relative
`ProjectReference` that assumes [openhpsdr-zeus](https://github.com/Kb2uka/openhpsdr-zeus)
is cloned as a sibling directory:

```
parent/
├── openhpsdr-zeus/                # main Zeus repo
│   └── Zeus.Plugins.Contracts/
└── openhpsdr-zeus-plugins/        # this repo
    └── samples/
        └── HelloWorld/
```

```bash
git clone https://github.com/Kb2uka/openhpsdr-zeus
git clone https://github.com/Kb2uka/openhpsdr-zeus-plugins
cd openhpsdr-zeus-plugins/samples/HelloWorld
dotnet publish -c Release -o publish/
```

After publish, `publish/HelloWorld.dll` + `plugin.json` make a valid
plugin payload. To ship as a `.zip`:

```bash
cd publish && zip -r ../helloworld-1.0.0.zip HelloWorld.dll plugin.json
```

### NuGet (future)

Once `Openhpsdr.Zeus.Plugins.Contracts` is published to NuGet or
GitHub Packages, replace the `<ProjectReference>` in each sample's
csproj with `<PackageReference Include="Openhpsdr.Zeus.Plugins.Contracts"
Version="1.0.*" />`. The sibling-clone step goes away.

## Sideloading into Zeus

Drop the published `.zip` onto Zeus via **Settings → Plugins → Install
from file…**, or POST `/api/plugins/install` with `source: "file"`.
For URL-installable: upload the `.zip` to a GitHub Release on your
plugin's repo, compute its SHA-256, and let operators install via
**Install from URL…** with the digest.

## Publishing to the registry

If you'd like a sample-derived plugin listed in `registry.json`, see
[CONTRIBUTING.md](../CONTRIBUTING.md) at the repo root.

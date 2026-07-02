#!/usr/bin/env bash
# Package the Zeus Digital plugin into the release zip.
#
# Builds/publishes Digital.csproj, stages exactly the payload the installer
# expects (plugin.json at the ZIP ROOT — wrapping it in a folder makes the
# in-app installer 400 silently), and zips it as com.kb2uka.digital-<ver>.zip.
#
# Usage: ./package-zip.sh            (from modes/Digital/)
# Output: com.kb2uka.digital-<ver>.zip next to this script, plus its sha256
#         for the registry.json entry.
set -euo pipefail

cd "$(dirname "$0")"

VERSION=$(python3 -c "import json; print(json.load(open('plugin.json'))['version'])")
ZIP="com.kb2uka.digital-${VERSION}.zip"
PUB=bin/publish
PKG=bin/pkg

dotnet publish Digital.csproj -c Release -o "$PUB"

rm -rf "$PKG" "$ZIP"
mkdir -p "$PKG"

# Payload: the plugin assembly + manifest + per-RID natives. Contracts is
# host-provided (Private=false) and must NOT ship; publish may still drop
# framework-satellite noise, so stage explicitly instead of zipping publish/.
cp "$PUB/Zeus.Digital.Plugin.dll" "$PKG/"
[ -f "$PUB/Zeus.Digital.Plugin.pdb" ] && cp "$PUB/Zeus.Digital.Plugin.pdb" "$PKG/" || true
cp plugin.json "$PKG/"
cp -R runtimes "$PKG/runtimes"

# plugin.json MUST be at the zip root (cd into the staging dir, not zip the dir).
(cd "$PKG" && zip -r -X "../../$ZIP" .)

echo
echo "Wrote $ZIP"
shasum -a 256 "$ZIP"

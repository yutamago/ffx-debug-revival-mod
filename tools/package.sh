#!/usr/bin/env bash
# SPDX-License-Identifier: MIT OR Apache-2.0
# Zips the Release build as mods/yuta.debugrevival/, ready to unpack into the Fahrenheit folder.
# usage: tools/package.sh <out.zip>   (after dotnet build -c Release)
set -euo pipefail
root=$(cd "$(dirname "$0")/.." && pwd)
out=$(realpath -m "${1:?usage: tools/package.sh <out.zip>}")
mod="$root/artifacts/deploy/rel/mods/yuta.debugrevival"
[ -f "$mod/yuta.debugrevival.dll" ] || { echo "build first: dotnet build -c Release ffx-debug-revival.slnx" >&2; exit 1; }
work=$(mktemp -d); trap 'rm -rf "$work"' EXIT
mkdir -p "$work/mods"
cp -r "$mod" "$work/mods/"
cp "$root/README.md" "$root/LICENSE-MIT" "$root/LICENSE-APACHE" "$root/THIRD-PARTY-NOTICES" "$work/mods/yuta.debugrevival/"
rm -f "$out"
(cd "$work" && python3 -I -m zipfile -c "$out" mods)
echo "$out"

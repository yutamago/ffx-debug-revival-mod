#!/usr/bin/env bash
# SPDX-License-Identifier: MIT OR Apache-2.0
# Compares the mod's disassembler with ffx-decomp's Rust one (`ffx script disasm`)
# over the shipped scripts. Needs FFX_GAME_DIR and FFX_CLI (path to the `ffx` binary).
# usage: tools/disasm-check.sh [max scripts]
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
: "${FFX_GAME_DIR:?set FFX_GAME_DIR}" "${FFX_CLI:?set FFX_CLI to the ffx binary of ffx-decomp}"
work=$(mktemp -d); trap 'rm -rf "$work"' EXIT
dotnet build -c Release -v q -nologo "$here/DisasmCheck" -o "$work/bin" >/dev/null
archive="$FFX_GAME_DIR/data/FFX_Data.vbf"
"$FFX_CLI" script list --game-dir "$FFX_GAME_DIR" | awk '{print $NF}' | grep -E '\.(ebp|bin)$' > "$work/list" || true
n=0; bad=0
while read -r path; do
    [ -n "${1:-}" ] && [ "$n" -ge "$1" ] && break
    n=$((n + 1))
    "$FFX_CLI" vbf cat "$archive" "$path" > "$work/file" 2>/dev/null || { echo "skip (cat): $path"; continue; }
    if ! "$FFX_CLI" script disasm --game-dir "$FFX_GAME_DIR" "$path" > "$work/rust" 2>"$work/rust.err"; then
        echo "skip (rust failed): $path"; continue
    fi
    "$work/bin/DisasmCheck" "$work/file" "$path" "$here/../src/Script/natives.tsv" > "$work/cs" 2>&1 || true
    # Native names depend on which ffx.sym each side was built from; compare the handler addresses.
    for f in rust cs; do sed -E 's/; [A-Za-z_][A-Za-z0-9_]* (0x[0-9a-f]{8})$/; \1/' "$work/$f" > "$work/$f.n"; done
    if ! cmp -s "$work/rust.n" "$work/cs.n"; then
        bad=$((bad + 1)); echo "DIFF: $path"; diff "$work/rust.n" "$work/cs.n" | head -6 || true
    fi
done < "$work/list"
echo "$n scripts, $bad differ"
[ "$bad" -eq 0 ]

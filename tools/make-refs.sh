#!/usr/bin/env bash
# Regenerate lib/interop from the interop assemblies BepInEx generated in a game install
# (run the game once with BepInEx first). Keeps the same set of assemblies that is there now.
set -euo pipefail
source "$(dirname "$0")/env.sh"
interop="$GAME_DIR/BepInEx/interop"
[ -d "$interop" ] || { echo "No $interop - start the game once with BepInEx installed." >&2; exit 1; }
"$DOTNET" build "$REPO_DIR/tools/refstrip" -c Release -nologo -v q
names=$(cd "$REPO_DIR/lib/interop" && ls *.dll | sed 's/\.dll$//')
# shellcheck disable=SC2086
"$DOTNET" "$REPO_DIR/tools/refstrip/bin/Release/net8.0/RefStrip.dll" "$interop" "$REPO_DIR/lib/interop" $names

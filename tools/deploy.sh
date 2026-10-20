#!/usr/bin/env bash
# Build the plugin and copy it, with the files in game/, into the game folder.
# BepInEx itself has to be installed already (the installer does that).
set -euo pipefail
source "$(dirname "$0")/env.sh"
"$DOTNET" build "$REPO_DIR/src/ShroomVR/ShroomVR.csproj" -c Release -nologo -v q
dest="$GAME_DIR/BepInEx/plugins/ShroomVR"
mkdir -p "$dest"
cp "$REPO_DIR/src/ShroomVR/bin/Release/net6.0/ShroomVR.dll" "$dest/"
cp -r "$REPO_DIR/game/." "$GAME_DIR/"
echo "deployed to $dest"

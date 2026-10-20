#!/usr/bin/env bash
# Launch the game through Proton directly (Steam must be running), with BepInEx enabled.
# Extra arguments are passed to the game, e.g. tools/launch.sh -shroomvr-novr
set -euo pipefail
source "$(dirname "$0")/env.sh"
export STEAM_COMPAT_CLIENT_INSTALL_PATH="$STEAM_ROOT" STEAM_COMPAT_DATA_PATH="$COMPAT_DATA"
export SteamAppId=3271280 SteamGameId=3271280 WINEDLLOVERRIDES="winhttp=n,b"
cd "$GAME_DIR"
exec "$PROTON" run "$GAME_DIR/Shroom and Gloom.exe" "$@"

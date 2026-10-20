# Shared paths for the dev scripts. Override any of them in the environment.
: "${STEAM_ROOT:=$HOME/.local/share/Steam}"
if [ -z "${GAME_DIR:-}" ]; then
    while IFS= read -r lib; do
        if [ -f "$lib/steamapps/common/Shroom and Gloom/Shroom and Gloom.exe" ]; then
            GAME_DIR="$lib/steamapps/common/Shroom and Gloom"
            break
        fi
    done < <(sed -n 's/^[[:space:]]*"path"[[:space:]]*"\(.*\)"/\1/p' "$STEAM_ROOT/steamapps/libraryfolders.vdf" 2>/dev/null)
fi
: "${GAME_DIR:?Shroom and Gloom not found in your Steam libraries; set GAME_DIR}"
: "${COMPAT_DATA:=$(dirname "$(dirname "$GAME_DIR")")/compatdata/3271280}"
: "${PROTON:=$STEAM_ROOT/steamapps/common/Proton - Experimental/proton}"
: "${DOTNET:=$(command -v dotnet || echo "$HOME/.dotnet/dotnet")}"
REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PLAYER_LOG="$COMPAT_DATA/pfx/drive_c/users/steamuser/AppData/LocalLow/Team Lazerbeam/Shroom and Gloom/Player.log"

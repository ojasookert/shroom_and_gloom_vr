#!/usr/bin/env bash
# Stop the running game: ask it to quit (clean XR shutdown, window size restored), then force.
# The bracket keeps pgrep from matching this script itself.
running() { pgrep -f 'Shroom and Gloom\.ex[e]' >/dev/null; }
running || exit 0
source "$(dirname "$0")/env.sh"
cmd="$GAME_DIR/BepInEx/shroomvr_cmd.txt"
# A game already on its way out never reads the request; don't leave it for the next launch to obey.
cleanup() { [ "$(cat "$cmd" 2>/dev/null)" = quit ] && rm -f "$cmd"; true; }
printf 'quit\n' > "$cmd"
for _ in $(seq 1 30); do sleep 0.5; running || { cleanup; exit 0; }; done
pkill -f 'Shroom and Gloom\.ex[e]'; sleep 3
running && pkill -9 -f 'Shroom and Gloom\.ex[e]'
cleanup

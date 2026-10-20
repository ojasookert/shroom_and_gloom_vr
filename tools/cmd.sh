#!/usr/bin/env bash
# Send a DevConsole command to the running game and print the reply.
set -euo pipefail
source "$(dirname "$0")/env.sh"
out="$GAME_DIR/BepInEx/shroomvr_out.txt"
: > "$out"
printf '%s\n' "$*" > "$GAME_DIR/BepInEx/shroomvr_cmd.txt"
for _ in $(seq 1 40); do sleep 0.25; [ -s "$out" ] && { sleep 0.2; cat "$out"; exit 0; }; done
echo "(no reply)"; exit 1

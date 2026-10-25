#!/usr/bin/env bash
# Sends dev commands to the running game and shows the new log lines.
# Usage: [WAIT=2] ./cmd.sh "cmd 1" "cmd 2" ...
GAME="/mnt/c/Program Files (x86)/Steam/steamapps/common/StellarDrive Demo"
LOG="$GAME/MelonLoader/Latest.log"
before=$(wc -l < "$LOG")
printf '%s\n' "$@" > "$GAME/UserData/stellar_tractor_cmd.txt"
sleep "${WAIT:-2}"
tail -n +$((before+1)) "$LOG" | grep -aE "\[ParkingTractorBeam\]|Exception|rror" | cut -c1-400

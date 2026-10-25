#!/usr/bin/env bash
# Dev loop (WSL): build with DEV, deploy, (re)start the game and open a world (WORLD, default TB_Test, a fresh copy of
# SOURCE, default "Arsenal 2", unless KEEP=1).
set -e
export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1
GAME="/mnt/c/Program Files (x86)/Steam/steamapps/common/StellarDrive Demo"
SAVES="/mnt/c/Users/tidic/AppData/LocalLow/CuriousOwlGames/StellarDrive/WorldSaves"
LOG="$GAME/MelonLoader/Latest.log"
WORLD="${WORLD:-TB_Test}"
cd "$(dirname "$0")"
# Never close a game the player has open: only with FORCE=1 (once they have handed it over).
if tasklist.exe 2>/dev/null | grep -qi "StellarDrive.exe"; then
  if [ "${FORCE:-0}" != "1" ]; then echo "StellarDrive is running: close it or rerun with FORCE=1"; exit 2; fi
  taskkill.exe /IM StellarDrive.exe /F > /dev/null 2>&1 || true
fi
dotnet build src/ParkingTractorBeam/ParkingTractorBeam.csproj -c Release -p:DefineConstants=DEV -p:GamePath="$GAME" -nologo -v q 2>&1 | grep -E "rror CS" && exit 1
sleep 2
cp bin/Release/ParkingTractorBeam.dll "$GAME/Mods/"
# the mod used to be called Tractor Beam: both at once register the same parts twice and the world never loads
rm -f "$GAME/Mods/TractorBeam.dll"
if [ "$WORLD" = "TB_Test" ] && [ "${KEEP:-0}" != "1" ]; then
  rm -rf "$SAVES/TB_Test" && mkdir -p "$SAVES/TB_Test" && cp "$SAVES/${SOURCE:-Arsenal 2}/world.json" "$SAVES/TB_Test/"
  cp "$SAVES/${SOURCE:-Arsenal 2}"/*.txt "$SAVES/TB_Test/" 2>/dev/null || true
fi
rm -f "$LOG"
(cd "$GAME" && cmd.exe /c start "" StellarDrive.exe < /dev/null > /dev/null 2>&1 &)
for i in $(seq 1 120); do [ -f "$LOG" ] && grep -q "ParkingTractorBeam ready" "$LOG" && grep -q "Backup menus ready" "$LOG" && break; sleep 1; done
sleep 8
printf 'load %s\n' "$WORLD" > "$GAME/UserData/stellar_tractor_cmd.txt"
for i in $(seq 1 240); do grep -q "Network channel registered" "$LOG" && break; sleep 1; done
sleep 10
grep -aE "ParkingTractorBeam.*(Exception|rror|ready|Registered|unlocked)" "$LOG" | tail -8 || true
echo loaded

#!/usr/bin/env bash
# Builds the mod and packs the release zip in the layout StellarModManager expects:
#   dist/StellarDrive-ParkingTractorBeam.zip
#     mod.json
#     Thumbnail.png
#     Mods/ParkingTractorBeam.dll
set -e
export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1
GAME="${GAME:-/mnt/c/Program Files (x86)/Steam/steamapps/common/StellarDrive Demo}"
cd "$(dirname "$0")"
rm -rf bin src/ParkingTractorBeam/obj
dotnet build src/ParkingTractorBeam/ParkingTractorBeam.csproj -c Release -p:GamePath="$GAME" -nologo -v q "$@"
rm -rf dist/stage && mkdir -p dist/stage/Mods
cp bin/Release/ParkingTractorBeam.dll dist/stage/Mods/
cp Mods/mod.json Mods/Thumbnail.png dist/stage/
rm -f dist/StellarDrive-ParkingTractorBeam.zip
(cd dist/stage && python3 -c "import zipfile,os
z=zipfile.ZipFile('../StellarDrive-ParkingTractorBeam.zip','w',zipfile.ZIP_DEFLATED)
for r,_,fs in os.walk('.'):
    for f in fs:
        p=os.path.join(r,f); z.write(p,os.path.relpath(p,'.'))
z.close()")
rm -rf dist/stage
echo "Package: dist/StellarDrive-ParkingTractorBeam.zip"

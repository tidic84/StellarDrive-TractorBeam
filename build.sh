#!/usr/bin/env bash
# Builds the mod and packs the release zip in the layout StellarModManager expects:
#   dist/StellarDrive-TractorBeam.zip
#     mod.json
#     Thumbnail.png
#     Mods/TractorBeam.dll
set -e
export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1
GAME="${GAME:-/mnt/c/Program Files (x86)/Steam/steamapps/common/StellarDrive Demo}"
cd "$(dirname "$0")"
rm -rf bin src/TractorBeam/obj
dotnet build src/TractorBeam/TractorBeam.csproj -c Release -p:GamePath="$GAME" -nologo -v q "$@"
rm -rf dist/stage && mkdir -p dist/stage/Mods
cp bin/Release/TractorBeam.dll dist/stage/Mods/
cp Mods/mod.json Mods/Thumbnail.png dist/stage/
rm -f dist/StellarDrive-TractorBeam.zip
(cd dist/stage && python3 -c "import zipfile,os
z=zipfile.ZipFile('../StellarDrive-TractorBeam.zip','w',zipfile.ZIP_DEFLATED)
for r,_,fs in os.walk('.'):
    for f in fs:
        p=os.path.join(r,f); z.write(p,os.path.relpath(p,'.'))
z.close()")
rm -rf dist/stage
echo "Package: dist/StellarDrive-TractorBeam.zip"

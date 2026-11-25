#!/bin/sh
# Builds the Windows installer on macOS (or Linux): Windows/dist/CtrlLink-<version>-Setup-x64.exe (+ arm64).
# Everything stays local: packages restore into Windows/.packages, output goes to Windows/dist. Nothing is uploaded.
set -e
cd "$(dirname "$0")/.."
export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
command -v dotnet >/dev/null || { echo "dotnet not found in ~/.dotnet (see docs/BUILDING.md)"; exit 1; }

VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)
ICON=src/CtrlLink.App/Assets/icon.ico
dotnet tool restore >/dev/null
rm -rf .vpk dist; mkdir -p dist

for RID in win-x64 win-arm64; do
  echo "== $RID"
  dotnet publish src/CtrlLink.App -c Release -r "$RID" --self-contained -p:PublishReadyToRun=false -o ".vpk/publish-$RID" | grep -E "error|warn.*CS|->" || true
  ICONARG=""; [ -f "$ICON" ] && ICONARG="--icon $ICON"
  set -- ; [ -f .signing/codesign.p12 ] && set -- --signTemplate "$PWD/scripts/sign.sh {{file}}" --signParallel 1
  dotnet vpk "[win]" pack --packId CtrlLink --packVersion "$VERSION" --packTitle "Ctrl Link" --packAuthors "Md Erfan Chowdhury" \
    --packDir ".vpk/publish-$RID" --mainExe CtrlLink.exe --runtime "$RID" $ICONARG "$@" \
    --outputDir ".vpk/out-$RID" | grep -E "FTL|ERR|WRN.*sign|Done" || true
  SETUP=$(ls .vpk/out-$RID/*-Setup.exe)
  ARCH=${RID#win-}
  cp "$SETUP" "dist/CtrlLink-$VERSION-Setup-$ARCH.exe"
done
if [ -f .signing/Focotik-agency.cer ]; then
  cp .signing/Focotik-agency.cer scripts/Install-Certificate.cmd dist/
  echo "Signed as Focotik agency (self-signed). Run Install-Certificate.cmd once on the PC."
else
  echo "Not signed: Windows/.signing/codesign.p12 is missing."
fi
( cd dist && shasum -a 256 *.exe *.cer 2>/dev/null > SHA256SUMS.txt )
ls -lh dist

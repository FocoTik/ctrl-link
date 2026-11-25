#!/bin/sh
# Builds the unsigned Windows installer on macOS (or Linux): Windows/dist/CtrlLink-<version>-Setup-x64.exe (+ arm64).
# Unsigned on purpose: SmartScreen shows "Unknown publisher" until it is signed (SignPath, planned).
# Everything stays local: packages restore into Windows/.packages, output goes to Windows/dist. Nothing is uploaded.
set -e
cd "$(dirname "$0")/.."
export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
command -v dotnet >/dev/null || { echo "dotnet not found in ~/.dotnet (see docs/BUILDING.md)"; exit 1; }

VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)
ICON=src/CtrlLink.App/Assets/icon.ico
dotnet tool restore >/dev/null
rm -rf .vpk; mkdir -p dist
rm -f dist/*-Setup-*.exe dist/*.cer dist/Install-Certificate.cmd dist/SHA256SUMS.txt # keeps CtrlLink-build.zip

for RID in win-x64 win-arm64; do
  echo "== $RID"
  dotnet publish src/CtrlLink.App -c Release -r "$RID" --self-contained -p:PublishReadyToRun=false -o ".vpk/publish-$RID" | grep -E "error|warn.*CS|->" || true
  ICONARG=""; [ -f "$ICON" ] && ICONARG="--icon $ICON"
  dotnet vpk "[win]" pack --packId CtrlLink --packVersion "$VERSION" --packTitle "Ctrl Link" --packAuthors "Focotik agency" \
    --packDir ".vpk/publish-$RID" --mainExe CtrlLink.exe --runtime "$RID" $ICONARG \
    --outputDir ".vpk/out-$RID" | grep -E "FTL|ERR|Done" || true
  SETUP=$(ls .vpk/out-$RID/*-Setup.exe)
  ARCH=${RID#win-}
  cp "$SETUP" "dist/CtrlLink-$VERSION-Setup-$ARCH.exe"
done
( cd dist && shasum -a 256 *.exe > SHA256SUMS.txt )
ls -lh dist

#!/bin/sh
# Makes dist/CtrlLink-build.zip: the app source, the Windows .NET SDK and every library, plus build.bat.
# On the PC, build.bat builds Ctrl Link\CtrlLink.exe offline. Everything stays local; only libraries missing from
# Windows/.packages are fetched from nuget.org while making the zip.
set -e
cd "$(dirname "$0")/.."
W=$PWD
export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
SDKVER=$(dotnet --version)
SDKZIP=.tools/dotnet-sdk-$SDKVER-win-x64.zip
[ -f "$SDKZIP" ] || { echo "Missing $SDKZIP. Download it from https://builds.dotnet.microsoft.com/dotnet/Sdk/$SDKVER/dotnet-sdk-$SDKVER-win-x64.zip"; exit 1; }

K=.buildkit/CtrlLink-build
rm -rf .buildkit
mkdir -p "$K/Windows/src" "$K/design/fonts"
for p in CtrlLink.Core CtrlLink.App; do rsync -a --exclude bin --exclude obj --exclude '*.py' "src/$p" "$K/Windows/src/"; done
cp Directory.Build.props "$K/Windows/"
cp ../design/fonts/Newsreader-Variable.ttf ../design/fonts/OFL.txt "$K/design/fonts/"
cp scripts/buildkit/NuGet.config "$K/"
for f in build.bat uninstall.cmd README.txt; do perl -pe 's/\r?\n/\r\n/' "scripts/buildkit/$f" > "$K/$f"; done

APP="$K/Windows/src/CtrlLink.App"
PUB="publish $APP -c Release -r win-x64 --self-contained -p:PublishReadyToRun=false -o .buildkit/out"
echo "== collecting libraries"
dotnet $PUB --source "$W/.packages" --source https://api.nuget.org/v3/index.json | grep -E "error|->" || true
echo "== dropping web, Linux and macOS native copies (a Windows build never reads them)"
for d in "$K"/packages/*nativeassets.webassembly/* "$K"/packages/*nativeassets.linux/* "$K"/packages/*nativeassets.macos/*; do
  find "$d" -mindepth 1 -maxdepth 1 ! -name "*.nupkg.metadata" ! -name "*.sha512" ! -name "*.nuspec" -exec rm -rf {} +
done
echo "== checking the build works with no package source"
find "$K/Windows" -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +
rm -rf .buildkit/out "$K/packages"/*/*/*.nupkg
dotnet $PUB | grep -E "error|->"
for f in CtrlLink.exe libSkiaSharp.dll libHarfBuzzSharp.dll av_libglesv2.dll; do [ -f ".buildkit/out/$f" ] || { echo "offline build is missing $f"; exit 1; }; done
ls .buildkit/out | wc -l; [ -f .buildkit/out/CtrlLink.exe ] || { echo "offline build failed"; exit 1; }
find "$K/Windows" -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +
rm -rf .buildkit/out

echo "== adding the .NET $SDKVER SDK for Windows x64"
unzip -q "$SDKZIP" -d "$K/sdk"

mkdir -p dist
rm -f dist/CtrlLink-build.zip
( cd .buildkit && zip -qr -9 -X ../dist/CtrlLink-build.zip CtrlLink-build )
rm -rf .buildkit
ls -lh dist/CtrlLink-build.zip

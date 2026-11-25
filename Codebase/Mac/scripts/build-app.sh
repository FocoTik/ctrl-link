#!/bin/sh
# Builds release and wraps it into "build/Ctrl Link.app" (ad-hoc signed). Works with only the Command Line Tools.
set -eu
cd "$(dirname "$0")/.."
swift build -c release --product CtrlLink
BIN="$(swift build -c release --show-bin-path)/CtrlLink"
APP="build/Ctrl Link.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS"
cp "$BIN" "$APP/Contents/MacOS/CtrlLink"
mkdir -p "$APP/Contents/Resources"
cp ../design/fonts/Newsreader-Variable.ttf ../design/fonts/OFL.txt "$APP/Contents/Resources/"
# App icon from the current mark (design/logo/options/6-merged.png, full-bleed rounded square), inset to the macOS icon grid
# (824 of 1024) so it sits the same size as other apps. Built into the bundle only; nothing is added to the sources.
ICON_SRC=../design/logo/options/6-merged.png
if [ -f "$ICON_SRC" ]; then
  SET=$(mktemp -d)/AppIcon.iconset; mkdir -p "$SET"
  sips -Z 824 "$ICON_SRC" --out "$SET/base.png" >/dev/null && sips -p 1024 1024 "$SET/base.png" --out "$SET/base.png" >/dev/null
  for s in 16 32 128 256 512; do
    sips -z $s $s "$SET/base.png" --out "$SET/icon_${s}x${s}.png" >/dev/null
    sips -z $((s*2)) $((s*2)) "$SET/base.png" --out "$SET/icon_${s}x${s}@2x.png" >/dev/null
  done
  rm "$SET/base.png"
  iconutil -c icns "$SET" -o "$APP/Contents/Resources/AppIcon.icns"
fi
cat > "$APP/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
	<key>CFBundleIdentifier</key><string>com.ctrllink.mac</string>
	<key>CFBundleName</key><string>Ctrl Link</string>
	<key>CFBundleDisplayName</key><string>Ctrl Link</string>
	<key>CFBundleExecutable</key><string>CtrlLink</string>
	<key>CFBundleIconFile</key><string>AppIcon</string>
	<key>CFBundlePackageType</key><string>APPL</string>
	<key>CFBundleShortVersionString</key><string>1.0</string>
	<key>CFBundleVersion</key><string>1</string>
	<key>CFBundleURLTypes</key><array><dict>
		<key>CFBundleURLName</key><string>com.ctrllink.mac.pair</string>
		<key>CFBundleURLSchemes</key><array><string>ctrllink</string></array>
	</dict></array>
	<key>LSMinimumSystemVersion</key><string>14.0</string>
	<key>LSUIElement</key><true/>
	<key>NSHighResolutionCapable</key><true/>
	<key>NSLocalNetworkUsageDescription</key><string>Ctrl Link finds your paired devices on this network and shares your clipboard with them.</string>
	<key>NSBonjourServices</key><array><string>_ctrllink._tcp</string></array>
</dict>
</plist>
PLIST
codesign --force -s - "$APP"
codesign --verify "$APP" && echo "built $APP"

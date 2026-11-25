#!/bin/sh
# One-time setup so Ctrl Link on Android can send text you copy in any app (READ_LOGS + display over other apps).
set -e
ADB="$HOME/Library/Android/sdk/platform-tools/adb"
PKG=com.ctrllink.android
[ -x "$ADB" ] || { echo "adb not found at $ADB"; exit 1; }
n=$("$ADB" devices | awk 'NR>1 && $2=="device"' | wc -l | tr -d ' ')
if [ "$n" != "1" ]; then
  echo "Expected exactly one phone attached over USB, found $n."
  echo "Plug in the phone, turn on USB debugging, accept the prompt on the phone, then run this again."
  "$ADB" devices
  exit 1
fi
"$ADB" shell pm list packages | grep -q "package:$PKG\$" || { echo "Ctrl Link isn't installed on the phone."; exit 1; }
"$ADB" shell pm grant $PKG android.permission.READ_LOGS 2>/dev/null || {
  echo "The phone refused the permission."
  echo "On Xiaomi/Redmi/POCO: Developer options > turn on 'USB debugging (Security settings)', unplug and replug, then run this again."
  echo "Also make sure the latest Ctrl Link APK is installed."
  exit 1
}
"$ADB" shell appops set $PKG SYSTEM_ALERT_WINDOW allow
"$ADB" shell am force-stop $PKG
"$ADB" shell am start -n $PKG/.MainActivity >/dev/null
echo "Done. Ctrl Link restarted with log access and permission to display over other apps."
echo "Android 13+ now asks \"Allow Ctrl Link to access all device logs?\" on the phone (again after each restart). Tap Allow / Allow one-time access."
echo "Then copy text in any app: a paired, connected Mac should get it within a second."

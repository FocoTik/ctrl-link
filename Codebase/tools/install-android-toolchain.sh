#!/bin/zsh
# Installs JDK 21, Gradle and the Android SDK into the user's home folder (no sudo).
# Everything installed is listed in docs/BUILDING.md (toolchains section).
set -euo pipefail

JDK_URL="https://github.com/adoptium/temurin21-binaries/releases/download/jdk-21.0.13%2B9/OpenJDK21U-jdk_aarch64_mac_hotspot_21.0.13_9.tar.gz"
GRADLE_VER="8.11.1"
CLT_ZIP="commandlinetools-mac-9862592_latest.zip"

JDK_DIR="$HOME/.jdks/temurin-21"
GRADLE_DIR="$HOME/.local/gradle-$GRADLE_VER"
SDK="$HOME/Library/Android/sdk"
DL="$(mktemp -d)"

mkdir -p "$HOME/.jdks" "$HOME/.local" "$SDK/cmdline-tools"

echo "==> JDK 21"
[ -x "$JDK_DIR/Contents/Home/bin/java" ] || { curl -fsSL "$JDK_URL" -o "$DL/jdk.tgz"
mkdir -p "$JDK_DIR" && tar -xzf "$DL/jdk.tgz" -C "$JDK_DIR" --strip-components=1; }
export JAVA_HOME="$JDK_DIR/Contents/Home"
"$JAVA_HOME/bin/java" -version

echo "==> Gradle $GRADLE_VER"
[ -d "$GRADLE_DIR" ] || { curl -fsSL "https://services.gradle.org/distributions/gradle-$GRADLE_VER-bin.zip" -o "$DL/gradle.zip"
unzip -q "$DL/gradle.zip" -d "$HOME/.local"; }

echo "==> Android cmdline-tools"
[ -x "$SDK/cmdline-tools/latest/bin/sdkmanager" ] || { curl -fsSL "https://dl.google.com/android/repository/$CLT_ZIP" -o "$DL/clt.zip"
unzip -q "$DL/clt.zip" -d "$DL/clt"
rm -rf "$SDK/cmdline-tools/latest" && mv "$DL/clt/cmdline-tools" "$SDK/cmdline-tools/latest"; }

SM="$SDK/cmdline-tools/latest/bin/sdkmanager"
(yes || true) | "$SM" --sdk_root="$SDK" --licenses >/dev/null || true
"$SM" --sdk_root="$SDK" "platform-tools" "platforms;android-35" "build-tools;35.0.0" \
  "emulator" "system-images;android-35;google_apis;arm64-v8a"

echo "==> Emulator AVD"
(echo no || true) | "$SDK/cmdline-tools/latest/bin/avdmanager" create avd -n clipsync_test \
  -k "system-images;android-35;google_apis;arm64-v8a" -d pixel_4 --force

rm -rf "$DL"
echo "DONE"

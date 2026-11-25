#!/bin/sh
# Authenticode-signs one Windows file with the local self-signed "Focotik agency" certificate (Windows/.signing, never committed).
# Called by Velopack for every exe/dll it ships and for Setup.exe. No timestamp server is contacted: everything stays local.
set -e
W="$(cd "$(dirname "$0")/.." && pwd)"
JAVA="${JAVA:-$HOME/.jdks/temurin-21/Contents/Home/bin/java}"
exec "$JAVA" -jar "$W/.tools/jsign-7.6.jar" --storetype PKCS12 --keystore "$W/.signing/codesign.p12" \
  --storepass "$(cat "$W/.signing/password.txt")" --alias clipsync --alg SHA-256 \
  --name "Ctrl Link" --replace "$@"

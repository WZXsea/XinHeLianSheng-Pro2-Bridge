#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
BUILD_ARGS=(-c release)
if [[ "${1:-}" == "--universal" ]]; then BUILD_ARGS+=(--arch arm64 --arch x86_64); fi
swift build --package-path "$ROOT" "${BUILD_ARGS[@]}"
BIN="$(swift build --package-path "$ROOT" "${BUILD_ARGS[@]}" --show-bin-path)"
APP="$ROOT/dist/PRO2 手柄助手.app"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN/Pro2Mac" "$APP/Contents/MacOS/Pro2Mac"
cp "$BIN/pro2-diagnostics" "$ROOT/dist/pro2-diagnostics"
cp "$ROOT/Info.plist" "$APP/Contents/Info.plist"
cp "$ROOT/../LICENSE" "$APP/Contents/Resources/LICENSE"
cp "$ROOT/../ACKNOWLEDGEMENTS.md" "$APP/Contents/Resources/ACKNOWLEDGEMENTS.md"
codesign --force --sign - "$APP"
codesign --verify --strict "$APP"
ditto -c -k --keepParent "$APP" "$ROOT/dist/PRO2-Mac-0.1.0.zip"
printf 'App: %s\nArchive: %s\n' "$APP" "$ROOT/dist/PRO2-Mac-0.1.0.zip"

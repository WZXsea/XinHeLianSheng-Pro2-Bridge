#!/bin/bash
# Build our optional C ABI shim against the pinned MIT public headers.
# The official universal MIT static client is embedded in this optional shim.
# The broker remains a separate official installation with explicit license activation.
set -euo pipefail
BROKER_MACOS_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
BROKER_OUTPUT_DIR="${1:-$BROKER_MACOS_ROOT/Vendor/libvirtualhid/lib}"
mkdir -p "$BROKER_OUTPUT_DIR"
clang++ -std=c++20 -dynamiclib \
    -arch arm64 -arch x86_64 -mmacosx-version-min=14.2 \
    -I "$BROKER_MACOS_ROOT/Vendor/libvirtualhid/include" \
    -I "$BROKER_MACOS_ROOT/Sources/CPro2Broker/include" \
    "$BROKER_MACOS_ROOT/Sources/CPro2Broker/Shim/Pro2BrokerShim.cpp" \
    "$BROKER_MACOS_ROOT/Vendor/libvirtualhid/lib/libvirtualhid.a" \
    -framework ApplicationServices -framework CoreFoundation -framework IOKit \
    -install_name @rpath/libPro2BrokerShim.dylib \
    -o "$BROKER_OUTPUT_DIR/libPro2BrokerShim.dylib"
codesign --force --sign - "$BROKER_OUTPUT_DIR/libPro2BrokerShim.dylib"
printf 'Built %s\n' "$BROKER_OUTPUT_DIR/libPro2BrokerShim.dylib"

#!/bin/bash
set -euo pipefail
PRO2_ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
if [[ "${1:-}" == --universal ]]; then
    bash "$PRO2_ROOT/scripts/build-desktop.sh" osx-arm64
    bash "$PRO2_ROOT/scripts/build-desktop.sh" osx-x64
else
    PRO2_ARCH="$(uname -m)"
    [[ "$PRO2_ARCH" == x86_64 ]] && PRO2_ARCH=x64
    exec bash "$PRO2_ROOT/scripts/build-desktop.sh" "osx-$PRO2_ARCH"
fi

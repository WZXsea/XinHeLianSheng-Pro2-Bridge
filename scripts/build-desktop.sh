#!/bin/bash
set -euo pipefail
PRO2_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PRO2_DOTNET="${PRO2_DOTNET:-$(command -v dotnet || true)}"
if [[ -z "$PRO2_DOTNET" ]]; then PRO2_DOTNET="$HOME/.local/share/pro2-dotnet/dotnet"; fi
PRO2_RID="${1:-osx-arm64}"
case "$PRO2_RID" in osx-arm64|osx-x64|win-x64) ;; *) printf 'Supported targets: osx-arm64, osx-x64, win-x64\n' >&2; exit 2;; esac
PRO2_PUBLISH="$PRO2_ROOT/artifacts/desktop/$PRO2_RID/publish"
PRO2_VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$PRO2_ROOT/desktop/Directory.Build.props")"
if [[ "${GITHUB_REF_TYPE:-}" == tag && "${GITHUB_REF_NAME:-}" != "v$PRO2_VERSION" ]]; then
    printf 'Version tag must match desktop/Directory.Build.props: v%s\n' "$PRO2_VERSION" >&2; exit 2
fi
rm -rf "$PRO2_PUBLISH"
"$PRO2_DOTNET" publish "$PRO2_ROOT/desktop/Pro2.Desktop/Pro2.Desktop.csproj" -c Release -r "$PRO2_RID" --self-contained true -o "$PRO2_PUBLISH" --nologo
if [[ "$PRO2_RID" == osx-* ]]; then
    PRO2_ARCH="${PRO2_RID#osx-}"; [[ "$PRO2_ARCH" == x64 ]] && PRO2_ARCH=x86_64
    swift build --package-path "$PRO2_ROOT/macos" -c release --arch "$PRO2_ARCH" --product pro2-native-host
    PRO2_NATIVE_BIN="$(swift build --package-path "$PRO2_ROOT/macos" -c release --arch "$PRO2_ARCH" --product pro2-native-host --show-bin-path)"
    PRO2_APP="$PRO2_ROOT/artifacts/desktop/$PRO2_RID/PRO2 手柄助手.app"
    rm -rf "$PRO2_APP"
    mkdir -p "$PRO2_APP/Contents/MacOS" "$PRO2_APP/Contents/Helpers" "$PRO2_APP/Contents/Frameworks" "$PRO2_APP/Contents/Resources"
    ditto "$PRO2_PUBLISH" "$PRO2_APP/Contents/MacOS"
    cp "$PRO2_NATIVE_BIN/pro2-native-host" "$PRO2_APP/Contents/Helpers/pro2-native-host"
    bash "$PRO2_ROOT/macos/scripts/build-broker-shim.sh" "$PRO2_APP/Contents/Frameworks"
    PRO2_ICONSET="$PRO2_ROOT/artifacts/desktop/$PRO2_RID/AppIcon.iconset"
    mkdir -p "$PRO2_ICONSET"
    for PRO2_ICON_SIZE in 16 32 128 256 512; do
        sips -z "$PRO2_ICON_SIZE" "$PRO2_ICON_SIZE" "$PRO2_ROOT/source/final-three-in-one/windows/v60_viiper_app/Assets/AppIcon-v2.png" --out "$PRO2_ICONSET/icon_${PRO2_ICON_SIZE}x${PRO2_ICON_SIZE}.png" >/dev/null
        PRO2_ICON_RETINA=$((PRO2_ICON_SIZE*2))
        sips -z "$PRO2_ICON_RETINA" "$PRO2_ICON_RETINA" "$PRO2_ROOT/source/final-three-in-one/windows/v60_viiper_app/Assets/AppIcon-v2.png" --out "$PRO2_ICONSET/icon_${PRO2_ICON_SIZE}x${PRO2_ICON_SIZE}@2x.png" >/dev/null
    done
    iconutil -c icns "$PRO2_ICONSET" -o "$PRO2_APP/Contents/Resources/AppIcon.icns"
    python3 - "$PRO2_ROOT/macos/Info.plist" "$PRO2_APP/Contents/Info.plist" "$PRO2_VERSION" <<'PY'
import plistlib,sys
from pathlib import Path
value=plistlib.loads(Path(sys.argv[1]).read_bytes())
value['CFBundleExecutable']='PRO2ControllerAssistant'
value['CFBundleShortVersionString']=sys.argv[3].split('-')[0]
value['CFBundleVersion']=sys.argv[3].split('-')[0]
value['CFBundleIconFile']='AppIcon.icns'
Path(sys.argv[2]).write_bytes(plistlib.dumps(value))
PY
    cp "$PRO2_ROOT/LICENSE" "$PRO2_APP/Contents/Resources/LICENSE"
    cp "$PRO2_ROOT/ACKNOWLEDGEMENTS.md" "$PRO2_APP/Contents/Resources/ACKNOWLEDGEMENTS.md"
    ditto "$PRO2_ROOT/macos/Vendor/libvirtualhid/licenses" "$PRO2_APP/Contents/Resources/licenses/libvirtualhid"
    mkdir -p "$PRO2_APP/Contents/Resources/licenses/fonts"
    cp "$PRO2_ROOT/source/final-three-in-one/windows/v60_viiper_app/Assets/Fonts/OFL.txt" "$PRO2_APP/Contents/Resources/licenses/fonts/OFL.txt"
    codesign --force --deep --sign - "$PRO2_APP"
    codesign --verify --deep --strict "$PRO2_APP"
    ditto -c -k --keepParent "$PRO2_APP" "$PRO2_ROOT/artifacts/desktop/PRO2-$PRO2_VERSION-$PRO2_RID.zip"
    printf '\nApp: %s\n' "$PRO2_APP"
elif [[ "$PRO2_RID" == win-* ]]; then
    "$PRO2_DOTNET" publish "$PRO2_ROOT/desktop/Pro2.Platform.Windows/Pro2.Platform.Windows.csproj" -c Release -r "$PRO2_RID" --self-contained true -p:PureSoftwareRelease=true -p:EnableWindowsTargeting=true -o "$PRO2_PUBLISH/native" --nologo
    cp "$PRO2_ROOT/LICENSE" "$PRO2_PUBLISH/LICENSE"
    cp "$PRO2_ROOT/ACKNOWLEDGEMENTS.md" "$PRO2_PUBLISH/ACKNOWLEDGEMENTS.md"
    mkdir -p "$PRO2_PUBLISH/licenses/fonts"
    cp "$PRO2_ROOT/source/final-three-in-one/windows/v60_viiper_app/Assets/Fonts/OFL.txt" "$PRO2_PUBLISH/licenses/fonts/OFL.txt"
    python3 - "$PRO2_PUBLISH" "$PRO2_ROOT/artifacts/desktop/PRO2-$PRO2_VERSION-$PRO2_RID.zip" <<'PY'
from pathlib import Path
from zipfile import ZipFile,ZIP_DEFLATED
import sys
root=Path(sys.argv[1])
with ZipFile(sys.argv[2],'w',ZIP_DEFLATED) as archive:
    for path in sorted(root.rglob('*')):
        if path.is_file(): archive.write(path,path.relative_to(root))
PY
    printf '\nApp: %s\n' "$PRO2_PUBLISH/PRO2ControllerAssistant.exe"
fi
python3 - "$PRO2_ROOT/artifacts/desktop/PRO2-$PRO2_VERSION-$PRO2_RID.zip" <<'PY'
from pathlib import Path
import hashlib,sys
archive=Path(sys.argv[1])
digest=hashlib.sha256()
with archive.open('rb') as stream:
    for block in iter(lambda:stream.read(1024*1024),b''): digest.update(block)
Path(str(archive)+'.sha256').write_text(digest.hexdigest()+'  '+archive.name+'\n')
PY

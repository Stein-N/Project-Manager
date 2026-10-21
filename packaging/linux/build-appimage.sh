#!/usr/bin/env bash
# Builds the AppImage from the published program file (a self-contained .NET program needs no bundled libraries).
#
# usage: build-appimage.sh <program file> <version> <output folder>
set -euo pipefail
program=$1
version=$2
out=$(realpath -m "$3")
here=$(cd "$(dirname "$0")" && pwd)
icons=$here/../icons
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
app=$work/AppDir

install -Dm755 "$program" "$app/usr/bin/project-manager"
install -Dm644 "$here/project-manager.desktop" "$app/project-manager.desktop"
install -Dm644 "$here/project-manager.desktop" "$app/usr/share/applications/project-manager.desktop"
install -Dm644 "$icons/128x128@2x.png" "$app/project-manager.png"
install -Dm644 "$icons/128x128@2x.png" "$app/usr/share/icons/hicolor/256x256/apps/project-manager.png"
cat > "$app/AppRun" <<'RUN'
#!/bin/sh
here=$(dirname "$(readlink -f "$0")")
exec "$here/usr/bin/project-manager" "$@"
RUN
chmod +x "$app/AppRun"

tool=${APPIMAGETOOL:-$work/appimagetool}
if [ ! -x "$tool" ]; then
  curl -fsSL -o "$tool" https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage
  chmod +x "$tool"
fi
mkdir -p "$out"
# --appimage-extract-and-run: no FUSE needed on the build machine
ARCH=x86_64 "$tool" --appimage-extract-and-run "$app" "$out/MC Mod Project Manager_${version}_amd64.AppImage"

#!/usr/bin/env bash
# Builds the deb package from the published program file – the same package name, paths and desktop file as the
# Tauri package of the Rust versions, so installing it (also through the built-in updater) replaces them cleanly.
#
# usage: build-deb.sh <program file> <version> <output folder>
set -euo pipefail
program=$1
version=$2
out=$3
here=$(cd "$(dirname "$0")" && pwd)
icons=$here/../icons
root=$(mktemp -d)
chmod 755 "$root"
trap 'rm -rf "$root"' EXIT

install -Dm755 "$program" "$root/usr/bin/project-manager"
install -Dm644 "$here/project-manager.desktop" "$root/usr/share/applications/MC Mod Project Manager.desktop"
install -Dm644 "$icons/32x32.png" "$root/usr/share/icons/hicolor/32x32/apps/project-manager.png"
install -Dm644 "$icons/128x128.png" "$root/usr/share/icons/hicolor/128x128/apps/project-manager.png"
install -Dm644 "$icons/128x128@2x.png" "$root/usr/share/icons/hicolor/256x256/apps/project-manager.png"
install -Dm644 "$icons/icon.png" "$root/usr/share/icons/hicolor/512x512/apps/project-manager.png"

size=$(du -sk "$root/usr" | cut -f1)
mkdir -p "$root/DEBIAN"
cat > "$root/DEBIAN/control" <<CONTROL
Package: mc-mod-project-manager
Version: $version
Architecture: amd64
Installed-Size: $size
Maintainer: morthen
Priority: optional
Section: devel
Depends: libc6, libgcc-s1, libstdc++6, libssl3 | libssl1.1, libx11-6, libice6, libsm6, libfontconfig1
Description: Manage Minecraft mod projects, versions and backports
 Git worktrees, Gradle tasks, dependency updates, changelogs and backports between
 the Minecraft versions of a mod.
CONTROL
(cd "$root" && find usr -type f -exec md5sum {} + > DEBIAN/md5sums)

mkdir -p "$out"
dpkg-deb --root-owner-group --build "$root" "$out/MC Mod Project Manager_${version}_amd64.deb"

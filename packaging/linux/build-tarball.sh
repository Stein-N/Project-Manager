#!/usr/bin/env bash
# Builds the tar.gz archive from the published program file, for distributions without dpkg (Arch, Fedora …).
# The archive is laid out like an install prefix (bin/, share/), so it is unpacked straight into one:
#   tar -xzf <archive> -C ~/.local            (for the user, updates without a password)
#   sudo tar -xzf <archive> -C /usr/local     (system-wide, updates ask for the password via pkexec)
#
# usage: build-tarball.sh <program file> <version> <output folder>
set -euo pipefail
program=$1
version=$2
out=$(realpath -m "$3")
here=$(cd "$(dirname "$0")" && pwd)
icons=$here/../icons
root=$(mktemp -d)
trap 'rm -rf "$root"' EXIT

install -Dm755 "$program" "$root/bin/project-manager"
# same file name as the entry the app writes itself, so the two never show up as separate launchers
install -Dm644 "$here/project-manager.desktop" "$root/share/applications/project-manager.desktop"
install -Dm644 "$icons/32x32.png" "$root/share/icons/hicolor/32x32/apps/project-manager.png"
install -Dm644 "$icons/128x128.png" "$root/share/icons/hicolor/128x128/apps/project-manager.png"
install -Dm644 "$icons/128x128@2x.png" "$root/share/icons/hicolor/256x256/apps/project-manager.png"
install -Dm644 "$icons/icon.png" "$root/share/icons/hicolor/512x512/apps/project-manager.png"

mkdir -p "$out"
tar --owner=0 --group=0 --numeric-owner --mode='u+rwX,go+rX,go-w' -czf "$out/MC Mod Project Manager_${version}_x86_64.tar.gz" -C "$root" bin share

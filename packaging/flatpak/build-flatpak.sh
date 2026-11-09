#!/usr/bin/env bash
# Builds the Flatpak from the published program file:
#   <site folder>    what GitHub Pages serves: the Flatpak repository (repo/), the .flatpakref to install from it, the
#                    .flatpakrepo to only add it, and a page with the commands. Every build starts a new repository with
#                    only this version – the app updates with flatpak update.
#   <output folder>  the single-file bundle for the release (installing it adds the repository for updates as well)
# Needs flatpak and flatpak-builder with org.freedesktop.Platform and org.freedesktop.Sdk (the runtime-version of the
# manifest) installed from Flathub.
#
# usage: build-flatpak.sh <program file> <version> <site folder> <output folder>
#   FLATPAK_GPG_KEY  id or fingerprint of the key in the gpg keyring that signs the repository (unsigned without it –
#                    only for trying it out locally: flatpak then needs the remote added with --no-gpg-verify)
#   FLATPAK_URL      where the site folder is published (default: the GitHub Pages of the repository)
set -euo pipefail
program=$(realpath "$1")
version=$2
site=$(realpath -m "$3")
out=$(realpath -m "$4")
key=${FLATPAK_GPG_KEY:-}
url=${FLATPAK_URL:-https://stein-n.github.io/Project-Manager}
url=${url%/}
id=io.github.Stein_N.ProjectManager
name="MC Mod Project Manager"
flathub=https://dl.flathub.org/repo/flathub.flatpakrepo
here=$(cd "$(dirname "$0")" && pwd)
icons=$here/../icons
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

if [ -e "$site" ] && [ -n "$(ls -A "$site")" ]; then
  echo "error: $site is not empty" >&2
  exit 1
fi

# the manifest finds its files next to it
src=$work/src
mkdir -p "$src"
cp "$here/$id.yml" "$src/"
cp "$program" "$src/project-manager"
cp "$here/../linux/project-manager.desktop" "$src/"
cp "$icons/32x32.png" "$src/32x32.png"
cp "$icons/128x128.png" "$src/128x128.png"
cp "$icons/128x128@2x.png" "$src/256x256.png"
cp "$icons/icon.png" "$src/512x512.png"
sed -e "s/@VERSION@/$version/" -e "s/@DATE@/$(date -u +%F)/" "$here/$id.metainfo.xml" > "$src/$id.metainfo.xml"

sign=()
gpgkey=
if [ -n "$key" ]; then
  sign=(--gpg-sign="$key")
  gpg --export "$key" > "$work/key.gpg"
  gpgkey=$(base64 -w0 < "$work/key.gpg")
fi

repo=$site/repo
mkdir -p "$site" "$out"
# --disable-rofiles-fuse: no FUSE in containers
flatpak-builder --disable-rofiles-fuse --force-clean --default-branch=stable --state-dir="$work/state" \
  --repo="$repo" "${sign[@]}" "$work/build" "$src/$id.yml"
flatpak build-update-repo "${sign[@]}" --title="$name" --default-branch=stable --generate-static-deltas --prune "$repo"

flatpak build-bundle --runtime-repo="$flathub" --repo-url="$url/repo/" ${gpgkey:+--gpg-keys="$work/key.gpg"} \
  "$repo" "$out/${name}_${version}_x86_64.flatpak" "$id" stable

cat > "$site/project-manager.flatpakref" <<REF
[Flatpak Ref]
Name=$id
Branch=stable
Title=$name
Url=$url/repo/
SuggestRemoteName=project-manager
RuntimeRepo=$flathub
IsRuntime=false
Homepage=https://github.com/Stein-N/Project-Manager
${gpgkey:+GPGKey=$gpgkey}
REF

cat > "$site/project-manager.flatpakrepo" <<REPO
[Flatpak Repo]
Title=$name
Url=$url/repo/
Homepage=https://github.com/Stein-N/Project-Manager
Comment=Flatpak repository of $name
${gpgkey:+GPGKey=$gpgkey}
REPO

cat > "$site/index.html" <<HTML
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>$name – Flatpak</title>
<style>
  :root { color-scheme: light dark; --bg: #ffffff; --fg: #1f2328; --code: #f3f4f6; }
  @media (prefers-color-scheme: dark) { :root { --bg: #1e1f22; --fg: #dfe1e5; --code: #2b2d30; } }
  body { background: var(--bg); color: var(--fg); font: 16px/1.5 system-ui, sans-serif; max-width: 46rem; margin: 2rem auto; padding: 0 16px; }
  pre, code { background: var(--code); border-radius: 4px; }
  pre { padding: .75rem; overflow-x: auto; }
  code { padding: 0 .25rem; }
  a { color: inherit; }
</style>
</head>
<body>
<h1>$name</h1>
<p>Flatpak repository of <a href="https://github.com/Stein-N/Project-Manager">$name</a> – version $version.</p>
<pre>flatpak install --user $url/project-manager.flatpakref</pre>
<p>Updates come with <code>flatpak update</code>, the software center or the update dialog of the app.
To only add the repository: <a href="project-manager.flatpakrepo">project-manager.flatpakrepo</a>.</p>
</body>
</html>
HTML

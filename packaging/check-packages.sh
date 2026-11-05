#!/usr/bin/env bash
# Builds the AUR package and the .deb from the release tarball publish-release.sh just made, and
# checks each as a player would get it: every file the tarball runs with is installed beside the
# program, and co-op's Epic meeting place starts from the installed copy, launched through its
# /usr/bin link (--eos-check: two players sign in, list, find, join and send). Needs the network.
#
#   packaging/check-packages.sh
#
# ⛳ The packages copy files out of the tarball by name, so a file added to the release is silently
# left out of a package until its recipe learns it (Epic's module and eos.txt were, from the AUR
# package, 2026-11-05). The runtime set here comes from the tarball itself, not from a list.
set -euo pipefail
cd "$(dirname "$0")/.."
fail() { echo "PACKAGE CHECK FAILED - $1"; exit 1; }
for tool in makepkg dpkg-deb tar dotnet; do command -v "$tool" >/dev/null || fail "$tool is needed"; done

VERSION=$(dotnet msbuild src/Driftwood.Client/Driftwood.Client.csproj -getProperty:Version -nologo | tr -d '[:space:]')
STEM=Driftwood-v$VERSION-linux-x64
TARBALL=artifacts/$STEM.tar.gz
[ -f "$TARBALL" ] || fail "$TARBALL is missing; run publish-release.sh first"
SUM=$(sha256sum "$TARBALL" | cut -d' ' -f1)

WORK=$(mktemp -d "$PWD/artifacts/.pkgcheck-XXXXXX")
trap 'rm -rf "$WORK"' EXIT

# What the game runs with: the tarball's top-level files, less what only a menu or a reader uses.
RUNTIME=$(tar -tzf "$TARBALL" | sed -n "s|^$STEM/\([^/]*\)$|\1|p" \
  | grep -vxE '|install\.sh|LICENSE|.*\.desktop|.*\.metainfo\.xml' | sort)
echo "$RUNTIME" | grep -qx Driftwood.Net.Eos.dll || fail "the tarball itself has no Epic module"

# check NAME ROOT LINK: ROOT is the unpacked package, LINK its /usr/bin command.
check() {
  local name=$1 root=$2 link=$3 lib=$2/usr/lib/driftwood-game file target
  for file in $RUNTIME; do
    [ -f "$lib/$file" ] || fail "$name does not install $file"
    cmp -s "$lib/$file" "$WORK/tar/$STEM/$file" || fail "$name installs a different $file"
  done
  [ -L "$root$link" ] || fail "$name has no $link link"
  target=$(readlink "$root$link")
  case "$target" in /*) target=$root$target ;; *) target=$(dirname "$root$link")/$target ;; esac
  # The game finds Epic beside the program it was started as, so start it through a link, as a
  # player's menu or PATH does.
  mkdir -p "$WORK/$name-bin"
  ln -sfn "$(realpath "$target")" "$WORK/$name-bin/driftwood-game"
  local home=$WORK/$name-home
  mkdir -p "$home"/{data,config,cache,state,tmp}
  env -i PATH=/usr/bin:/bin HOME="$home" TMPDIR="$home/tmp" XDG_DATA_HOME="$home/data" \
    XDG_CONFIG_HOME="$home/config" XDG_CACHE_HOME="$home/cache" XDG_STATE_HOME="$home/state" \
    "$WORK/$name-bin/driftwood-game" --eos-check > "$WORK/$name-eos.txt" 2>&1 \
    || { cat "$WORK/$name-eos.txt"; fail "co-op's Epic meeting place does not start from the $name"; }
  echo "OK  $name: $(echo "$RUNTIME" | wc -l) runtime files installed, Epic co-op works from $link"
}

mkdir -p "$WORK/tar"
tar -xzf "$TARBALL" -C "$WORK/tar"

# AUR: the recipe as update-aur.sh will pin it, built against this tarball rather than the download.
mkdir -p "$WORK/aur"
sed "s|^pkgver=.*|pkgver=$VERSION|; s|^pkgrel=.*|pkgrel=1|; s|^sha256sums_x86_64=.*|sha256sums_x86_64=('$SUM')|" \
  packaging/aur/PKGBUILD > "$WORK/aur/PKGBUILD"
cp "$TARBALL" "$WORK/aur/"
(cd "$WORK/aur" && BUILDDIR="$WORK/aur" PKGDEST="$WORK/aur" SRCDEST="$WORK/aur" \
  makepkg -f --nodeps --noarchive >"$WORK/aur/makepkg.log" 2>&1) \
  || { tail -20 "$WORK/aur/makepkg.log"; fail "makepkg could not build the AUR package"; }
check "AUR package" "$WORK/aur/pkg/driftwood-game-bin" /usr/bin/driftwood-game

# Debian/Ubuntu.
packaging/deb/build-deb.sh >/dev/null || fail "the .deb could not be built"
mkdir -p "$WORK/deb"
dpkg-deb -x "artifacts/driftwood-game_$VERSION-1_amd64.deb" "$WORK/deb"
check ".deb" "$WORK/deb" /usr/bin/driftwood-game

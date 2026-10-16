#!/usr/bin/env bash
# Publishes the current release to the AUR package driftwood-game-bin. Run after publish-release.sh
# has built artifacts/ and the GitHub release for that version is live (the AUR package downloads it).
#
#   packaging/aur/update-aur.sh [AUR_CLONE_DIR]
#
# Pins pkgver and the tarball's checksum in packaging/aur/PKGBUILD, copies it into a clone of
# ssh://aur@aur.archlinux.org/driftwood-game-bin.git, regenerates .SRCINFO, checks the live download
# against the checksum with makepkg, then commits and pushes. Refuses on any mismatch.
set -euo pipefail
cd "$(dirname "$0")/../.."
fail() { echo "AUR UPDATE FAILED - $1"; exit 1; }

AUR=${1:-${AUR_DIR:-$HOME/Documents/Development/aur-driftwood-game-bin}}
[ -d "$AUR/.git" ] || fail "no AUR clone at $AUR"
for tool in makepkg git dotnet; do command -v "$tool" >/dev/null || fail "$tool is needed"; done

VERSION=$(dotnet msbuild src/Driftwood.Client/Driftwood.Client.csproj -getProperty:Version -nologo | tr -d '[:space:]')
SUM_FILE=artifacts/Driftwood-v$VERSION-linux-x64.sha256
[ -f "$SUM_FILE" ] || fail "$SUM_FILE is missing; run publish-release.sh first"
SUM=$(cut -d' ' -f1 "$SUM_FILE")
[[ $SUM =~ ^[0-9a-f]{64}$ ]] || fail "$SUM_FILE does not hold a sha256"

PKG=packaging/aur/PKGBUILD
sed -i "s|^pkgver=.*|pkgver=$VERSION|; s|^pkgrel=.*|pkgrel=1|; s|^sha256sums_x86_64=.*|sha256sums_x86_64=('$SUM')|" "$PKG"
grep -q "^pkgver=$VERSION$" "$PKG" && grep -q "$SUM" "$PKG" || fail "could not pin $VERSION in $PKG"

git -C "$AUR" pull -q --ff-only
cp "$PKG" "$AUR/PKGBUILD"
(
  cd "$AUR"
  makepkg --printsrcinfo > .SRCINFO
  # Downloads the published tarball and checks it against the pinned sum, so a package that
  # cannot install never reaches the AUR.
  makepkg --verifysource -f >/dev/null 2>&1 || fail "the published tarball does not match $SUM"
  rm -f Driftwood-v*-linux-x64.tar.gz
  if git diff --quiet -- PKGBUILD .SRCINFO; then echo "AUR already at $VERSION"; exit 0; fi
  git add PKGBUILD .SRCINFO
  git commit -q -m "Update to $VERSION"
  git push -q origin HEAD
  echo "OK  driftwood-game-bin $VERSION-1 pushed ($(git rev-parse --short HEAD))"
)

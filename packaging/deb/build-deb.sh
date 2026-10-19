#!/usr/bin/env bash
# Builds the Debian/Ubuntu package driftwood-game from the release tarball publish-release.sh gated,
# so the .deb carries exactly the binary that was tested — the same thing the AUR package repackages.
#
#   packaging/deb/build-deb.sh            writes artifacts/driftwood-game_VERSION-1_amd64.deb (+ .sha256)
#
# Layout matches the AUR package: the game in /usr/lib/driftwood-game, a driftwood-game link in
# /usr/bin, the menu entry, metainfo and icons under /usr/share. No maintainer scripts: dpkg's own
# triggers (desktop-file-utils, hicolor-icon-theme) refresh the menu and icon caches.
# Lintian's remaining complaints are deliberate: the command lives in /usr/bin rather than /usr/games
# so the menu entry's Exec=driftwood-game resolves on every PATH, and nothing is stripped or unbundled
# because the files must be the ones the release gate tested (libzstd sits inside .NET's bundle).
# Needs dpkg-deb (Arch: dpkg; Debian/Ubuntu: dpkg) and tar. Reproducible: timestamps come from the
# last commit, owners are root.
set -euo pipefail
cd "$(dirname "$0")/../.."
fail() { echo "DEB FAILED - $1"; exit 1; }
for tool in dpkg-deb tar sha256sum dotnet; do command -v "$tool" >/dev/null || fail "$tool is needed"; done

VERSION=$(dotnet msbuild src/Driftwood.Client/Driftwood.Client.csproj -getProperty:Version -nologo | tr -d '[:space:]')
[ -n "$VERSION" ] || fail "Directory.Build.props did not give a version"
ID=io.github.codingncaffeine.Driftwood
PKG=driftwood-game
REV=1
TARBALL=artifacts/Driftwood-v$VERSION-linux-x64.tar.gz
DEB=artifacts/${PKG}_$VERSION-${REV}_amd64.deb
[ -f "$TARBALL" ] || fail "$TARBALL is missing; run publish-release.sh first"

WORK=$(mktemp -d "${TMPDIR:-/tmp}/driftwood-deb.XXXXXX")
trap 'rm -rf "$WORK"' EXIT
tar -xzf "$TARBALL" -C "$WORK"
SRC=$WORK/Driftwood-v$VERSION-linux-x64
ROOT=$WORK/root
[ -x "$SRC/Driftwood" ] || fail "the tarball has no Driftwood executable"

install -dm755 "$ROOT/usr/lib/$PKG" "$ROOT/usr/bin" "$ROOT/DEBIAN"
install -m755 "$SRC/Driftwood" "$ROOT/usr/lib/$PKG/Driftwood"
install -m644 "$SRC"/lib*.so* "$ROOT/usr/lib/$PKG/"
ln -s "../lib/$PKG/Driftwood" "$ROOT/usr/bin/$PKG"
install -Dm644 "$SRC/$ID.desktop" "$ROOT/usr/share/applications/$ID.desktop"
install -Dm644 "$SRC/$ID.metainfo.xml" "$ROOT/usr/share/metainfo/$ID.metainfo.xml"
for size in 16 24 32 48 64 128 256; do
  install -Dm644 "$SRC/icons/hicolor/${size}x${size}/apps/$ID.png" \
    "$ROOT/usr/share/icons/hicolor/${size}x${size}/apps/$ID.png"
done

# Debian keeps the licence as the package's copyright file, in the machine-readable format.
install -dm755 "$ROOT/usr/share/doc/$PKG"
{
  echo "Format: https://www.debian.org/doc/packaging-manuals/copyright-format/1.0/"
  echo "Upstream-Name: Driftwood"
  echo "Source: https://github.com/codingncaffeine/Driftwood"
  echo
  echo "Files: *"
  echo "Copyright: 2026 codingncaffeine"
  echo "License: GPL-3.0-only"
  echo " On Debian systems, the full text of the GNU General Public License version 3"
  echo " can be found in /usr/share/common-licenses/GPL-3."
} > "$ROOT/usr/share/doc/$PKG/copyright"
chmod 644 "$ROOT/usr/share/doc/$PKG/copyright"

# A binary package still carries a Debian changelog; one entry per build, pointing at the release notes.
{
  echo "$PKG ($VERSION-$REV) unstable; urgency=medium"
  echo
  echo "  * Driftwood v$VERSION, repackaged from the release tarball."
  echo "    Release notes: https://github.com/codingncaffeine/Driftwood/releases/tag/v$VERSION"
  echo
  echo " -- codingncaffeine <codingncaffeine@users.noreply.github.com>  $(date -u -R -d "@$(git log -1 --format=%ct)")"
} | gzip -9n > "$ROOT/usr/share/doc/$PKG/changelog.Debian.gz"
chmod 644 "$ROOT/usr/share/doc/$PKG/changelog.Debian.gz"

# The release brings .NET, GLFW, OpenAL Soft and SDL3 with it, as the AUR package says; these are
# what those open at run time. Names differ across releases (ICU's soname, OpenSSL's 64-bit-time
# rename on Ubuntu 24.04), so each is a list of alternatives, newest first. glibc 2.35 is the floor
# publish-release.sh enforces.
ICU="libicu78 | libicu77 | libicu76 | libicu75 | libicu74 | libicu73 | libicu72 | libicu71 | libicu70"
SIZE=$(du -sk --apparent-size "$ROOT/usr" | cut -f1)
cat > "$ROOT/DEBIAN/control" <<CONTROL
Package: $PKG
Version: $VERSION-$REV
Architecture: amd64
Maintainer: codingncaffeine <codingncaffeine@users.noreply.github.com>
Installed-Size: $SIZE
Depends: libc6 (>= 2.35), libgcc-s1, libstdc++6, zlib1g, $ICU, libssl3t64 | libssl3, libgl1, libegl1, libx11-6, libxrandr2, libxinerama1, libxcursor1, libxi6, libxext6, libwayland-client0, libwayland-cursor0, libwayland-egl1, libxkbcommon0, libpulse0, libudev1, hicolor-icon-theme
Recommends: libgl1-mesa-dri, libdecor-0-0, xdg-desktop-portal
Suggests: zenity
Section: games
Priority: optional
Homepage: https://github.com/codingncaffeine/Driftwood
Description: Open-world survival crafting game
 Gather, build, explore, fight, trade and learn magic in a persistent,
 procedurally generated world. Self-contained: brings its own .NET runtime,
 GLFW, OpenAL Soft and SDL3, and runs under Wayland or X11 with an OpenGL 3.3
 driver. Start it from the application menu or with driftwood-game.
CONTROL

# Every timestamp from the last commit, so the same tarball always gives the same package.
export SOURCE_DATE_EPOCH=$(git log -1 --format=%ct)
find "$ROOT" -exec touch -h -d "@$SOURCE_DATE_EPOCH" {} +
rm -f -- "$DEB" "$DEB.sha256"
dpkg-deb --root-owner-group -Zxz --build "$ROOT" "$DEB" >/dev/null || fail "dpkg-deb refused the tree"
(cd artifacts && sha256sum "$(basename "$DEB")" > "$(basename "$DEB").sha256")
dpkg-deb --info "$DEB" | grep -E "Package|Version|Installed-Size" | sed 's/^ */    /'
echo "OK  $DEB"

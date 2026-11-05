#!/usr/bin/env bash
# Builds the Windows installer (Driftwood-vX.Y.Z-win-x64-setup.exe + .sha256) on Linux from the files
# INSIDE the Windows zip, so the installer and the zip carry the same bytes. makensis runs in a small
# Debian container (podman or docker), so the release machine needs no NSIS and no Wine.
# Called by publish-release.sh after build-zip.sh; runnable alone (VERSION as the argument).
set -euo pipefail
cd "$(dirname "$0")/../.."
ROOT=$PWD

fail() { echo "$1" >&2; exit 1; }
ENGINE=$(command -v podman || command -v docker || true)
[ -n "$ENGINE" ] || fail "MISSING TOOL - podman or docker is needed to run makensis."
for tool in unzip sha256sum git; do
  command -v "$tool" >/dev/null 2>&1 || fail "MISSING TOOL - $tool is needed to build the installer."
done

VERSION=${1:-$(dotnet msbuild src/Driftwood.Client/Driftwood.Client.csproj -getProperty:Version -nologo | tr -d '[:space:]')}
STEM=Driftwood-v$VERSION-win-x64
ZIP=artifacts/$STEM.zip
SETUP=artifacts/$STEM-setup.exe
CHECKSUM=$SETUP.sha256
[ -f "$ZIP" ] || fail "INSTALLER FAILED - $ZIP is missing; run packaging/windows/build-zip.sh first."
(cd artifacts && sha256sum -c --quiet "$STEM.zip.sha256") || fail "INSTALLER FAILED - $ZIP does not match its .sha256."

# One small image with NSIS, built once and reused (Debian's nsis package: makensis + MUI2).
IMAGE=localhost/driftwood-nsis:debian13
if ! "$ENGINE" image exists "$IMAGE" 2>/dev/null && ! "$ENGINE" image inspect "$IMAGE" >/dev/null 2>&1; then
  printf 'FROM docker.io/library/debian:13\nENV DEBIAN_FRONTEND=noninteractive\nRUN apt-get update && apt-get install -y --no-install-recommends nsis && rm -rf /var/lib/apt/lists/*\n' \
    | "$ENGINE" build -q -t "$IMAGE" - >/dev/null || fail "INSTALLER FAILED - the NSIS image could not be built."
fi

WORK=$(mktemp -d "$ROOT/artifacts/.setup-XXXXXX")
trap 'rm -rf -- "$WORK"' EXIT
unzip -q "$ZIP" -d "$WORK/payload"
cp assets/driftwood.ico packaging/windows/installer.nsi "$WORK/"
rm -f -- "$SETUP" "$CHECKSUM"
# SOURCE_DATE_EPOCH: makensis stamps the commit's time, not the build's, where it stamps one.
"$ENGINE" run --rm --network=none -e SOURCE_DATE_EPOCH="$(git log -1 --format=%ct)" \
  -v "$WORK:/w:Z" -w /w "$IMAGE" \
  makensis -V2 -WX -DVERSION="$VERSION" -DPAYLOAD="payload/$STEM" -DICON=driftwood.ico -DOUTFILE="$STEM-setup.exe" installer.nsi \
  || fail "INSTALLER FAILED - makensis stopped (see above)."
mv "$WORK/$STEM-setup.exe" "$SETUP"
(cd artifacts && sha256sum "$STEM-setup.exe" > "$STEM-setup.exe.sha256")
echo "OK  $SETUP"
echo "    $CHECKSUM"

#!/usr/bin/env bash
# Builds the Windows download on Linux: a self-contained win-x64 publish, checked file by file, in a
# deterministic zip with its .sha256 in artifacts/. Called by publish-release.sh as its last step;
# runnable alone (VERSION as the argument, else read from Directory.Build.props).
#
# Natives sit beside the exe, as on Linux — the old self-extracting exe unpacked them into %TEMP% on
# every machine that ran it — and the Windows loader searches the exe's folder by itself.
# The zip is gated on a real Windows machine by .github/workflows/windows-gate.yml before the release
# goes public, so the bytes that were checked are the bytes that ship.
#
# Needs: the .NET 11 SDK, the Epic SDK + credentials (as publish-release.sh), objdump, zip, unzip.
set -euo pipefail
cd "$(dirname "$0")/../.."
ROOT=$PWD

fail() { echo "$1" >&2; exit 1; }
for tool in dotnet objdump zip unzip sha256sum git; do
  command -v "$tool" >/dev/null 2>&1 || fail "MISSING TOOL - $tool is needed to build the Windows download."
done

VERSION=${1:-$(dotnet msbuild src/Driftwood.Client/Driftwood.Client.csproj -getProperty:Version -nologo | tr -d '[:space:]')}
[ -n "$VERSION" ] || fail "VERSION FAILED - no version."

RID=win-x64
OUT=artifacts/Driftwood-$RID
STEM=Driftwood-v$VERSION-$RID
PACKAGE=artifacts/$STEM.zip
CHECKSUM=artifacts/$STEM.zip.sha256

case "$OUT" in artifacts/Driftwood-win-x64) ;; *) fail "WINDOWS FAILED - refusing to clean an unexpected path." ;; esac
rm -rf -- "$OUT"
dotnet publish src/Driftwood.Client/Driftwood.Client.csproj -c Release -r "$RID" --self-contained true \
  -o "$OUT" -v quiet --nologo -p:PublishSingleFile=true -p:DebugType=embedded -p:ContinuousIntegrationBuild=true \
  || fail "WINDOWS FAILED - the win-x64 publish did not finish."

# Exactly the expected payload: a new native dependency is a reviewed decision, not a passenger. Epic
# (module, library, the game's credentials) is part of every release, as on Linux.
expected="Driftwood.exe SDL3.dll glfw3.dll soft_oal.dll Driftwood.Net.Eos.dll EOSSDK-Win64-Shipping.dll eos.txt"
actual=$(cd "$OUT" && ls -A | LC_ALL=C sort | tr '\n' ' ' | sed 's/ $//')
[ "$actual" = "$(echo $expected | tr ' ' '\n' | LC_ALL=C sort | tr '\n' ' ' | sed 's/ $//')" ] \
  || fail "WINDOWS FAILED - the payload is [$actual], not [$expected]."
# A double-click is a window, never a window with a console behind it: the GUI subsystem (2).
# ⚠ Read into a variable first: under pipefail, grep -q closing the pipe early fails objdump (SIGPIPE).
headers=$(objdump -p "$OUT/Driftwood.exe")
grep -Eq '^Subsystem[[:space:]]+0+2[[:space:]]' <<<"$headers" \
  || fail "WINDOWS FAILED - Driftwood.exe is not a GUI-subsystem program."
for native in Driftwood.exe SDL3.dll glfw3.dll soft_oal.dll EOSSDK-Win64-Shipping.dll; do
  format=$(objdump -f "$OUT/$native" 2>/dev/null || true)
  grep -q 'file format pei-x86-64' <<<"$format" \
    || fail "WINDOWS FAILED - $native is not a 64-bit Windows binary."
done
# The Epic module is managed and built per platform; the Windows one must bind Epic's Windows library.
if ! grep -aq 'EOSSDK-Win64-Shipping' "$OUT/Driftwood.Net.Eos.dll" || grep -aq 'EOSSDK-Linux-Shipping' "$OUT/Driftwood.Net.Eos.dll"; then
  fail "WINDOWS FAILED - Driftwood.Net.Eos.dll was built for another platform."
fi

STAGE_ROOT=$(mktemp -d "$ROOT/artifacts/.win-stage-XXXXXX")
trap 'rm -rf -- "$STAGE_ROOT"' EXIT
STAGE=$STAGE_ROOT/$STEM
mkdir -p "$STAGE"
cp -a "$OUT"/. "$STAGE/"
cp LICENSE "$STAGE/LICENSE.txt"
chmod 755 "$STAGE"
chmod 644 "$STAGE"/*
# Deterministic: sorted names, every time stamp = the commit's, no extra attributes (-X), no
# directory entries (-D), UTC so the DOS time fields do not follow the build machine's zone.
STAMP=$(git log -1 --format=%ct)
find "$STAGE_ROOT" -mindepth 1 -exec touch -h -d "@$STAMP" {} +
rm -f -- "$PACKAGE" "$CHECKSUM"
(cd "$STAGE_ROOT" && find "$STEM" -type f -print | LC_ALL=C sort | TZ=UTC zip -X -q -D -9 "$ROOT/$PACKAGE" -@) \
  || fail "WINDOWS FAILED - the zip was not written."
listed=$(unzip -Z1 "$PACKAGE" | LC_ALL=C sort)
staged=$(cd "$STAGE_ROOT" && find "$STEM" -type f | LC_ALL=C sort)
[ "$listed" = "$staged" ] || fail "WINDOWS FAILED - the zip does not match the staged files."
(cd artifacts && sha256sum "$STEM.zip" > "$STEM.zip.sha256")

echo "OK  $PACKAGE"
echo "    $CHECKSUM"

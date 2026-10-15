#!/usr/bin/env bash
# Builds Release, publishes a self-contained linux-x64 build, runs every release gate on it, and
# packages the public tarball. The Linux counterpart of publish-release.bat, step for step.
# Run once after the complete change batch, immediately before publishing it.
#
# Needs: the .NET 11 SDK, xvfb-run (Arch xorg-server-xvfb, Debian/Ubuntu xvfb), dbus-run-session,
# objdump (binutils), and podman or docker the first time (tools/linux/build-sdl3.sh builds SDL).
# Every instrument runs with private XDG folders and OpenAL's null device, so the gate never reads or
# writes a real player's saves and never makes a sound; the interface check draws into a private X
# server with a private session bus, so nothing reaches the desktop.
set -euo pipefail
cd "$(dirname "$0")"
ROOT=$PWD

RID=linux-x64
ID=io.github.codingncaffeine.Driftwood
OUT=artifacts/Driftwood-$RID

fail() { echo; echo "$1"; exit 1; }

for tool in dotnet xvfb-run dbus-run-session tar sha256sum objdump; do
  command -v "$tool" >/dev/null 2>&1 || fail "MISSING TOOL - $tool is needed to build and gate a release."
done

VERSION=$(dotnet msbuild src/Driftwood.Client/Driftwood.Client.csproj -getProperty:Version -nologo | tr -d '[:space:]')
[ -n "$VERSION" ] || fail "VERSION FAILED - Directory.Build.props did not give a version."
STEM=Driftwood-v$VERSION-$RID
PACKAGE=artifacts/$STEM.tar.gz
CHECKSUM=artifacts/$STEM.sha256

echo "[1/8] building Release..."
dotnet build Driftwood.sln -c Release -v quiet --nologo || fail "BUILD FAILED."

# dotnet publish does not clear its output directory, so an old loose file could ride along into a
# later release. Remove exactly this generated directory first, and nothing that is not it.
echo "[2/8] publishing a self-contained $RID build..."
case "$OUT" in artifacts/Driftwood-linux-x64) ;; *) fail "PUBLISH FAILED - refusing to clean an unexpected path." ;; esac
rm -rf -- "$OUT"
dotnet publish src/Driftwood.Client/Driftwood.Client.csproj -c Release -r "$RID" --self-contained true \
  -o "$OUT" -v quiet --nologo -p:PublishSingleFile=true -p:DebugType=embedded || fail "PUBLISH FAILED."

# ⚠ Single-file, but with the native libraries BESIDE the executable rather than extracted from it:
# self-extraction on Linux unpacks into ~/.net on every machine that runs it. The SDL package carries
# its library three times, once per soname; the loader asks for libSDL3.so alone, so the other two
# are removed — after proving they are the same bytes, so a package that ever ships two different
# builds there stops the release instead of losing one silently.
for twin in "$OUT"/libSDL3.so.*; do
  [ -e "$twin" ] || continue
  cmp -s "$twin" "$OUT/libSDL3.so" || fail "PUBLISH FAILED - $(basename "$twin") differs from libSDL3.so."
  rm -f -- "$twin"
done

# ⛔ The package's SDL needs glibc 2.38 and so does not load on Ubuntu 22.04 or Debian 12, where the
# rest of the release runs: no controllers, no file chooser. The same SDL release, built on a 22.04
# baseline by tools/linux/build-sdl3.sh, replaces it.
SDL=$(tools/linux/build-sdl3.sh) || fail "PUBLISH FAILED - SDL3 could not be built on its baseline."
cp -f -- "$SDL" "$OUT/libSDL3.so"

# Exactly the expected payload: a new native dependency is a reviewed decision, not a passenger.
expected="Driftwood libSDL3.so libglfw.so.3 libopenal.so"
actual=$(cd "$OUT" && ls -A | sort | tr '\n' ' ' | sed 's/ $//')
[ "$actual" = "$(echo $expected | tr ' ' '\n' | sort | tr '\n' ' ' | sed 's/ $//')" ] \
  || fail "PUBLISH FAILED - the payload is [$actual], not [$expected]."
file "$OUT/Driftwood" 2>/dev/null | grep -q 'ELF 64-bit LSB.*x86-64' || fail "PUBLISH FAILED - Driftwood is not an x86-64 ELF executable."

# The oldest systems the release promises: Ubuntu 22.04 (glibc 2.35) and Debian 12 (2.36). One file
# built against a newer glibc quietly drops them, so every shipped binary is read for it.
for binary in "$OUT"/*; do
  floor=$(objdump -T "$binary" 2>/dev/null | grep -oE 'GLIBC_[0-9.]+' | sort -Vu | tail -1)
  case "$floor" in ""|GLIBC_2.3[0-5]|GLIBC_2.[12]*) ;;
    *) fail "PUBLISH FAILED - $(basename "$binary") needs $floor; the release must run on glibc 2.35." ;;
  esac
done

# Private homes for every instrument, inside artifacts/ and removed afterwards.
GATE=$(mktemp -d "$ROOT/artifacts/.gate-XXXXXX")
trap 'rm -rf -- "$GATE"' EXIT
mkdir -p "$GATE"/{config,data,cache,state}
export XDG_CONFIG_HOME=$GATE/config XDG_DATA_HOME=$GATE/data XDG_CACHE_HOME=$GATE/cache XDG_STATE_HOME=$GATE/state
export ALSOFT_DRIVERS=null NO_AT_BRIDGE=1 DOTNET_CLI_TELEMETRY_OPTOUT=1

run() { "$ROOT/$OUT/Driftwood" "$@"; }

# Gate on the published binary, not the build output: this is the thing that ships, and it catches
# a publish that dropped a native dependency the build had sitting beside it.
echo "[3/8] checking release identity, embedded offline audio and magic..."
[ "$(run --version)" = "Driftwood v$VERSION" ] || fail "VERSION FAILED - the executable does not report Driftwood v$VERSION."
run --audio-check || fail "AUDIO CHECK FAILED - the executable cannot decode its offline fallback."
run --magic-check || fail "MAGIC CHECK FAILED - progression, spells, companions or persistence drifted."

echo "[4/8] checking bundled SDL3 and controller interop..."
run --controller-check || fail "CONTROLLER CHECK FAILED - bundled SDL3 is unavailable."

echo "[5/8] auditing the published build..."
run --audit --seed driftwood --chunks 12 || fail "AUDIT FAILED - not shipping this build."

# A hidden OpenGL window whose pixels are read back off the framebuffer, in a private X server on a
# private session bus. Mesa's software rasteriser draws it, so the gate needs no GPU and runs the
# same on any machine; it still exercises the exact shaders and every screen state.
echo "[6/8] checking every interface reaches the screen..."
for size in "400 480" "1600 900" "1920 1440"; do
  set -- $size
  xvfb-run -a -s "-screen 0 1920x1440x24" \
    dbus-run-session --config-file="$ROOT/tools/linux/isolated-bus.conf" -- \
    "$ROOT/$OUT/Driftwood" --ui-check --chunks 6 --seed driftwood --width "$1" --height "$2" \
    || fail "UI CHECK FAILED - an interface is not reaching the screen at $1x$2. See the faults above."
done

# The icon theme comes from the committed .ico, the same pixels the Windows build wears.
echo "[7/8] staging the desktop entry, icons and licence..."
STAGE=$GATE/stage/$STEM
mkdir -p "$STAGE"
cp -a "$OUT"/. "$STAGE/"
dotnet tools/IconForge/bin/Release/net11.0/IconForge.dll --hicolor assets/driftwood.ico "$STAGE/icons" "$ID" >/dev/null \
  || fail "PACKAGE FAILED - the icon theme could not be written."
cp packaging/linux/$ID.desktop packaging/linux/$ID.metainfo.xml packaging/linux/install.sh LICENSE "$STAGE/"
chmod 755 "$STAGE/Driftwood" "$STAGE/install.sh"
chmod 644 "$STAGE"/lib*.so* "$STAGE"/*.desktop "$STAGE"/*.xml "$STAGE/LICENSE"
find "$STAGE/icons" -type d -exec chmod 755 {} + && find "$STAGE/icons" -type f -exec chmod 644 {} +

echo "[8/8] packaging the public release asset..."
rm -f -- "$PACKAGE" "$CHECKSUM"
tar --sort=name --owner=0 --group=0 --numeric-owner --mtime="@$(git log -1 --format=%ct)" \
  -C "$GATE/stage" -czf "$PACKAGE" "$STEM" || fail "PACKAGE FAILED - the tarball was not written."
# The archive must hold exactly what was staged, under one top folder.
listed=$(tar -tzf "$PACKAGE" | grep -v '/$' | sort)
staged=$(cd "$GATE/stage" && find "$STEM" -type f | sort)
[ "$listed" = "$staged" ] || fail "PACKAGE FAILED - the tarball does not match the staged files."
(cd artifacts && sha256sum "$STEM.tar.gz" > "$STEM.sha256")

echo
echo "OK  $PACKAGE"
echo "    $CHECKSUM"

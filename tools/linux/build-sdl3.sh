#!/usr/bin/env bash
# Builds the libSDL3.so the Linux release ships, on an old enough baseline that it loads everywhere
# the rest of the release does.
#
#   tools/linux/build-sdl3.sh            build (or reuse) artifacts/sdl3-<version>/libSDL3.so
#
# Why not the one in the SDL3-CS.Linux package: that build needs glibc 2.38 (Ubuntu 24.04), so on
# Ubuntu 22.04 and Debian 12 it does not load — no controllers and no file chooser there, while GLFW
# and OpenAL, built against glibc 2.34, run fine. This builds the same SDL release the C# binding
# wraps, inside Ubuntu 22.04 (glibc 2.35), with every window, audio and input backend loaded at run
# time as the package's is, so the library itself needs nothing but libc and libm.
# Needs podman or docker. The result is cached by version; delete the folder to rebuild.
set -euo pipefail
cd "$(dirname "$0")/../.."

# The binding's version is SDL's: SDL3-CS 3.4.14 wraps SDL release-3.4.14. Read from the project so
# the two can never drift apart.
VERSION=$(grep -oE 'Include="SDL3-CS" Version="[0-9.]+"' src/Driftwood.Client/Driftwood.Client.csproj | grep -oE '[0-9]+\.[0-9]+\.[0-9]+')
[ -n "$VERSION" ] || { echo "build-sdl3: no SDL3-CS version in the client project" >&2; exit 1; }
OUT=$PWD/artifacts/sdl3-$VERSION
if [ -s "$OUT/libSDL3.so" ]; then echo "$OUT/libSDL3.so"; exit 0; fi

ENGINE=$(command -v podman || command -v docker || true)
[ -n "$ENGINE" ] || { echo "build-sdl3: podman or docker is needed to build SDL on its baseline" >&2; exit 1; }

mkdir -p "$OUT"
"$ENGINE" run --rm -v "$OUT:/out:Z" docker.io/library/ubuntu:22.04 bash -euo pipefail -c "
  export DEBIAN_FRONTEND=noninteractive
  apt-get update -qq
  apt-get install -y -qq --no-install-recommends ca-certificates curl cmake ninja-build gcc g++ pkg-config \
    libx11-dev libxext-dev libxrandr-dev libxcursor-dev libxi-dev libxfixes-dev libxss-dev libxtst-dev \
    libxkbcommon-dev libwayland-dev libegl-dev libgl-dev libgles-dev libdrm-dev libgbm-dev \
    libdbus-1-dev libudev-dev libusb-1.0-0-dev libibus-1.0-dev \
    libasound2-dev libpulse-dev libpipewire-0.3-dev libdecor-0-dev > /dev/null
  curl -sSL https://github.com/libsdl-org/SDL/releases/download/release-$VERSION/SDL3-$VERSION.tar.gz | tar -xz -C /tmp
  cmake -S /tmp/SDL3-$VERSION -B /tmp/build -G Ninja -DCMAKE_BUILD_TYPE=Release \
    -DSDL_SHARED=ON -DSDL_STATIC=OFF -DSDL_TESTS=OFF -DSDL_EXAMPLES=OFF -DSDL_INSTALL=OFF \
    -DSDL_DEPS_SHARED=ON > /tmp/configure.txt
  grep -E 'SDL_(X11|WAYLAND|PIPEWIRE|PULSEAUDIO|ALSA|DBUS|HIDAPI|LIBUDEV)\b' /tmp/build/CMakeCache.txt | sed 's/:BOOL//' >&2
  cmake --build /tmp/build > /dev/null
  cp -L /tmp/build/libSDL3.so.0 /out/libSDL3.so
"

# The whole point, checked rather than assumed: nothing newer than the baseline, and nothing linked
# but libc and libm — every backend must be loaded at run time or a missing one stops the game.
floor=$(objdump -T "$OUT/libSDL3.so" | grep -oE 'GLIBC_[0-9.]+' | sort -Vu | tail -1)
needed=$(readelf -d "$OUT/libSDL3.so" | grep -oE 'NEEDED.*\[[^]]+\]' | grep -oE '\[[^]]+\]' | tr -d '[]' | sort | tr '\n' ' ')
echo "build-sdl3: SDL $VERSION, needs $floor, links $needed" >&2
case "$floor" in GLIBC_2.3[0-5]|GLIBC_2.[12]*) ;; *) echo "build-sdl3: $floor is newer than the baseline" >&2; rm -f "$OUT/libSDL3.so"; exit 1 ;; esac
case "$needed" in "ld-linux-x86-64.so.2 libc.so.6 libm.so.6 "|"libc.so.6 libm.so.6 ") ;; *) echo "build-sdl3: links more than libc and libm: $needed" >&2; rm -f "$OUT/libSDL3.so"; exit 1 ;; esac
echo "$OUT/libSDL3.so"

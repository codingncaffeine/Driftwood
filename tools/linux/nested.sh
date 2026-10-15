#!/usr/bin/env bash
# Runs Driftwood inside a private, virtual KWin (Wayland, with Xwayland for an X11 run), so no window
# reaches the desktop and nothing reaches the desktop's D-Bus or the player's own saves and settings.
#
#   tools/linux/nested.sh WORK_DIR SECONDS [--x11] -- DRIFTWOOD [ARGS...]
#
# Isolation: a session bus that activates no services (isolated-bus.conf); every XDG directory, the
# runtime one included, inside WORK_DIR or a private /tmp folder, and the script refuses to start
# otherwise. OpenAL opens its null device, so nothing is ever heard. --x11 hides the Wayland socket so
# GLFW takes Xwayland instead. The game's output goes to WORK_DIR/driftwood.txt, KWin's to
# WORK_DIR/kwin.txt. Afterwards a leak check lists anything written to the real Driftwood folders.
set -u
usage() { sed -n '2,12p' "$0"; exit 2; }
[ $# -ge 4 ] || usage
HERE="$(cd "$(dirname "$0")" && pwd)"
WORK="$(mkdir -p "$1" && cd "$1" && pwd)"; SECS="$2"; shift 2
X11=0
if [ "$1" = "--x11" ]; then X11=1; shift; fi
[ "$1" = "--" ] || usage
shift
command -v kwin_wayland >/dev/null 2>&1 || { echo "nested: kwin_wayland not found"; exit 2; }
command -v dbus-run-session >/dev/null 2>&1 || { echo "nested: dbus-run-session not found"; exit 2; }

K="$WORK/home"
rm -rf "$K"; mkdir -p "$K/config" "$K/state" "$K/data" "$K/cache"
RT="/tmp/driftwood-$(id -u)-rt-$$"; mkdir -p "$RT"; chmod 700 "$RT"
printf '[Wayland]\nEnablePrimarySelection=false\n' > "$K/config/kwinrc"

# KWin splits --exit-with-session on spaces, so the session is a space-free script in the runtime dir.
SESSION="$RT/session.sh"
{
  echo '#!/usr/bin/env bash'
  [ "$X11" = 1 ] && echo 'unset WAYLAND_DISPLAY'
  printf 'exec'; printf ' %q' "$@"; printf ' > %q 2>&1\n' "$WORK/driftwood.txt"
} > "$SESSION"
chmod +x "$SESSION"

export XDG_CONFIG_HOME="$K/config" XDG_STATE_HOME="$K/state" XDG_DATA_HOME="$K/data" XDG_CACHE_HOME="$K/cache" XDG_RUNTIME_DIR="$RT"
export ALSOFT_DRIVERS=null NO_AT_BRIDGE=1
unset DISPLAY WAYLAND_DISPLAY JOURNAL_STREAM
for v in XDG_CONFIG_HOME XDG_STATE_HOME XDG_DATA_HOME XDG_CACHE_HOME; do
  case "${!v}" in "$K"/*) ;; *) echo "isolation guard: $v=${!v} is outside $K, refusing"; exit 99;; esac
done
case "$XDG_RUNTIME_DIR" in /tmp/driftwood-*-rt-*) ;; *) echo "isolation guard: runtime dir not private, refusing"; exit 99;; esac

START=$(date '+%Y-%m-%d %H:%M:%S')
timeout -k 5 -s TERM "$SECS" dbus-run-session --config-file="$HERE/isolated-bus.conf" -- \
  kwin_wayland --virtual --xwayland --no-lockscreen --no-global-shortcuts --socket "wayland-driftwood-$$" \
  --width 1600 --height 900 --exit-with-session "$SESSION" > "$WORK/kwin.txt" 2>&1
STATUS=$?
if [ -d "$RT/doc" ]; then fusermount3 -u "$RT/doc" 2>/dev/null; fi
rm -rf "$RT"
echo "nested: compositor exit $STATUS"
echo "--- real-profile leak check (anything listed is a leak) ---"
find ~/.config/Driftwood ~/.local/share/Driftwood ~/.cache/Driftwood -newermt "$START" 2>/dev/null
find ~/.config ~/.local/state -maxdepth 2 -newermt "$START" \( -name 'kwin*' -o -name 'kglobal*' -o -name 'kdeglobals' \) 2>/dev/null
echo "--- end leak check ---"
exit $STATUS

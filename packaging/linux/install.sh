#!/bin/sh
# Adds Driftwood to your application menu, from wherever this folder was extracted to.
#
#   ./install.sh               add the menu entry, icons and a `driftwood-game` command for this copy
#   ./install.sh --uninstall   remove them again (your worlds and settings are not touched)
#
# Nothing here needs root: the entry and icons go under ~/.local/share and the command is a link in
# ~/.local/bin. The game itself stays in this folder; after moving the folder, run this again.
# Worlds, packs and skins live in ~/.local/share/Driftwood and settings in ~/.config/Driftwood.
set -eu

ID=io.github.codingncaffeine.Driftwood
HERE=$(cd "$(dirname "$0")" && pwd)
DATA=${XDG_DATA_HOME:-$HOME/.local/share}
case "$DATA" in /*) ;; *) DATA=$HOME/.local/share ;; esac
APPS=$DATA/applications
ICONS=$DATA/icons/hicolor
BIN=$HOME/.local/bin
SIZES="16 24 32 48 64 128 256"

refresh() {
  command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database -q "$APPS" 2>/dev/null || true
  command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q -t "$ICONS" 2>/dev/null || true
}

if [ "${1:-}" = "--uninstall" ]; then
  rm -f "$APPS/$ID.desktop"
  for size in $SIZES; do rm -f "$ICONS/${size}x${size}/apps/$ID.png"; done
  # Only ever the link this script made: a real file of that name is somebody else's.
  [ -L "$BIN/driftwood-game" ] && rm -f "$BIN/driftwood-game"
  refresh
  echo "Driftwood removed from the application menu."
  exit 0
fi

[ -x "$HERE/Driftwood" ] || { echo "install.sh: no Driftwood executable beside this script" >&2; exit 1; }
if [ -e "$BIN/driftwood-game" ] && [ ! -L "$BIN/driftwood-game" ]; then
  echo "install.sh: $BIN/driftwood-game exists and is not a link this script made; leaving it alone" >&2
  exit 1
fi

# The menu entry launches the link rather than this folder: a folder name can hold characters an
# Exec line cannot carry everywhere (GLib rejects a quoted %% before it expands it), and the link's
# path is one this script chose. The desktop-entry quoting is still applied, for an unusual $HOME:
# ", `, $ and \ are escaped inside double quotes, the string escape then doubles every backslash.
mkdir -p "$BIN"
ln -sfn "$HERE/Driftwood" "$BIN/driftwood-game"
exec_path=$(printf '%s' "$BIN/driftwood-game" | sed 's/[\\"`$]/\\&/g; s/\\/\\\\/g; s/%/%%/g')

mkdir -p "$APPS"
sed "s|^Exec=.*|Exec=\"$(printf '%s' "$exec_path" | sed 's/[|&\\]/\\&/g')\"|" "$HERE/$ID.desktop" > "$APPS/$ID.desktop.tmp"
mv "$APPS/$ID.desktop.tmp" "$APPS/$ID.desktop"

for size in $SIZES; do
  mkdir -p "$ICONS/${size}x${size}/apps"
  cp "$HERE/icons/hicolor/${size}x${size}/apps/$ID.png" "$ICONS/${size}x${size}/apps/$ID.png"
done

refresh
echo "Driftwood added to the application menu (launching $HERE/Driftwood)."
case ":$PATH:" in *":$BIN:"*) echo "Type 'driftwood-game' in a terminal to start it from there too." ;; esac

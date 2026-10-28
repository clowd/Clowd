#!/usr/bin/env bash
# Checks an assembled package directory (the UI publish with the app's Rust
# binaries beside Prisc.Ui and the recorder in recorder/) against the list in
# shipped.sh, so a binary that silently fell out of one of the three builds
# fails here instead of on a user's machine. Used by ci.yml's package job.
#
#   verify-package.sh <rid> <package dir> [--local]
#
# --local (build.sh) skips what only CI produces: the shell extension's MSIX
# and the VC++ runtime, which a local build copies only when it finds it.
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
# shellcheck source=tools/staging/shipped.sh
. "$here/shipped.sh"

if [ $# -lt 2 ] || [ $# -gt 3 ] || { [ $# -eq 3 ] && [ "$3" != --local ]; }; then
  sed -n '2,10p' "$0" | sed 's/^# \{0,1\}//' >&2
  exit 2
fi
rid=$1
pkg=$2
ci=1
[ "${3:-}" = --local ] && ci=0
exe=$(rid_exe "$rid")

expected=("Prisc.Ui$exe" "Prisc.VideoRender$exe")
while IFS= read -r f; do expected+=("$f"); done < <(app_binaries "$rid")
while IFS= read -r f; do expected+=("recorder/$f"); done < <(recorder_binaries "$rid")
expected+=(recorder/LICENSE.txt)
if rid_has_ai "$rid"; then
  expected+=(prisc_ai.LICENSE.txt prisc_ai.NOTICE.txt)
fi
case "$rid" in
  win-*)
    expected+=(recorder/obs.dll recorder/obs-plugins recorder/data/libobs)
    if rid_has_ai "$rid"; then expected+=(DirectML.dll); fi
    if [ "$ci" = 1 ]; then
      expected+=('PriscShellExt_*.dll' PriscShellExt.msix recorder/vcruntime140.dll)
      if rid_has_ai "$rid"; then expected+=(vcruntime140.dll msvcp140.dll); fi
    fi
    ;;
  osx-*)
    expected+=(recorder/Frameworks/libobs.framework recorder/Frameworks/libavcodec.dylib recorder/obs-plugins)
    ;;
  linux-*)
    expected+=(recorder/libobs.so.30 recorder/obs-plugins recorder/data/libobs)
    ;;
esac

missing=0
for f in "${expected[@]}"; do
  if ! compgen -G "$pkg/$f" >/dev/null; then
    echo "::error::package $rid is missing $f"
    missing=1
  fi
done
[ "$missing" -eq 0 ]
echo "package $rid holds all ${#expected[@]} expected entries"

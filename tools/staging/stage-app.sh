#!/usr/bin/env bash
# The app's Rust half (everything beside Prisc.Ui except the recorder/ folder),
# driven by the list in shipped.sh. Used by build.sh and build-rust.yml.
#
#   stage-app.sh packages <rid>
#       the cargo `-p` arguments that build it
#   stage-app.sh stage <rid> <cargo profile dir> <dest>
#       copy the binaries, their runtime companions and licenses into <dest>
#   stage-app.sh debug-files <rid> <cargo profile dir>
#       one tab-separated "<debug file> <binary>" pair per line, for the
#       Sentry upload (Windows and macOS)
#
# Works on macOS, Linux and on Windows under git bash.
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../.." && pwd)
# shellcheck source=tools/staging/shipped.sh
. "$here/shipped.sh"

usage() {
  sed -n '2,12p' "$0" | sed 's/^# \{0,1\}//' >&2
  exit 2
}

mode=${1:-}
rid=${2:-}
[ -n "$mode" ] && [ -n "$rid" ] || usage
rid_triple "$rid" >/dev/null

case "$mode" in
  packages)
    app_packages "$rid" | as_cargo_args
    echo
    ;;

  stage)
    src=${3:?usage: stage-app.sh stage <rid> <cargo profile dir> <dest>}
    dest=${4:?usage: stage-app.sh stage <rid> <cargo profile dir> <dest>}
    mkdir -p "$dest"
    while IFS= read -r f; do
      cp "$src/$f" "$dest/"
    done < <(app_binaries "$rid")
    if rid_has_ai "$rid"; then
      # prisc_ai is GPL-3.0 (it embeds the RobustVideoMatting weights); GPL
      # sections 4/6 require the license text and source offer to travel with
      # every distributed copy.
      cp "$root/crates/prisc_ai/LICENSE" "$dest/prisc_ai.LICENSE.txt"
      cp "$root/crates/prisc_ai/NOTICE.txt" "$dest/prisc_ai.NOTICE.txt"
      case "$rid" in
        win-*)
          # ONNX Runtime is statically linked, but the DirectML EP needs its
          # helper dll beside the exe (ort's copy-dylibs staged it into the
          # profile dir). The macOS CoreML EP is fully static.
          cp "$src/DirectML.dll" "$dest/"
          # prisc_ai is the app's one /MD binary; every other binary beside
          # Prisc.Ui is crt-static (the recorder/ folder carries its own copy).
          copy_vc_runtime "$rid" "$dest"
          ;;
      esac
    fi
    ;;

  debug-files)
    src=${3:?usage: stage-app.sh debug-files <rid> <cargo profile dir>}
    # Named artifacts rather than the directory, which would recurse into
    # deps/ and upload every dependency's intermediates. The binary goes up
    # beside its debug file for its symbol table.
    while IFS= read -r f; do
      case "$rid" in
        win-*) printf '%s\t%s\n' "$src/${f%.*}.pdb" "$src/$f" ;;
        osx-*) printf '%s\t%s\n' "$src/$f.dSYM" "$src/$f" ;;
      esac
    done < <(app_symbolicated_binaries "$rid")
    ;;

  *) usage ;;
esac

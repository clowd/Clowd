#!/usr/bin/env bash
# The app's Rust half (everything beside Prisc.Ui except the recorder/ folder),
# driven by the list in shipped.sh. Used by build.sh and build-rust.yml.
#
#   stage-app.sh packages <rid>
#       the cargo `-p` arguments that build it, except the isolated packages
#   stage-app.sh packages-isolated <rid>
#       one line of `-p` arguments per package that needs a cargo
#       invocation of its own (shipped.sh: app_packages_isolated)
#   stage-app.sh verify-isolated <rid> <cargo profile dir>
#       check the built isolated libraries: exports only prisc_*, no Sentry
#       linked in, only OS libraries as dependencies
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
  sed -n '2,19p' "$0" | sed 's/^# \{0,1\}//' >&2
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

  packages-isolated)
    for pkg in $(app_packages_isolated "$rid"); do
      echo "$pkg" | as_cargo_args
      echo
    done
    ;;

  verify-isolated)
    src=${3:?usage: stage-app.sh verify-isolated <rid> <cargo profile dir>}
    failed=0
    fail() { echo "::error::$*" >&2; failed=1; }
    for pkg in $(app_packages_isolated "$rid"); do
      lib="$src/$(rid_cdylib "$rid" "$pkg")"
      if [ ! -f "$lib" ]; then fail "$lib was not built"; continue; fi
      echo "==> $lib"
      case "$rid" in
        osx-*)
          # Exports: every global defined symbol is a prisc_ export (C names carry a leading _).
          exports=$(nm -gU "$lib" | awk 'NF { print $NF }')
          [ -n "$(echo "$exports" | grep '^_prisc_' || true)" ] || fail "$lib exports no _prisc_ symbol"
          foreign=$(echo "$exports" | grep -v '^_prisc_' || true)
          [ -z "$foreign" ] || fail "$lib exports symbols other than _prisc_*: $(echo "$foreign" | tr '\n' ' ')"
          # Sentry must never be linked in-process (crates/prisc_interop/README.md, rule D3).
          sentry=$(nm -a "$lib" | grep -ci sentry || true)
          [ "$sentry" -eq 0 ] || fail "$lib links Sentry ($sentry symbols); was it built together with another package?"
          # The install name keeps CI paths out of the dylib; dependencies are OS libraries only.
          id=$(otool -D "$lib" | sed -n '2p')
          [ "$id" = "@rpath/$(rid_cdylib "$rid" "$pkg")" ] || fail "$lib install name is '$id', not @rpath/$(rid_cdylib "$rid" "$pkg")"
          deps=$(otool -L "$lib" | sed '1d' | awk '{ print $1 }' | grep -v -x -F "$id" || true)
          other=$(echo "$deps" | grep -v -E '^(/usr/lib/|/System/Library/)' | sed '/^$/d' || true)
          [ -z "$other" ] || fail "$lib depends on non-OS libraries: $(echo "$other" | tr '\n' ' ')"
          echo "exports: $(echo "$exports" | grep -c '^_prisc_') prisc_ symbols; dependencies: $(echo "$deps" | sed "/^$/d" | wc -l | tr -d " ")"
          ;;
        win-*)
          dumpbin=${DUMPBIN:-}
          if [ -z "$dumpbin" ]; then
            vswhere="/c/Program Files (x86)/Microsoft Visual Studio/Installer/vswhere.exe"
            found=$("$vswhere" -latest -products '*' -find 'VC\Tools\MSVC\**\bin\Hostx64\x64\dumpbin.exe' | tr -d '\r' | head -1)
            [ -n "$found" ] || { echo "::error::dumpbin.exe not found through vswhere" >&2; exit 1; }
            dumpbin=$(cygpath -u "$found")
          fi
          # Exports: the export table names prisc_ functions only.
          exports=$("$dumpbin" -nologo -exports "$lib" | tr -d '\r' |
            awk '$1 ~ /^[0-9]+$/ && $2 ~ /^[0-9A-Fa-f]+$/ && length($3) >= 8 && $3 ~ /^[0-9A-Fa-f]+$/ { print $4 }')
          [ -n "$(echo "$exports" | grep '^prisc_' || true)" ] || fail "$lib exports no prisc_ function"
          foreign=$(echo "$exports" | grep -v '^prisc_' | sed '/^$/d' || true)
          [ -z "$foreign" ] || fail "$lib exports functions other than prisc_*: $(echo "$foreign" | tr '\n' ' ')"
          # Dependencies (direct and delay-loaded) are OS DLLs only: present in System32 or an
          # API set the loader resolves, and never the CRT, which crt-static links in.
          deps=$("$dumpbin" -nologo -dependents "$lib" | tr -d '\r' | awk '$0 ~ /^ +[^ ]+\.[dD][lL][lL] *$/ { print $1 }')
          [ -n "$deps" ] || fail "$lib: dumpbin listed no dependencies"
          sys32="$(cygpath -u "${SYSTEMROOT:-C:\\Windows}")/System32"
          for dep in $deps; do
            lower=$(echo "$dep" | tr '[:upper:]' '[:lower:]')
            case "$lower" in
              vcruntime*|msvcp*|ucrtbase*|api-ms-win-crt-*) fail "$lib depends on the dynamic CRT ($dep); crt-static should have linked it in" ;;
              api-ms-win-*|ext-ms-win-*) ;;
              *) [ -f "$sys32/$dep" ] || fail "$lib depends on $dep, which is not a System32 DLL" ;;
            esac
          done
          # Sentry must never be linked in-process (crates/prisc_interop/README.md, rule D3). A PE
          # image keeps no symbol names; its pdb names every linked function, like nm -a on macOS.
          pdb="${lib%.*}.pdb"
          if [ -f "$pdb" ]; then
            sentry=$(grep -a -c -i sentry "$pdb" || true)
            [ "$sentry" -eq 0 ] || fail "$lib links Sentry ($sentry pdb records); was it built together with another package?"
          else
            fail "$pdb is missing, so the Sentry check cannot run"
          fi
          echo "exports: $(echo "$exports" | grep -c '^prisc_') prisc_ functions; dependencies: $(echo "$deps" | tr '\n' ' ')"
          ;;
      esac
    done
    exit "$failed"
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

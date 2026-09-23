#!/usr/bin/env bash
# Runs a staged Linux bundle end to end, from a copy outside the checkout so
# nothing can resolve through the build tree. Used twice by CI: in the build
# image right after staging, and on plain ubuntu-22.04 / ubuntu-24.04 runners
# that have only runtime packages installed (the portability check).
#
#   tools/linux-build/verify-bundle.sh <bundle dir> <ffprobe> [work dir]
#
# <ffprobe> is the BtbN ffprobe from the FFmpeg archive obs-sys pinned; it is
# run against the bundle's own FFmpeg libraries (LD_LIBRARY_PATH), so the
# machine needs no FFmpeg of its own. Needs xvfb-run, and Mesa's llvmpipe
# (EGL + DRI) for libobs's OpenGL renderer under Xvfb.
set -euo pipefail

bundle=${1:?usage: verify-bundle.sh <bundle dir> <ffprobe> [work dir]}
ffprobe_bin=${2:?usage: verify-bundle.sh <bundle dir> <ffprobe> [work dir]}
work=${3:-$(mktemp -d)}

moved="$work/obs-express-relocated"
out="$work/smoke"
rm -rf "$moved" "$out"; mkdir -p "$out"
cp -a "$bundle" "$moved"
ffprobe() { LD_LIBRARY_PATH="$moved" "$ffprobe_bin" "$@"; }

# 1. ldd: nothing "not found", libobs/FFmpeg only from the bundle, and every
#    symbol of every ELF file resolvable (`ldd -r`), which is what catches a
#    system library too old for what we linked against (e.g. a PipeWire or
#    GLib function newer than this distro ships).
bad=0
while IFS= read -r -d '' f; do
  [ "$(head -c 4 "$f" | od -An -c | tr -d ' ')" = '177ELF' ] || continue
  rel=${f#"$moved"/}
  deps=$(ldd -r "$f" 2>&1 || true)
  if grep -q 'not found' <<< "$deps"; then
    echo "::error::$rel: unresolved libraries"; grep 'not found' <<< "$deps"; bad=1
  fi
  if grep -E '^\s*lib(obs|av[a-z]*|sw[a-z]*|postproc)[.-]' <<< "$deps" | grep -v "=> $moved/"; then
    echo "::error::$rel: libobs/FFmpeg resolved from outside the bundle"; bad=1
  fi
  if grep -q 'undefined symbol' <<< "$deps"; then
    echo "::error::$rel: undefined symbols"; grep 'undefined symbol' <<< "$deps" | head -20; bad=1
  fi
done < <(find "$moved" -type f -print0)
[ "$bad" -eq 0 ]
echo "ldd: every ELF file in the relocated bundle resolves, with all symbols defined"

# 2. Record ~3 s under Xvfb, then `quit` on stdin. Without a pulse server the
#    audio sources still create and record silence, which is enough to prove
#    the AAC tracks are wired.
record() {
  local name=$1; shift
  xvfb-run -a -s "-screen 0 1280x720x24 +extension RANDR" \
    sh -c "(sleep 3; echo quit) | '$moved/obs-express' $* > '$out/$name.out' 2> '$out/$name.err'" \
    || { echo "::error::$name recording exited non-zero"; tail -40 "$out/$name.err"; exit 1; }
  # Key order in the JSON line is not part of the protocol.
  grep '"type":"stopped_recording"' "$out/$name.out" | grep -q '"code":0[,}]' \
    || { echo "::error::$name: no successful stopped_recording"; cat "$out/$name.out"; tail -40 "$out/$name.err"; exit 1; }
}
streams() {
  ffprobe -v error -count_frames -show_entries stream=codec_name,width,height,nb_read_frames \
    -of csv=p=0 "$1"
}

record single --output "$out/single.mp4" --fps 30 --speaker default --microphone default
s=$(streams "$out/single.mp4"); echo "single.mp4:"; echo "$s"
grep -Eq '^h264,1280,720,([3-9][0-9]|[1-9][0-9]{2,})$' <<< "$s" \
  || { echo "::error::single.mp4: expected >= 30 frames of 1280x720 h264"; exit 1; }
grep -q '^aac' <<< "$s" || { echo "::error::single.mp4: no AAC track"; exit 1; }

record multi --output "$out/multi.mp4" --region 100,50,640,480 --fps 30 \
  --multi-track --webcam test --speaker default --microphone default
s=$(streams "$out/multi.mp4"); echo "multi.mp4:"; echo "$s"
[ "$(grep -c '^h264' <<< "$s")" -eq 2 ] || { echo "::error::multi.mp4: expected 2 video tracks"; exit 1; }
[ "$(grep -c '^aac' <<< "$s")" -eq 2 ]  || { echo "::error::multi.mp4: expected 2 audio tracks"; exit 1; }
grep -q '^h264,640,480,' <<< "$s" || { echo "::error::multi.mp4: screen track is not 640x480"; exit 1; }

# 3. vid2gif on the recording, from the relocated bundle.
"$moved/vid2gif" "$out/single.mp4" "$out/single.gif" --quality fair | tail -1
[ "$(head -c 6 "$out/single.gif")" = "GIF89a" ] || { echo "::error::vid2gif did not produce a GIF"; exit 1; }

# 4. Out-of-scope features fail fast instead of misbehaving, and the
#    share-region binary (not built on Linux) is not shipped.
if [ -e "$moved/clowd_share_region" ]; then
  echo "::error::clowd_share_region is not built on Linux and must not be in the bundle"; exit 1
fi
code=0; "$moved/obs-express" --output "$out/x.mp4" --tracker 2> "$out/tracker.err" || code=$?
[ "$code" -eq 2 ] || { echo "::error::--tracker should exit 2 on Linux, got $code"; exit 1; }
echo "Smoke OK ($(. /etc/os-release && echo "$PRETTY_NAME"), $(ldd --version | sed -n 1p))"

#!/usr/bin/env bash
# Measure cold-process startup latency of the capture overlay.
#
# Usage: ./tools/bench-startup.sh [runs]        # default 10 runs
#
# Environment:
#   COLD=1     refuse to run and explain why a cold-shader-cache measurement
#              is not available on macOS (see the COLD section below)
#   SETTLE=n   seconds to sleep between runs (default 1) so WindowServer,
#              the display link and the OS file cache settle between samples
#   KEEP=1     keep the per-run logs in the output directory
#
# WARNING: each run opens a real fullscreen borderless overlay on every
# monitor. `--bench-startup` tears it down one event-loop turn after the show
# gate opens, so each flash is brief, but the screen IS taken over N times.
# Do not run this over a screen share you care about.
#
# The binary emits one multi-line `startup` log record from inside the show
# gate (telemetry/startup.rs::report). This script runs the binary N times,
# parses that record out of each run, and reports the distribution of every
# stage's absolute offset from process entry.

set -euo pipefail

RUNS="${1:-10}"
SETTLE="${SETTLE:-1}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BIN="$REPO_ROOT/target/release/clowd_capture"

if ! [[ "$RUNS" =~ ^[0-9]+$ ]] || [[ "$RUNS" -lt 1 ]]; then
  echo "error: run count must be a positive integer (got '$RUNS')" >&2
  exit 64
fi

# ---------------------------------------------------------------- COLD ----
# There is no reliable, supported way to force a cold shader cache on macOS,
# so this script does not pretend to offer one.
#
# What we would need to clear does not belong to us: wgpu 30's Metal backend
# keeps no on-disk pipeline cache of its own, so every compile that matters
# here happens inside Apple's Metal compiler service, which caches into
# undocumented per-user locations under ~/Library/Caches (com.apple.metal*)
# plus state held live by the running MTLCompilerService. Deleting those paths
# is unsupported, is not guaranteed to be the whole cache, does not evict what
# the already-running service holds in memory, and Apple has changed the layout
# between releases. A "cold" number produced that way would be unreproducible
# and would quietly become a warm number the day the layout changes.
#
# For a genuinely cold first-ever-launch measurement, use a fresh user account
# or a fresh VM snapshot, and take exactly one sample.
#
# (On Windows the equivalent knobs do exist and are vendor-owned rather than
# ours: %LOCALAPPDATA%\D3DSCache and the vendor caches such as NV_Cache /
# AMD's DxCache. That path is out of scope for this script, which runs the
# macOS binary.)
if [[ "${COLD:-0}" != "0" ]]; then
  cat >&2 <<'COLDMSG'
COLD=1 is not supported.

There is no reliable way to force a cold Metal shader cache on macOS: the
cache is owned by Apple's compiler service in undocumented locations, and
clearing it by hand is neither complete nor stable across OS releases. See
the COLD section in this script for the details.

This harness measures the WARM case, which is what a user hitting PrintScreen
on a machine that has run the tool before actually experiences. For a true
cold measurement, use a fresh user account or VM snapshot and take one sample.
COLDMSG
  exit 2
fi

# --------------------------------------------------------------- build ----
echo "==> building release binary"
cargo build --release -p clowd_capture

if [[ ! -x "$BIN" ]]; then
  echo "error: expected binary at $BIN" >&2
  exit 1
fi

OUT_DIR="$(mktemp -d "${TMPDIR:-/tmp}/bench-startup.XXXXXX")"
cleanup() {
  if [[ "${KEEP:-0}" != "0" ]]; then
    echo "logs kept in $OUT_DIR"
  else
    rm -rf "$OUT_DIR"
  fi
}
trap 'if [[ -n "${BENCH_PID:-}" ]]; then kill -KILL "$BENCH_PID" 2>/dev/null || true; fi; cleanup' EXIT

# ---------------------------------------------------------------- runs ----
cat <<BANNER

==> about to run $RUNS fullscreen overlay launches
    Each run briefly takes over every display. Ctrl-C now to abort.
BANNER
for i in 3 2 1; do printf '\r    starting in %ds ' "$i"; sleep 1; done
printf '\r%*s\r' 40 ''

# `--bench-startup` is supposed to tear the overlay down one event-loop turn
# after the show gate. If that path ever breaks, an unguarded run leaves a
# fullscreen borderless window covering every display with no way to dismiss
# it. Never invoke the binary without this wrapper. macOS ships no `timeout(1)`,
# hence the manual poll rather than coreutils.
RUN_TIMEOUT="${RUN_TIMEOUT:-20}"
BENCH_PID=""

run_guarded() {
  local log="$1" waited=0
  "$BIN" --bench-startup >"$log" 2>&1 &
  BENCH_PID=$!
  while kill -0 "$BENCH_PID" 2>/dev/null; do
    if (( waited >= RUN_TIMEOUT * 10 )); then
      kill -TERM "$BENCH_PID" 2>/dev/null || true
      sleep 1
      kill -KILL "$BENCH_PID" 2>/dev/null || true
      wait "$BENCH_PID" 2>/dev/null || true
      BENCH_PID=""
      return 124
    fi
    sleep 0.1
    waited=$((waited + 1))
  done
  local code=0
  wait "$BENCH_PID" || code=$?
  BENCH_PID=""
  return "$code"
}

failures=0
for ((i = 1; i <= RUNS; i++)); do
  printf '==> run %d/%d ... ' "$i" "$RUNS"
  # Both streams: the log (TerminalMode::Stderr) and any crash diagnosis are
  # on stderr; stdout carries only the standby protocol lines, if any.
  if run_guarded "$OUT_DIR/run-$i.log"; then
    printf 'ok\n'
  else
    code=$?
    if [[ "$code" -eq 124 ]]; then
      printf 'TIMED OUT after %ds (killed)\n' "$RUN_TIMEOUT"
    else
      printf 'FAILED (exit %d)\n' "$code"
    fi
    failures=$((failures + 1))
  fi
  if [[ "$i" -lt "$RUNS" ]]; then
    sleep "$SETTLE"
  fi
done

if [[ "$failures" -gt 0 ]]; then
  echo "warning: $failures/$RUNS run(s) exited non-zero; parsing whatever they produced" >&2
fi

# --------------------------------------------------------------- parse ----
# Kept in python3 rather than awk purely for the percentiles; no cargo deps,
# no pip deps, stdlib only.
python3 - "$OUT_DIR" "$RUNS" <<'PYEOF'
import math
import os
import re
import sys

out_dir, runs = sys.argv[1], int(sys.argv[2])

# `startup 123.45ms (offsets in ms from process entry; ...)` opens the record.
HEADER = re.compile(r"startup\s+([0-9]+\.[0-9]+)ms\s+\(offsets in ms")
# `  name                    12.34     5.67` — name, absolute offset, delta.
ROW = re.compile(r"^(\s+)(\S(?:.*\S)?)\s+(-?[0-9]+\.[0-9]{2})\s+(-?[0-9]+\.[0-9]{2})\s*$")
WORKER = re.compile(r"^\s+worker\s+(\d+)\s*$")
GATE = re.compile(r"gate\s+([0-9]+\.[0-9]+)ms")

# Stage key -> ordinal, so the summary prints in the order the binary printed
# it rather than in whatever order dict insertion happened to produce.
order = {}
samples = {}


def record(key, value):
    order.setdefault(key, len(order))
    samples.setdefault(key, []).append(value)


def parse(path):
    with open(path, "r", errors="replace") as fh:
        lines = fh.read().splitlines()

    start = next((i for i, ln in enumerate(lines) if HEADER.search(ln)), None)
    if start is None:
        return False

    per_run = {}
    per_run["total"] = float(HEADER.search(lines[start]).group(1))

    # The report is a single log record, so simplelog prefixes only its first
    # line; every continuation line is indented. The first unindented,
    # non-empty line after it is the next record.
    section = "main"
    for ln in lines[start + 1:]:
        if ln.strip() and not ln.startswith(" "):
            break
        if "background (" in ln:
            section = "bg"
            m = GATE.search(ln)
            if m:
                per_run["background gate"] = float(m.group(1))
            continue
        m = WORKER.match(ln)
        if m:
            section = "worker"
            continue
        m = ROW.match(ln)
        if not m:
            continue
        indent, name, at, _delta = m.groups()
        if name == "stage":  # the column header row
            continue
        if section == "main":
            key = name
        elif section == "bg":
            key = "bg." + name
        else:
            # Worker rows are folded across monitors by taking the max: the
            # fleet is not ready for a stage until its slowest member is, and
            # keeping them separate would make the table's shape depend on how
            # many displays the machine happens to have.
            key = "worker." + name
        value = float(at)
        per_run[key] = max(per_run[key], value) if key in per_run else value

    # Emit in report order, but make sure `total` sorts last.
    for key in per_run:
        if key != "total":
            record(key, per_run[key])
    record("total", per_run["total"])
    return True


parsed = 0
for i in range(1, runs + 1):
    path = os.path.join(out_dir, f"run-{i}.log")
    if os.path.exists(path) and parse(path):
        parsed += 1

if parsed == 0:
    print("\nno startup reports parsed — the runs produced no `startup ...ms` record.")
    print("Check the logs (re-run with KEEP=1) — the overlay may have failed before the show gate.")
    sys.exit(1)


def pct(sorted_vals, q):
    """Nearest-rank percentile; with few runs this beats interpolation because
    every printed number is a real observation."""
    if not sorted_vals:
        return float("nan")
    k = max(1, math.ceil(q * len(sorted_vals)))
    return sorted_vals[k - 1]


def median(sorted_vals):
    n = len(sorted_vals)
    mid = n // 2
    if n % 2:
        return sorted_vals[mid]
    return (sorted_vals[mid - 1] + sorted_vals[mid]) / 2.0


print(f"\n==> {parsed}/{runs} run(s) produced a startup report")
print("    all figures are ms from process entry (absolute offsets, not deltas)")
print("    worker.* rows are the max across all monitors within each run\n")

name_w = max(24, max(len(k) for k in order) + 2)
print(f"{'stage':<{name_w}}{'n':>4}{'min':>10}{'median':>10}{'p90':>10}{'max':>10}")
print("-" * (name_w + 44))
for key in sorted(order, key=lambda k: order[k]):
    vals = sorted(samples[key])
    if key == "total":
        print("-" * (name_w + 44))
    print(f"{key:<{name_w}}{len(vals):>4}{vals[0]:>10.2f}{median(vals):>10.2f}{pct(vals, 0.90):>10.2f}{vals[-1]:>10.2f}")

fp = samples.get("first_present")
if fp:
    vals = sorted(fp)
    print(
        f"\ntotal to first present: median {median(vals):.2f}ms  "
        f"(min {vals[0]:.2f}  p90 {pct(vals, 0.90):.2f}  max {vals[-1]:.2f}) over {len(vals)} run(s)"
    )
else:
    print(
        "\ntotal to first present: NOT RECORDED — no run marked `first_present`.\n"
        "  That mark is only set when frame 0 actually reached queue.present(); on macOS a\n"
        "  window that AppKit still considers occluded makes wgpu return SurfaceError::Occluded\n"
        "  and frame 0 presents nothing. Its absence here is a real finding, not a parse bug."
    )
PYEOF

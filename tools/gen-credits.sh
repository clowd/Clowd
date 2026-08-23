#!/usr/bin/env bash
# Regenerate the open-source credits list shown on the About page.
#
# Usage: ./tools/gen-credits.sh
#
# Produces clowd_ui/Clowd.Ui/Assets/credits.json (embedded as an Avalonia
# resource and rendered by AboutPage). Re-run this whenever dependencies
# change — the output is deterministic (sorted, deduplicated) so diffs stay
# clean and only reflect real dependency changes.
#
# Two sections are emitted:
#   * "capture" — the direct Rust dependencies of the workspace crates
#                 (pulled from `cargo metadata`, license = crate SPDX field),
#                 plus the manual EXTRA_ENTRIES below
#   * "ui"      — the direct NuGet packages of the shipped .NET projects
#                 (license/url read from each package's cached .nuspec)
#
# Requirements: cargo, dotnet, jq. Install jq with:
#   winget install jqlang.jq   (Windows)
#   brew install jq            (macOS)
#   apt-get install jq         (Debian/Ubuntu)
#
# Set JQ=/path/to/jq to override the jq binary (e.g. before a PATH refresh).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/clowd_ui/Clowd.Ui/Assets/credits.json"
JQ="${JQ:-jq}"

# .NET projects that ship inside the Clowd.Ui executable. Test/benchmark/tool
# projects are intentionally excluded — their packages are not distributed.
SHIPPED_CSPROJ=(
    "clowd_ui/Clowd.Ui/Clowd.Ui.csproj"
    "clowd_ui/Clowd.Shared/Clowd.Shared.csproj"
    "clowd_ui/Clowd.Upload/Clowd.Upload.csproj"
    "clowd_ui/Clowd.Drawing/Clowd.Drawing.csproj"
)

# Some older packages ship a nuspec without an SPDX <license type="expression">
# (they use a license *file* or a deprecated licenseUrl), so the license name
# cannot be read automatically. Map the known OSS license for those here to
# keep the list complete. Verify the license before adding an entry.
declare -A LICENSE_OVERRIDES=(
    [YamlDotNet]="MIT"           # repo LICENSE.txt is MIT
    [WindowsAzure.Storage]="MIT" # azure-storage-net LICENSE.txt is MIT
)

# Shipped components no dependency manifest can enumerate: the model weights
# clowd_ai embeds (see clowd_ai/assets/models/README.md) and the
# ONNX Runtime the ort crate links statically into it. Merged into the Rust
# section.
# Fields are name|version|license|url; version may be empty.
EXTRA_ENTRIES=(
    "RobustVideoMatting|1.0.0|GPL-3.0|https://github.com/PeterL1n/RobustVideoMatting"
    "DPDFNet||Apache-2.0|https://huggingface.co/Ceva-IP/DPDFNet"
    "PaddleOCR PP-OCRv6 models||Apache-2.0|https://github.com/PaddlePaddle/PaddleOCR"
    "ONNX Runtime||MIT|https://github.com/microsoft/onnxruntime"
)

command -v cargo >/dev/null || { echo "error: cargo not found on PATH" >&2; exit 1; }
command -v dotnet >/dev/null || { echo "error: dotnet not found on PATH" >&2; exit 1; }
command -v "$JQ" >/dev/null || { echo "error: jq not found (set JQ=/path/to/jq or install it)" >&2; exit 1; }

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

# ---------------------------------------------------------------------------
# Rust: direct dependencies of the workspace crates (normal + build, no dev).
# ---------------------------------------------------------------------------
echo "==> Collecting Rust dependencies (cargo metadata)"
cargo metadata --format-version 1 --manifest-path "$ROOT/Cargo.toml" \
    | "$JQ" -c '
        (INDEX(.packages[]; .id)) as $pkgs
        | .workspace_members as $members
        | [ .resolve.nodes[]
            | select(.id as $id | $members | index($id))
            | .deps[]
            | select([.dep_kinds[].kind] | any(. == null or . == "build"))
            | $pkgs[.pkg]
            | {
                name,
                version,
                license: (.license // ""),
                url: (if (.repository // "") != "" then .repository
                      else "https://crates.io/crates/\(.name)" end)
              }
          ]
        | unique_by(.name) | sort_by(.name | ascii_downcase)
      ' > "$TMP/rust.json"

# Fold the manual entries in with the generated ones.
: > "$TMP/extra.jsonl"
for entry in "${EXTRA_ENTRIES[@]}"; do
    IFS='|' read -r name version license url <<< "$entry"
    "$JQ" -n --arg name "$name" --arg version "$version" --arg license "$license" --arg url "$url" \
        '{name:$name, version:$version, license:$license, url:$url}' >> "$TMP/extra.jsonl"
done
"$JQ" -s '(.[0] + .[1:]) | unique_by(.name) | sort_by(.name | ascii_downcase)' \
    "$TMP/rust.json" "$TMP/extra.jsonl" > "$TMP/rust.merged.json"
mv "$TMP/rust.merged.json" "$TMP/rust.json"

# ---------------------------------------------------------------------------
# .NET: direct NuGet packages of the shipped projects. License + url come from
# each package's cached .nuspec (SPDX license expression / repository url).
# ---------------------------------------------------------------------------
echo "==> Restoring .NET packages"
dotnet restore "$ROOT/clowd_ui/Clowd.Ui/Clowd.Ui.csproj" >/dev/null

NUGET="$(dotnet nuget locals global-packages --list | sed -E 's/^[^:]*: *//' | tr -d '\r')"
echo "==> Reading NuGet package licenses from $NUGET"

# "Id Version" for every non-Debug PackageReference across the shipped projects.
mapfile -t PKGS < <(
    for proj in "${SHIPPED_CSPROJ[@]}"; do cat "$ROOT/$proj"; done \
        | grep -E '<PackageReference' \
        | grep -viE 'Condition=[^>]*Debug' \
        | sed -E 's/.*Include="([^"]+)".*Version="([^"]+)".*/\1 \2/' \
        | sort -u
)

: > "$TMP/dotnet.jsonl"
for entry in "${PKGS[@]}"; do
    id="${entry%% *}"
    ver="${entry##* }"
    low="$(echo "$id" | tr '[:upper:]' '[:lower:]')"
    nuspec="$NUGET/$low/$ver/$low.nuspec"

    license=""
    url=""
    if [ -f "$nuspec" ]; then
        # SPDX license expression, e.g. <license type="expression">MIT</license>
        license="$(grep -oiE '<license type="expression">[^<]*' "$nuspec" | sed -E 's/.*>//' | tr -d '\r' | head -1 || true)"
        # Fall back to an SPDX id embedded in a licenses.nuget.org URL.
        if [ -z "$license" ]; then
            license="$(grep -oiE 'licenses\.nuget\.org/[^"<]+' "$nuspec" | sed -E 's#.*/##' | tr -d '\r' | head -1 || true)"
        fi
        # Prefer the source repository, then the project homepage.
        url="$(grep -oiE '<repository[^>]*url="[^"]+"' "$nuspec" | grep -oiE 'url="[^"]+"' | sed -E 's/url="//; s/"$//' | tr -d '\r' | head -1 || true)"
        if [ -z "$url" ]; then
            url="$(grep -oiE '<projectUrl>[^<]+' "$nuspec" | sed -E 's/.*>//' | tr -d '\r' | head -1 || true)"
        fi
    else
        echo "   warning: nuspec not found for $id $ver ($nuspec)" >&2
    fi
    [ -z "$license" ] && license="${LICENSE_OVERRIDES[$id]:-}"
    [ -z "$url" ] && url="https://www.nuget.org/packages/$id"

    "$JQ" -n --arg name "$id" --arg version "$ver" --arg license "$license" --arg url "$url" \
        '{name:$name, version:$version, license:$license, url:$url}' >> "$TMP/dotnet.jsonl"
done
"$JQ" -s 'unique_by(.name) | sort_by(.name | ascii_downcase)' "$TMP/dotnet.jsonl" > "$TMP/dotnet.json"

# ---------------------------------------------------------------------------
# Assemble the two-section document.
# ---------------------------------------------------------------------------
"$JQ" -n --slurpfile ui "$TMP/dotnet.json" --slurpfile capture "$TMP/rust.json" '{
    "_comment": "Generated by tools/gen-credits.sh — do not edit by hand. Re-run the script to update.",
    sections: [
        { id: "ui",      title: "Application & Editor (Avalonia / .NET)", items: $ui[0] },
        { id: "capture", title: "Capture & AI Engines (Rust)",            items: $capture[0] }
    ]
}' > "$OUT"

RUST_COUNT="$("$JQ" 'length' "$TMP/rust.json")"
DOTNET_COUNT="$("$JQ" 'length' "$TMP/dotnet.json")"
echo "==> Wrote $OUT ($DOTNET_COUNT .NET packages, $RUST_COUNT Rust crates)"

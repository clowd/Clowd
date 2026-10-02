#!/usr/bin/env python3
"""Builds the Inter faces the Rust capture overlay (clowd_capture) bundles.

The overlay and the C# UI must draw the same Inter, so the source is not a download: it is the
exact Inter the app already ships, carved out of the Avalonia.Fonts.Inter package in the NuGet
cache (the version Clowd.Ui.csproj references). Two changes are then baked into the files,
because egui shapes every string with harfrust's default features and offers no way to turn
one on or off per run:

  * `tnum` is frozen into the cmap, so every digit has the same advance. Live readouts (the
    selection size, word counts) would otherwise shuffle sideways as their digits change. The
    C# tray asks for the same thing with FontFeatures="tnum" on its readouts.
  * `calt` is removed. Inter's contextual alternates rewrite "->" to an arrow and the "x" in
    "1x1" to a multiplication sign, which would misrepresent recognised OCR text.

Everything else (kerning, glyph coverage, hinting, names) is left as Inter ships it. Inter is
licensed under the SIL OFL 1.1 without a Reserved Font Name, so a modified copy may keep its
name; the licence is copied next to the fonts as Inter-OFL.txt.

Usage (from the repo root):
  python tools/capture-fonts/build.py [--package-dir DIR]

  --package-dir DIR  an extracted Avalonia.Fonts.Inter package (default: the NuGet cache entry
                     for the version Clowd.Ui.csproj references)

Requires fontTools: pip install fonttools
"""
import argparse
import io
import os
import re
import struct
import sys

from fontTools import subset
from fontTools.ttLib import TTFont

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
OUT_DIR = os.path.join(REPO, "clowd_capture", "assets", "fonts")
CSPROJ = os.path.join(REPO, "clowd_ui", "Clowd.Ui", "Clowd.Ui.csproj")

# PostScript name in the package -> file written next to the overlay's sources.
FACES = {
    "Inter-Regular": "Inter-Regular.ttf",
    "Inter-SemiBold": "Inter-SemiBold.ttf",
}


def default_package_dir():
    with open(CSPROJ, encoding="utf-8") as f:
        m = re.search(r'Include="Avalonia\.Fonts\.Inter"\s+Version="([^"]+)"', f.read())
    if not m:
        sys.exit("error: no Avalonia.Fonts.Inter PackageReference in " + CSPROJ)
    home = os.environ.get("NUGET_PACKAGES") or os.path.join(os.path.expanduser("~"), ".nuget", "packages")
    return os.path.join(home, "avalonia.fonts.inter", m.group(1).lower())


def find_assembly(package_dir):
    lib = os.path.join(package_dir, "lib")
    if not os.path.isdir(lib):
        sys.exit("error: %s not found; restore Clowd.Ui first (dotnet restore)" % lib)
    for tfm in sorted(os.listdir(lib), reverse=True):
        dll = os.path.join(lib, tfm, "Avalonia.Fonts.Inter.dll")
        if os.path.isfile(dll):
            return dll
    sys.exit("error: no Avalonia.Fonts.Inter.dll under " + lib)


def carve_fonts(blob):
    """Every complete TrueType file embedded in `blob`, keyed by PostScript name.

    Avalonia stores resources uncompressed, so each font sits in the assembly byte for byte; a
    candidate is a version-1.0 sfnt header whose table directory is sane and parses."""
    found = {}
    i = blob.find(b"\x00\x01\x00\x00")
    while i >= 0:
        num_tables = struct.unpack(">H", blob[i + 4:i + 6])[0]
        if 8 <= num_tables <= 64:
            end = 0
            for t in range(num_tables):
                rec = blob[i + 12 + 16 * t:i + 28 + 16 * t]
                if len(rec) < 16 or not all(32 <= c < 127 for c in rec[:4]):
                    end = 0
                    break
                offset, length = struct.unpack(">II", rec[8:16])
                end = max(end, offset + length)
            if 0 < end <= len(blob) - i:
                data = blob[i:i + end]
                try:
                    name = TTFont(io.BytesIO(data))["name"].getDebugName(6)
                except Exception:
                    name = None
                if name:
                    found[name] = data
        i = blob.find(b"\x00\x01\x00\x00", i + 1)
    return found


def single_substitutions(gsub, tag):
    """The glyph-to-glyph map of every single-substitution lookup behind feature `tag`."""
    mapping = {}
    table = gsub.table
    for record in table.FeatureList.FeatureRecord:
        if record.FeatureTag != tag:
            continue
        for index in record.Feature.LookupListIndex:
            lookup = table.LookupList.Lookup[index]
            for sub in lookup.SubTable:
                if lookup.LookupType == 7:
                    sub = sub.ExtSubTable
                if getattr(sub, "LookupType", lookup.LookupType) != 1:
                    sys.exit("error: %s lookup %d is not a single substitution" % (tag, index))
                mapping.update(sub.mapping)
    if not mapping:
        sys.exit("error: font has no %s substitutions" % tag)
    return mapping


def build(data):
    font = TTFont(io.BytesIO(data))

    tnum = single_substitutions(font["GSUB"], "tnum")
    for cmap in font["cmap"].tables:
        if cmap.isUnicode():
            for code, glyph in list(cmap.cmap.items()):
                if glyph in tnum:
                    cmap.cmap[code] = tnum[glyph]

    tags = {r.FeatureTag for r in font["GSUB"].table.FeatureList.FeatureRecord}
    tags |= {r.FeatureTag for r in font["GPOS"].table.FeatureList.FeatureRecord}
    options = subset.Options()
    options.layout_features = sorted(tags - {"calt"})
    options.name_IDs = ["*"]
    options.name_languages = ["*"]
    options.name_legacy = True
    options.notdef_outline = True
    options.glyph_names = True
    options.hinting = True
    options.legacy_kern = True
    options.drop_tables = []
    subsetter = subset.Subsetter(options)
    subsetter.populate(unicodes=font.getBestCmap().keys())
    subsetter.subset(font)

    out = io.BytesIO()
    font.save(out)
    return out.getvalue()


OFL_HEADER = """Copyright 2020 The Inter Project Authors (https://github.com/rsms/inter)

This Font Software is licensed under the SIL Open Font License, Version 1.1.
This license is copied below, and is also available with a FAQ at:
http://scripts.sil.org/OFL

The copies in this folder are modified by tools/capture-fonts/build.py: tabular figures are
the default and contextual alternates are removed.
"""


def write_licence():
    # The OFL body is the same for every OFL font; take it from the Cascadia licence already in
    # the repository rather than retyping it.
    cascadia = os.path.join(REPO, "clowd_ui", "Clowd.Ui", "Assets", "Fonts", "CascadiaCode-OFL.txt")
    with open(cascadia, encoding="utf-8") as f:
        text = f.read()
    body = text[text.index("\n\n-----") + 1:]
    with open(os.path.join(OUT_DIR, "Inter-OFL.txt"), "w", encoding="utf-8", newline="\n") as f:
        f.write(OFL_HEADER + body)


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--package-dir", default=None)
    args = parser.parse_args()

    dll = find_assembly(args.package_dir or default_package_dir())
    with open(dll, "rb") as f:
        fonts = carve_fonts(f.read())

    os.makedirs(OUT_DIR, exist_ok=True)
    for ps_name, file_name in FACES.items():
        if ps_name not in fonts:
            sys.exit("error: %s not found in %s (found: %s)" % (ps_name, dll, ", ".join(sorted(fonts))))
        data = build(fonts[ps_name])
        with open(os.path.join(OUT_DIR, file_name), "wb") as f:
            f.write(data)
        print("%s: %d bytes (from %s)" % (file_name, len(data), dll))
    write_licence()


if __name__ == "__main__":
    main()

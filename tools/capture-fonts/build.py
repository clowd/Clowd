#!/usr/bin/env python3
"""Builds the Inter faces Prisc ships: the processed pair the Rust capture overlay (prisc_capture)
bundles, and the stock weights the C# UI (Prisc.Ui) embeds.

The source is Inter 4.1's text optical size: the static `extras/ttf/Inter-*.ttf` files from the
v4.1 release zip on rsms/inter, checked against the zip's known hash. ("Inter", not
"InterDisplay", is the cut drawn for small sizes; the overlay's chrome is 9-12 pt.)

The C# copies are the release files byte for byte. Avalonia shapes with per-run features, so the
C# tray asks for tabular figures itself (FontFeatures="tnum") and nothing needs baking in.

The overlay copies get two changes, because egui shapes every string with harfrust's default
features and offers no way to turn one on or off per run:

  * `tnum` is frozen into the cmap, so every digit has the same advance. Live readouts (the
    selection size, word counts) would otherwise shuffle sideways as their digits change.
  * `calt` is removed. Inter's contextual alternates rewrite "->" to an arrow and the "x" in
    "1x1" to a multiplication sign, which would misrepresent recognised OCR text.

Everything else (kerning, glyph coverage, hinting, names) is left as Inter ships it. Inter is
licensed under the SIL OFL 1.1 without a Reserved Font Name, so a modified copy may keep its
name; the licence is copied next to each set of fonts as Inter-OFL.txt.

Usage (from the repo root):
  python tools/capture-fonts/build.py

Requires network access and fontTools: pip install fonttools
"""
import hashlib
import io
import os
import sys
import urllib.request
import zipfile

from fontTools import subset
from fontTools.ttLib import TTFont

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
CAPTURE_DIR = os.path.join(REPO, "crates", "prisc_capture", "assets", "fonts")
UI_DIR = os.path.join(REPO, "ui", "Prisc.Ui", "Assets", "Fonts", "Inter")

ZIP_URL = "https://github.com/rsms/inter/releases/download/v4.1/Inter-4.1.zip"
ZIP_SHA256 = "9883fdd4a49d4fb66bd8177ba6625ef9a64aa45899767dde3d36aa425756b11e"
ZIP_DIR = "extras/ttf/"

# The overlay draws in two weights; egui picks a face per family, so each is a family of its own.
CAPTURE_FACES = ["Inter-Regular.ttf", "Inter-SemiBold.ttf"]
# The weights Avalonia.Fonts.Inter used to provide, so every FontWeight the C# UI asks for still
# resolves to a real face rather than a synthesised one.
UI_FACES = [
    "Inter-Thin.ttf",
    "Inter-Light.ttf",
    "Inter-Regular.ttf",
    "Inter-Medium.ttf",
    "Inter-SemiBold.ttf",
    "Inter-Bold.ttf",
]


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
    # Keep the source's modified time, so rebuilding from the same release gives the same bytes.
    font = TTFont(io.BytesIO(data), recalcTimestamp=False)

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


CAPTURE_OFL_HEADER = """Copyright (c) 2016 The Inter Project Authors (https://github.com/rsms/inter)

This Font Software is licensed under the SIL Open Font License, Version 1.1.
This license is copied below, and is also available with a FAQ at:
http://scripts.sil.org/OFL

The copies in this folder are modified by tools/capture-fonts/build.py: tabular figures are
the default and contextual alternates are removed.
"""


def download():
    with urllib.request.urlopen(ZIP_URL) as r:
        data = r.read()
    actual = hashlib.sha256(data).hexdigest()
    if actual != ZIP_SHA256:
        sys.exit("error: %s has SHA-256 %s, expected %s" % (ZIP_URL, actual, ZIP_SHA256))
    return zipfile.ZipFile(io.BytesIO(data))


def write(directory, file_name, data):
    os.makedirs(directory, exist_ok=True)
    with open(os.path.join(directory, file_name), "wb") as f:
        f.write(data)
    print("%s: %d bytes" % (os.path.relpath(os.path.join(directory, file_name), REPO), len(data)))


def main():
    archive = download()
    licence = archive.read("LICENSE.txt").decode("utf-8").replace("\r\n", "\n")

    for name in CAPTURE_FACES:
        write(CAPTURE_DIR, name, build(archive.read(ZIP_DIR + name)))
    body = licence[licence.index("\n\n-----") + 1:]
    write(CAPTURE_DIR, "Inter-OFL.txt", (CAPTURE_OFL_HEADER + body).encode("utf-8"))

    for name in UI_FACES:
        write(UI_DIR, name, archive.read(ZIP_DIR + name))
    write(UI_DIR, "Inter-OFL.txt", licence.encode("utf-8"))


if __name__ == "__main__":
    main()

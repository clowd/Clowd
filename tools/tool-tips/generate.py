#!/usr/bin/env python3
"""Generates the tool demo GIFs shown in the image editor's tool-strip flyouts.

Each GIF is a faithful miniature of the Clowd image editor: a screenshot card on the dark canvas
surround and the real Clowd cursor using the tool, with the editor's own ink, shadows and selection
chrome. The tool's keyboard shortcut is not in the GIF: the flyout card (Controls/ToolTipCard)
shows it as a keycap beside the header, sourced from EditorWindow's ToolRegistry. They are
embedded as Avalonia resources from clowd_ui/Clowd.Ui/Assets/ToolTips/ and shown by
Controls/ToolTipCard.axaml (played through Controls/AnimatedGifImage.cs). See README.md next to
this script for the storyboard contract, the style rules and how to add a demo for a new tool.

One demo per file in demos/<name>.py (auto-discovered): each exports frames() returning
supersampled Frame objects; this script downsamples, quantises and saves tool-<name>.gif. The
shared framework is common.py.

Usage:
  python tools/tool-tips/generate.py [--out DIR] [--sheet PNG] [--frames DIR] [--list] [name ...]

  --out DIR      output folder (default: clowd_ui/Clowd.Ui/Assets/ToolTips, relative to the repo)
  --sheet PNG    also write a review contact sheet (6 frames per GIF) to this path
  --frames DIR   also write every output frame as DIR/<name>-NN.png for review
  --list         print the discovered demos, then exit
  name           only regenerate demos whose name contains one of these (e.g. "rect")

Requires Pillow: pip install Pillow
"""
import importlib
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import common  # noqa: E402  (tools/tool-tips must be on sys.path first)

DEFAULT_OUT = os.path.join(common.REPO_ROOT, "clowd_ui", "Clowd.Ui", "Assets", "ToolTips")
MIN_FRAMES, MAX_FRAMES = 40, 72
WARN_KB = 220
PAN_WARN_KB = 300   # the whole artwork moves, so pan legitimately costs more


def discover():
    folder = os.path.join(HERE, "demos")
    names = []
    for fn in sorted(os.listdir(folder)):
        stem, ext = os.path.splitext(fn)
        if ext == ".py" and not stem.startswith("_"):
            names.append(stem)
    return names


def load(stem):
    mod = importlib.import_module("demos." + stem)
    if hasattr(mod, "KEY"):
        raise ValueError(f"{stem}: KEY is no longer a demo's business; the flyout card shows the "
                         "shortcut from EditorWindow's ToolRegistry")
    if not callable(getattr(mod, "frames", None)):
        raise ValueError(f"{stem}: no frames() function")
    return mod


def render(stem, mod):
    fs = mod.frames()
    if not (MIN_FRAMES <= len(fs) <= MAX_FRAMES):
        raise ValueError(f"{stem}: {len(fs)} frames, expected {MIN_FRAMES} to {MAX_FRAMES}")
    out = []
    for i, f in enumerate(fs):
        if not isinstance(f, common.Frame):
            raise ValueError(f"{stem}: frame {i} is not a Frame")
        if f.img.size != (common.W, common.H):
            raise ValueError(f"{stem}: frame {i} is {f.img.size}, expected {(common.W, common.H)}")
        out.append(common.finish(f.img))
    return out


def main(argv):
    out, sheet, frames_dir, list_only, only = DEFAULT_OUT, None, None, False, []
    i = 0
    while i < len(argv):
        a = argv[i]
        if a == "--out":
            out = argv[i + 1]
            i += 2
        elif a == "--sheet":
            sheet = argv[i + 1]
            i += 2
        elif a == "--frames":
            frames_dir = argv[i + 1]
            i += 2
        elif a == "--list":
            list_only = True
            i += 1
        elif a in ("-h", "--help"):
            print(__doc__)
            return 0
        else:
            only.append(a)
            i += 1

    names = [n for n in discover() if not only or any(o in n for o in only)]
    if list_only:
        for n in names:
            try:
                load(n)
                print(f"  {n:<12} tool-{n}.gif")
            except Exception as ex:
                print(f"  {n:<12} ERROR: {ex}")
        return 0

    os.makedirs(out, exist_ok=True)
    produced, failed = {}, []
    for n in names:
        try:
            mod = load(n)
            frames = render(n, mod)
            path = os.path.join(out, f"tool-{n}.gif")
            size = common.save_gif(frames, path)
            kb = size // 1024
            warn = "" if kb <= (PAN_WARN_KB if n == "pan" else WARN_KB) else "   WARNING: over the size budget"
            print(f"  tool-{n}.gif: {len(frames)} frames, {kb} KB{warn}")
            produced[n] = frames
            if frames_dir:
                common.dump_frames(frames, frames_dir, n)
        except Exception as ex:
            failed.append(n)
            print(f"  {n}: FAILED: {ex}")
            import traceback
            traceback.print_exc()
    if sheet and produced:
        common.contact_sheet(produced, sheet)
        print("contact sheet:", sheet)
    if failed:
        print("failed:", ", ".join(failed))
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))

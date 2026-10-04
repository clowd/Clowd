# Tool tip demo GIFs

The looping demos shown in the rich flyouts behind the buttons on the image editor's drawing tool
strip (Pan, Selection, Rectangle, Filled Rectangle, Ellipse, Line, Arrow, Measure, Pen, Brush, Step
Count, Text, Sticky Note, Obscure). Each flyout is a header with the tool's keyboard shortcut as a
keycap beside it (top-right of the card), a short description, and a demo GIF.

The shortcut is the card's business, not the GIF's: `ToolTipCard.Shortcut` draws the keycap, and
the letter comes from the `ToolRegistry` entry in `EditorWindow.axaml.cs`, the same field the bare
key handler reads. Change the letter there and both the key and the flyout follow; no GIF needs
regenerating.

This folder holds the generator. The GIFs are never hand-edited: change the demo (or the framework),
re-run the generator, commit the script and the regenerated GIFs together.

The video editor's track tips (`tools/track-tips/`) are the sibling family: same frame size, same
timing, same encoding, same flyout control. The two generators share conventions but not code, so
each can be edited without touching the other.

## Where things live

| What | Path |
| --- | --- |
| CLI, demo discovery, encoding | `tools/tool-tips/generate.py` |
| The shared framework every demo draws with | `tools/tool-tips/common.py` |
| One demo per tool | `tools/tool-tips/demos/<name>.py` |
| Output GIFs, embedded as Avalonia resources | `clowd_ui/Clowd.Ui/Assets/ToolTips/tool-*.gif` |
| The flyout control (header, shortcut keycap, description, demo, disabled footer) | `clowd_ui/Clowd.Ui/Controls/ToolTipCard.axaml(.cs)` |
| The bare ToolTip theme the card sits in | `RichTipToolTipTheme` in `clowd_ui/Clowd.Ui/Assets/AppResources.axaml` |
| The GIF player (streams frames through SkiaSharp `SKCodec`) | `clowd_ui/Clowd.Ui/Controls/AnimatedGifImage.cs` |
| Where the tips are attached to the buttons, the copy and the shortcuts | `clowd_ui/Clowd.Ui/Editor/EditorWindow.axaml.cs`: `ToolRegistry` (`Shortcut`, `Description`, `DemoName`), `CreateToolButton` |
| The bare-key handler, derived from the registry's `Shortcut` | `EditorWindow.axaml.cs`: `ToolShortcuts`, `OnTunnelKeyDown` |
| The cursors the demos paste | `clowd_ui/Clowd.Drawing/Cursors/*.cur` |
| The graphics the ink painters mimic | `clowd_ui/Clowd.Drawing/Graphics/*.cs`, tools in `clowd_ui/Clowd.Drawing/Tools/*.cs` |

`EditorWindow` resolves each demo as `avares://Clowd.Ui/Assets/ToolTips/tool-{DemoName}.gif`. A
missing file just hides the demo area (the card collapses to header + description), so the app builds
and runs with or without the GIFs.

## Tooling

- Python 3 with Pillow: `pip install Pillow` (12.3 was used; any Pillow 10+ should work).
- Run from the repo root:
  - `python tools/tool-tips/generate.py` regenerates every demo into the assets folder.
  - `python tools/tool-tips/generate.py pen count` regenerates only the demos whose names contain
    those words.
  - `python tools/tool-tips/generate.py --list` prints the discovered demos.
  - `--sheet PATH.png` also writes a review contact sheet (6 evenly spaced frames per GIF, labelled
    with the frame index); `--frames DIR` writes every output frame as `DIR/<name>-NN.png`; `--out DIR`
    overrides the output folder. The exit code is 1 when any demo failed validation.
- Fonts: Inter from `clowd_capture/assets/fonts` (the editor's own face) and Cascadia Mono from
  `clowd_ui/Clowd.Ui/Assets/Fonts` (the count badge), falling back to Arial / DejaVu / Helvetica and
  then Pillow's built-in font. Regenerate on the same machine if pixel-identical output matters.
- Cursors: the real Clowd cursor art, read straight out of the `.cur` files (the 256 px PNG frame
  and its hotspot), so the pointer in every demo is the one the editor shows.
- Always review before committing (see Review workflow). Typical things to catch: clipped shapes,
  text too small to read, a loop that jumps, a one-shot tool whose cursor forgets to revert.

## Adding a demo for a new tool

1. Create `demos/<name>.py` (see Demo module contract). `<name>` is the GIF's stem and the registry's
   `DemoName`. Give it `frames()`. Do not give it a `KEY`: the generator rejects one, because the
   shortcut is drawn by the card, not the GIF.
2. `python tools/tool-tips/generate.py <name> --sheet <scratch>/sheet.png --frames <scratch>/frames`,
   then look at the sheet and the frames and iterate.
3. In `EditorWindow.axaml.cs`, add a `ToolRegistryEntry` with `DisplayName` (the header),
   `Shortcut` (the bare key that selects the tool, and the keycap's label), `Description` (the copy)
   and `DemoName = "<name>"`. `CreateToolButton` wraps them in a `ToolTipCard` inside a `ToolTip`
   wearing `RichTipToolTipTheme`. The key handler is built from the registry, so a duplicate
   shortcut trips a `Debug.Assert` at startup rather than silently shadowing another tool.
4. Build `clowd_ui/Clowd.Ui` and hover the button in the editor to check placement and playback.

## Demo module contract

```python
# tools/tool-tips/demos/rectangle.py
from common import *          # generate.py puts tools/tool-tips on sys.path

def frames():
    """Returns the list of Frame objects, supersampled (W x H), in order. The framework
    downsamples, quantises and saves. 40 to 72 frames."""
    sb = Storyboard()
    sb.add("open", OPEN)      # 0-2: the shared opening rest, the tool cursor already on the press point
    sb.add("press", 2)        # 3-4
    sb.add("drag", 16)        # 5-20
    sb.add("release", 2)      # 21-22: the tool reverts to Selection, the shape stays selected
    sb.add("hold", 14)        # 23-36, then loop
    start, end = (72, 42), (178, 70)
    out = []
    for i in range(sb.total):
        name, t, k = sb.at(i)
        img, d = new_frame()
        # the far corner follows the pressed drag, then stays put
        far = drag(start, end, t) if sb.during(i, "drag") else (end if sb.after(i, "drag") else start)
        pos = far
        if i >= sb.start("press"):
            box = (start[0], start[1], far[0], far[1])
            ink_rect(img, box, radius=8)           # ink first, with its drop shadow
            rect_handles(d, box)                   # new shapes are selected on creation
        for beat in ("press", "release"):          # a pulse on every press and release
            if i >= sb.start(beat):
                press_pulse(d, start[0] if beat == "press" else end[0],
                            start[1] if beat == "press" else end[1], (i - sb.start(beat)) / 4)
        pressed = sb.during(i, "press") or sb.during(i, "drag")
        kind = "rect" if i < sb.start("release") else "default"
        cursor(img, pos[0], pos[1], kind, pressed=pressed)   # cursor last
        out.append(Frame(img))
    return out
```

Rules for demo authors:

- A demo edits its own file only. Never edit `common.py` or `generate.py` from a demo task; a helper
  that is missing from `common` is written privately in the demo file with a `# TODO(hoist)` comment,
  to be lifted into `common.py` in a later pass.
- Never draw the tool's own shortcut, as a keycap or otherwise: the card shows it. Mini keycaps are
  for modifiers and gesture keys only (`Shift`, `Alt`, `Enter`, `Esc`, `Space`).
- Every frame is a fresh `new_frame()`, fully repainted. No incremental drawing between frames.
- Return `Frame`s at `W x H`; do not call `finish` or `save_gif` yourself.
- Paint in this order: `obscure` (if any), ink, selection chrome, mini keycaps, press pulses, cursor.
- 40 to 72 frames (the generator rejects anything else; 50 to 62 is the norm); plan beats in whole
  frames, and keep the closing hold at 6 to 8 so the family loops at one rhythm.
- Typing is one character every 2 frames; a cursor that has just dragged a handle eases 8 to 12 px
  off it over 3 frames, so the handle the copy talks about is visible in the hold.
- Waiting to be hoisted (grep `TODO(hoist)`): the obscure demo's live-frame mosaic and shadowless
  rounded paste, and the text demo's caret width (`ink_text` draws a 1 px bar, too thin beside its
  22 px face). Everything from the first pass (the brush pressure model, the badge edit highlight,
  the artwork-aligned mosaic) already lives in `common.py`.

## Framework API (`common.py`)

Geometry and timing: `LW, LH = 252, 144`, `SS = 4`, `OUT_SCALE = 2`, `W, H`, `OUT_W, OUT_H`,
`FRAME_MS = 70`, `P(v)` (logical to supersampled). Same values as track-tips, so the player's
`AuthoredScale = 2` and the 252x144 slot in the card fit unchanged.

Scene constants: `SURROUND` (30,30,30), `ARTWORK` (10,10,242,134), `INK` (255,0,0), `LINE_W` 3,
`BRUSH_W` 5, `ACCENT` (47,124,174), `ROTATE_GREEN`, `PAPERS`, `PAPER_INK`, `CURSOR_PX` 26,
`HANDLE_R` 3.5, `WHITE`, `BLACK`. `ANCHORS` names points on the artwork: `ANCHORS.btn` (213,121),
`ANCHORS.line(i)` (76, 38+i*11), `ANCHORS.line_end(i)`, `ANCHORS.sidebar(i)` (38, 38+i*13),
`ANCHORS.title`, `ANCHORS.content_box` (76,36,230,126).

Maths: `lerp, mix, clamp01, ease_out, ease_in_out, back_out`, `travel(a, b, t, arc=6)` (eased
cursor travel with a lift), `drag(a, b, t)` (ease_out, no lift), `snap45(anchor, p, diag_only=False)`
(port of `SnapPointToCommonAngle`), `rotate(p, center, deg)` (clockwise positive), `dist`, `unit`.

Fonts: `font(size, bold=False)` (Inter), `mono_font(size)` (Cascadia Mono); presets `F_KEY_MINI`
7.5 bold, `F_LABEL` 8 bold, `F_TEXT` 11, `F_NOTE` 9 bold, `F_BADGE` mono 11 (the two small ones
were raised from 6.5 and 7 after a 1x-display check; keep them there). `text_size(s, f)` returns
logical (w, h).

Primitives (logical coords; `d` is the frame's `ImageDraw`): `rrect, rect, line, ellipse, disc,
polygon, text` (`draw_text` is an alias), `dashed_rect(d, box, w, dash, colors, radius)`,
`dashed_line`, `rrect_points`, `norm_box`.

Frames: `class Frame(img)`; `new_frame(zoom=1.0, offset=(0,0), pressed=False) -> (img, d)`;
`artwork(zoom, pressed)` (the cached card image); `paste_card`; `finish`, `save_gif`,
`contact_sheet`, `dump_frames` (used by generate.py).

Layers: `begin_layer(img) -> (layer, d)` and `end_layer(img, layer, shadow=False, alpha=1.0)`
composite an RGBA layer, optionally under the editor's drop shadow (offset (1.4, 1.4), blur sigma
1.7, black at alpha 128). `rotated_layer(layer, center, angle)`.

Storyboard: `OPEN = 3`; `Storyboard().add(name, n)`, `.total`, `.at(i) -> (name, t, k)`,
`.progress(i, name)`, `.during(i, name)`, `.after(i, name)`, `.start(name)`, `.length(name)`;
`blink(i, period=3)`.

Cursor: `cursor(img, x, y, kind="default", s=1.0, pressed=False)` pastes the real cursor with its
hotspot on (x, y) at `CURSOR_PX * s` px (92 % while pressed). Kinds: `default, rect, ellipse, line,
arrow, measure, pen, numerical, text, stickynote, obscure, move, rotate, sizeall, grab, grabbing,
size0..size35`. `brush_cursor(d, x, y, diameter)` draws the brush ring. `press_pulse(d, x, y, t)`
is the accent ring that marks every press and release (t 0..1 over 4 frames).

Mini keycaps: `mini_keycap(d, label, x, y, scale, alpha)` (`MINI_CAP_H` 14 px tall) and
`mini_keycap_pop(d, label, t_in, t_out)` (bottom-left, `back_out` scale-in, fade out) are for
modifiers and keys pressed mid-story: `Shift`, `Alt`, `Enter`, `Esc`, `Space`. A pop at the
bottom-left occupies about (8, 122)-(40, 136): keep ink out of it.

Ink painters (logical units, colour `INK`, width `LINE_W`, each with the drop shadow):
`ink_rect(img, box, w, radius, dash, filled, color, angle)`, `ink_ellipse(img, box, w, dash, color,
angle)`, `ink_line(img, a, b, w, curve, dash)` (`curve` is the editor's CurveOffset; `curve_control`
and `curve_mid` give the control point and the point the mid handle sits on), `ink_arrow(img, a, b, w,
curve, tapered)` (ArrowShape's proportions; `arrow_parts` exposes the geometry), `ink_measure(img, a,
b, w)` (`measure_label(a, b)` formats the readout), `ink_path(img, anchors, closed, w)` with anchors
`(P, In, Out)` (`path_points` flattens them), `ink_brush(img, samples, w, radii, n)` with samples
`(x, y, t_ms)` drawn as round dabs (`brush_radii` is FreehandStroke's simulated pressure: the radius
eases toward w / 2 at `BRUSH_V_FAST` and 1.5 w at rest with time constant `BRUSH_TAU`, so the ink runs
from w to 3 w across; `brush_bounds` boxes the first n samples), `count_badge(img, center, label,
ring_w, arrow_tip, editing, scale)` (`editing` draws the select-all highlight behind the number),
`ink_text(img, xy, s, font, color, caret, fill, angle)`, `sticky_note(img, center, side, paper, text,
angle, look, caret, scale)` (looks: `lift_r`, `lift_l`, `dogear`; the dog-ear is 0.14 of the side and
lifts the text as `BottomInset` does), `obscure(img, box, mode="mosaic", block=6, texture=True)`
(modes `mosaic`, `blur`, `solid`; clipped to the artwork; call it before any ink). The mosaic is
aligned to the artwork's grid and box-averaged like `GraphicImage.UpdateObscureCache`; `texture`
varies each block's distance from the page colour by a stable pseudo-random factor, a deliberate
exaggeration because the mock window's flat text bars average into flat bars that do not read as
pixelated. `wrap_text(s, f, max_w)`.

Selection chrome: `handle_ring(d, x, y)`, `rect_handles(d, box, angle, rotation=True)`,
`rotation_handle(d, anchor, box_center, angle)`, `line_handles(d, a, b, mid)`, `count_handles(d,
center, tip, radius)`, `dashed_border(d, box, angle)` (text/brush marquee), `marquee(d, box)`
(selection/obscure drag), `path_chrome(d, anchors, closed, active)`, `handle_dot`, `rubber_band(d,
last, cursor, color, w, last_out, near_first)`, `edit_highlight(d, box)`, `caret(d, x, y, h, on)`.

## Storyboard contract

Every demo tells the same short story so the family reads as one set (70 ms per frame):

1. Opening, frames 0 to 2 (`OPEN`): the cursor already wears the tool's own glyph (Pan: SizeAll;
   Selection: the Default arrow) and rests on the point the first gesture presses. No key press: the
   card's keycap says which key picked the tool, so the GIF starts with the tool live and loops
   without an empty pause.
2. Action beats: eased travel between places (`travel`), press pulses on every press and release,
   live ink while dragging. New shapes are selected on creation in the editor, so their handles
   show during the drag.
3. Revert: when a one-shot tool releases, the cursor becomes the Default arrow on the same frame
   (`ToolBase.OnMouseUp` sets the tool back to Pointer); the result stays selected with its chrome.
   A second gesture needs the tool again: the cursor swaps back to the tool's glyph mid-travel
   (nothing else marks the re-pick).
4. Closing hold: 6 frames with the final state, then loop. The loop cuts from the finished state back
   to the empty opening, as the track tips do.

## Composition

- Logical canvas 252x144, drawn at 4x supersample (1008x576) and downsampled with Lanczos to the 2x
  output, 504x288 px. The flyout shows it at 252x144 logical, so it is crisp on HiDPI.
- Ground `SURROUND` (30,30,30), the editor's dark canvas surround, so the GIF reads as an inset panel
  in the dark card in both app themes.
- The artwork at `ARTWORK` (10,10)-(242,134): a rounded light app window card with a soft shadow,
  the same mock app the track tips use (title bar dots and search pill, a sidebar with the second item
  highlighted, eight content lines, a blue button bottom-right). A window capture only, no wallpaper:
  red ink and white badges need a light ground. The mock window is identical in every demo so the
  family reads as one editor.
- The whole frame is the demo's: there is no reserved corner (the old shortcut keycap and its
  keep-out zone are gone, the card draws the key instead).
- The pan demo is the only one that draws the artwork at a zoom (1.5) so it overflows the frame and
  the panning shows.

## Style rules

- Ink is the real default red `#FF0000`, 3 px lines (brush 5 px), round caps, every graphic with its
  drop shadow: it is what the user's first click produces, so the miniature is literal.
- Selection chrome in the accent `#2F7CAE` scaled down from the editor's 12 DIP handles (rings r 3.5,
  rotation handle green 18 px right of the right-middle ring, dashed borders, pen squares and dots,
  marquee). Chrome is drawn after ink, cursor last.
- The real Clowd cursor art at 26 logical px, hotspot-aligned, 92 % while pressed. Travel is eased
  with a 6 px lift so the hand reads as lifted between actions; drags are `ease_out` with no lift.
  Every press and release gets a `press_pulse`.
- Text in demos: Inter, 9 to 11 logical px (SemiBold for notes); measure labels 7 bold. No text
  under 5.4 logical px.
- Modifier and key presses are mini keycaps bottom-left (`Shift`, `Alt`, `Enter`, `Esc`, `Space`),
  14 px tall, the same dark cap the card's shortcut keycap is drawn as so the two read as one family.
- The thing the tool adds is the focal point, drawn at full size (shapes span a third to a half of
  the artwork). Motion is eased, never linear snaps except Shift snapping, which is the point.
- Encoding: one shared 255-colour palette across all frames, no dither, `optimize=True`, loop forever.
  Target under 220 KB per GIF (the generator warns past that); pan may reach 300 KB because the whole
  artwork moves. Anything else over the warning means a demo repaints the full canvas for no story
  reason. Pillow merges identical consecutive frames on save and sums their durations, so the saved
  frame count can be lower than the rendered one; the player honours per-frame durations, so holds
  survive. Never reason about frame counts from the saved file.
- No em-dashes anywhere, in copy, comments or scripts. Use commas, colons or two sentences.

## Review workflow

Run the generator filtered to your demo with `--sheet <scratch>/sheet.png --frames <scratch>/frames`,
then look at the sheet and at least: the opening frame (the tool cursor must already be live), two
mid-drag frames, the release frame and the final hold. Check: no shape or cursor clipped by the
frame; every label readable; ink reads on the light window and nothing important sits on the dark
surround; smooth motion (no jumps except the loop cut); press pulses present; the cursor reverts to
the arrow on release for one-shot tools; file size inside budget. Iterate until polished.

## Copy rules (flyout text)

- Header: the tool's display name, as the customise popup shows it (`Pan`, `Selection`, `Rectangle`,
  `Filled Rectangle`, `Ellipse`, `Line`, `Arrow`, `Measure`, `Pen`, `Brush`, `Step Count`, `Text`,
  `Sticky Note`, `Obscure`).
- Description: concise plain sentences, accurate to the code, with only the important information:
  the gesture, the modifiers that shape it, and any mechanic you would not discover by looking at the
  drawn shape (the line's bend handle, the pen's double-click and continue-from-an-end). Leave out the
  bar's settings and anything the demo or the drawn shape makes obvious (resize/rotate handles,
  editing afterwards). The shortcut letter is not in the copy, not even
  as "(R)": the card's keycap shows it.
- No em-dashes.

## Existing demos

The shortcuts (D S R F E L A M P B C T N O) live in `ToolRegistry`, not here.

- `tool-pan.gif`: the artwork at 1.5x zoom; the SizeAll cursor drags it one way, then another
  tool is active, a `Space` mini keycap pops, the view is dragged back, and the tool returns when
  Space is released. The cursor never reverts on release: pan is not a one-shot tool.
- `tool-select.gif`: a pre-existing rectangle and arrow; the arrow cursor becomes the move
  cursor over the rectangle and drags it (8 rings + rotation handle follow), then a `Shift` resize
  from the bottom-right handle keeps the proportions, then a marquee from empty canvas selects both.
- `tool-rectangle.gif`, `tool-filled.gif`, `tool-ellipse.gif`: one drag, the shape selected as it
  grows, the cursor reverting on release; then the tool again and a `Shift` drag that stays a
  square (or a circle) however the pointer wanders.
- `tool-line.gif`, `tool-arrow.gif`: a drag (`Shift` snaps the line to the diagonal mid-drag), then
  the middle handle pulled to bend the ink; the hand eases off the handle afterwards.
- `tool-measure.gif`: a horizontal measure with its live readout, then the end handle dragged
  down so the readout turns to a length and a negative angle.
- `tool-pen.gif`: click, click, press-drag a smooth anchor (mirrored handles), click, then a
  double-click finishes; the pen stays active.
- `tool-brush.gif`: one stroke, thin through a quick wavy underline and swelling as the hand
  slows into a loop around the button; the ring cursor, then the dashed marquee and the arrow.
- `tool-count.gif`: two badges (`Enter` commits the number), then a press-drag that pulls the
  third badge's arrow out to the button.
- `tool-text.gif`: click, type, `Enter`, then the green handle dragged to rotate the text. The
  text is set at 22 px (a raised Size setting), because 11 px could not be read at the card's 1x.
- `tool-note.gif`: a note stuck on at the click, typed into (the text wraps), `Enter`; then the
  next note in the next paper with a dog-ear.
- `tool-obscure.gif`: the page carries a landscape photo and a square avatar (private painters
  in the demo, both on the 10 px mosaic grid so every block is a pure patch of picture); a marquee
  around the photo pixelates it on release, then a `Shift` square over the face. The mosaic is
  sampled from the live frame (`_mosaic_live`), not the cached bare artwork, so the pictures are in
  it; 10 px blocks stand in for a raised Blur setting.

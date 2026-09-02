# Track tip demo GIFs

The looping demos shown in the rich flyouts behind the add-track buttons on the video editor's left
tool strip (Video, Audio, Image, Text, Background, Zoom, Speed, Cursor, Keyboard). Each flyout is a header, a
one or two sentence description, a demo GIF, and, when the button is disabled, the reason why.

This folder holds the generator. The GIFs are never hand-edited: change `generate.py`, re-run it,
commit both the script and the regenerated GIFs.

## Where things live

| What | Path |
| --- | --- |
| Generator (this script) | `tools/track-tips/generate.py` |
| Output GIFs, embedded as Avalonia resources | `clowd_ui/Clowd.Ui/Assets/TrackTips/track-*.gif` |
| The flyout control (header, description, demo, disabled footer) | `clowd_ui/Clowd.Ui/VideoEditor/TrackTip.axaml(.cs)` |
| The GIF player (streams frames through SkiaSharp `SKCodec`) | `clowd_ui/Clowd.Ui/Controls/AnimatedGifImage.cs` |
| Where the tips are attached to the buttons | `clowd_ui/Clowd.Ui/VideoEditor/VideoEditorWindow.axaml` (the `ToolBar` strip) |
| Disabled reasons driven from code | `VideoEditorWindow.axaml.cs`: `RefreshAddSpeedButton`, `RefreshInputOverlayButtons` |
| Timeline colours the demos copy | `clowd_ui/Clowd.Ui/VideoEditor/Timeline/TimelinePalette.cs` |
| Row order the demos copy | `clowd_ui/Clowd.Ui/VideoEditor/Timeline/TimelineRowLayout.cs` |

`TrackTip` resolves its demo as `avares://Clowd.Ui/Assets/TrackTips/track-{DemoName}.gif`. A missing
file just hides the demo area, so the app builds and runs with or without the GIFs.

## Tooling

- Python 3 with Pillow: `pip3 install Pillow` (any Pillow 10+ works; 11 was used).
- Run from the repo root:
  - `python3 tools/track-tips/generate.py` regenerates all nine into the assets folder.
  - `python3 tools/track-tips/generate.py speed cursor` regenerates only the GIFs whose names contain
    those words.
  - `python3 tools/track-tips/generate.py --sheet /tmp/contact.png` also writes a review contact
    sheet (5 evenly spaced frames per GIF, labelled). `--out DIR` overrides the output folder.
- Fonts: Arial Bold / Helvetica on macOS, DejaVu Sans Bold on Linux, Arial on Windows, falling back
  to Pillow's built-in font. Regenerate on macOS if pixel-identical output matters.
- Always open the contact sheet (and a few full-size frames if something looks off) and iterate before
  committing. Typical things to catch: clipped shapes, labels overlapping blips, text too small to read,
  colours that vanish against the light app window, a loop that jumps.

## Adding a demo for a new tool

1. In `generate.py`, write `gif_<name>()` returning a list of finished frames (follow an existing one,
   `gif_image` and `gif_zoom` are the simplest), and register it in the `GIFS` table as
   `"track-<name>.gif": gif_<name>`. Reuse the helpers: `card_row_demo(...)` for a row that holds an
   item at the playhead (video, audio, image, text, zoom), `overlay_row_demo(...)` for a row that
   mirrors the whole recording (cursor, keys), `phases(i)` for the storyboard timing,
   `render_desktop(...)` + `paste_canvas(...)` for the mock recording, `base_rows(...)` /
   `header_cell` / `filmstrip` / `waveform` / `draw_ruler` / `draw_playhead` for the timeline.
2. Run the script (optionally filtered to the new name) and review the contact sheet.
3. In `VideoEditorWindow.axaml`, give the new `ToolButton` the same attached properties as its
   neighbours (`ToolTip.HorizontalOffset="6"`, `ToolTip.Placement="Right"`,
   `ToolTip.ShowOnDisabled="True"`) and a `<ToolTip.Tip>` holding
   `<ToolTip Theme="{StaticResource TrackTipToolTipTheme}"><ve:TrackTip x:Name="tipXxx"
   DemoName="<name>" Header="Add Xxx Track" Description="..." /></ToolTip>`.
4. If the button can be disabled, set `tipXxx.DisabledReason` from the code-behind wherever its
   command's CanExecute is refreshed (null when enabled), as `RefreshAddSpeedButton` does.
5. Build `clowd_ui/Clowd.Ui` and hover the button in the editor to check placement and playback.

## Storyboard contract

Every demo tells the same short story so the family reads as one set (70 ms per frame):

1. Hold, about 6 frames: the project as it is, playhead parked at 3 s.
2. Pop, about 8 frames: the new row is inserted at its real position (it grows in and pushes the rows
   under it down) and the new item pops in at the playhead with a back-out ease, selected (accent
   outline). Rows that mirror the recording (Cursor, Keys) fade in across the whole recording instead.
3. Sweep, about 30 frames: the playhead runs across the item (3 s to 8 s) while the preview plays the
   effect: the overlay appears, the text types, the zoom eases in and out, the cursor travels and
   clicks, the keycaps pop.
4. Hold, about 8 frames, then loop.

Nominal length is 52 frames. Speed is the exception: its playhead runs at 1x before the item, 2x across
it and 1x after, so it has a few more frames and its own path code.

## Composition

- Logical canvas 252x144, drawn at 4x supersample (1008x576) and downsampled with Lanczos to the 2x
  output, 504x288 px. The flyout shows it at 252x144 logical, so it is crisp on HiDPI.
- Top 80 px: the preview, `#1e1e1e`, with the letterboxed mock recording (`CANVAS`, 148x74): a
  desktop wallpaper with one light app window (title bar dots, text lines, a blue button). Zoom
  crops and resizes this rendered frame toward a focus point exactly as the compositor does.
- Bottom 64 px: the timeline. A narrow track header column with grip dots and the row name, a ruler
  with `0:00 / 0:04 / 0:08` timestamps, the Screen row (accent filmstrip of desktop thumbnails) and
  the Audio row (green waveform), block gutters with the faint hatch.
- New rows go where `TimelineRowLayout` puts them: Speed pinned at the top above a hatched gutter;
  Zoom, Text, Image and an imported clip above Screen; Cursor and Keys glued directly above Screen and
  spanning the whole recording (with motion / click blips and key blips); Background under Screen,
  because the video block composites bottom up and a backdrop draws behind the recording; imported
  audio in the audio block under Audio.
- A row name that does not fit the 34px header column is condensed rather than left to spill over the
  items next to it (`fitted_text`); "Background" is the only one so far that needs it.
- Item labels use the editor's own formats: `200%` (zoom), `2x` (speed), `photo.png`, `clip`,
  `music`, `Hello!`, `Cursor`, `Keys`.

## Style rules

- Dark chrome only: preview `#1e1e1e`, surface (30,30,32), rows (45,45,48) / (38,38,41), ruler text
  (228,228,230), header text (140,140,140).
- Row kind colours, exactly as `TimelinePalette.cs` (dark variant): recording / video accent
  (84,169,255), audio (52,140,108), text (118,92,176), image (176,122,52), speed (184,70,92),
  zoom (46,136,150), cursor (158,74,158), keyboard (132,144,56), background (64,142,76). Playhead
  (240,82,82). Selection
  outline in the accent. Waveform ink (226,244,236), cursor motion blips (246,228,246), key blips
  (246,248,226). Speed's stopwatch tints rose while it runs fast.
- The thing the tool adds must be the obvious focal point: added preview elements are large and
  prominent (PiP video card, image card, text card, zoom reticle and badge, the stopwatch). The
  cursor arrow and the keycaps are drawn very large (roughly 2x what "realistic" would be) on purpose.
- Minimal text in the preview (`Hello!`, `2x`, `Ctrl`, `C`, `V`); anything smaller than about 5.4
  logical px is unreadable and should be omitted.
- Smooth, eased motion; no jitter; clean loop (hold at the end, restart from the same first frame).
- Encoding: one shared 255-colour palette across all frames (median cut on a strip of every frame),
  no dither, `optimize=True`, loop forever. Target under about 200 KB per GIF; zoom, speed and
  background run a little over (roughly 206 to 225 KB) because every frame changes across the whole
  canvas, which is acceptable. Anything much past that is a warning that something in the demo is
  repainting the full canvas for no story reason: `optimize=True` can only drop the pixels a frame
  shares with the one before it, so a backdrop that moves under everything else costs more than the
  motion is worth. The background demo hit 296 KB while its mesh gradient drifted, and its still
  mesh reads the same at 504x288 for 72 KB less.

## Copy rules (flyout text)

- Header: `Add <Kind> Track` (Add Video Track, Add Speed Track, and so on).
- Description: one or two plain sentences saying what it adds, where it lands (at the playhead), and
  what you can do with it next.
- Disabled reason: a full sentence that names the cause and the fix ("The playhead is inside an
  existing speed change. Move it to a free stretch of the Speed row to add another.").
- No em-dashes anywhere, in copy, comments or this script. Use commas, colons or two sentences.

## Existing demos

- `track-video.gif`: an imported clip row appears above Screen; a picture-in-picture video card with a
  drifting sun fades into the recording's corner, with gizmo handles.
- `track-audio.gif`: a `music` row appears in the audio block with a waveform; a speaker glyph with
  animated sound arcs sits on the window.
- `track-image.gif`: an Image row with `photo.png`; a landscape card scales in over the window with
  corner handles.
- `track-text.gif`: a Text row with `Hello!`; a dark text card types `Hello!` with a blinking caret.
- `track-zoom.gif`: a Zoom row at `200%`; a focus reticle fades as the frame zooms toward the
  window's button, holds, and eases back out.
- `track-speed.gif`: a Speed row pinned on top with `2x` and chevrons; a stopwatch in the top-right of
  the recording runs 1x, then 2x (rose tint, `2x` badge) while the playhead crosses the item, then 1x.
- `track-cursor.gif`: a Cursor row spanning the recording with motion and click blips; a large cursor
  travels to the button, presses it, and an orchid click ring expands.
- `track-keyboard.gif`: a Keys row spanning the recording with key blips; large `Ctrl + C` then
  `Ctrl + V` keycaps pop up at the bottom of the frame.
- `track-background.gif`: a Background row appears under Screen with a `Big Sur` item that opens out
  from the playhead across the whole project; the recording shrinks toward the middle of the canvas,
  rounded and shadowed, uncovering a still mesh wallpaper in the Big Sur artwork's colours (still
  because Big Sur is one of the library's static styles, and because a moving backdrop repaints the
  whole canvas every frame - see the size note under Style rules).

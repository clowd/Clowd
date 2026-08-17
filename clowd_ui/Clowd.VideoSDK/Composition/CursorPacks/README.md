# Cursor pack artwork

The SVG sources of the four [ful1e5](https://github.com/ful1e5) cursor packs the video editor
offers, embedded into `Clowd.VideoSDK` and read by `CursorPackLoader`. Nothing here is
hand-authored: each file is upstream's own, and re-syncing a pack means re-copying it.

| Folder        | Upstream                                        | Set                                 |
| ------------- | ----------------------------------------------- | ----------------------------------- |
| `bibata-r`    | `ful1e5/Bibata_Cursor` — `svg/modern*`          | Modern (rounded edge), left hand    |
| `bibata-s`    | `ful1e5/Bibata_Cursor` — `svg/original*`        | Original (sharp edge), left hand    |
| `breezex`     | `ful1e5/BreezeX_Cursor` — `svg`                 |                                     |
| `macos`       | `ful1e5/apple_cursor` — `svg`                   |                                     |
| `fuchsia`     | `ful1e5/fuchsia-cursor` — `svg`                 |                                     |

Bibata is the only pack carried twice, because its two edge sets are two drawings rather than two
palettes; the editor presents them as the `R` and `S` halves of one style. Its `-Right` mirrors are
a separate set for a different hand and are not carried.

## What was changed on the way in

1. **Only the sixteen cursors the editor draws** are copied, renamed from the packs' X11 names to
   the editor's `CursorAssets` kind keys — `left_ptr` → `arrow.svg`, `xterm` → `ibeam.svg`,
   `crosshair`/`cross` → `cross.svg`, `center_ptr` → `uparrow.svg`, `bd_double_arrow` (or
   `bottom_right_corner`) → `sizenwse.svg`, `fd_double_arrow` (or `bottom_left_corner`) →
   `sizenesw.svg`, `sb_h_double_arrow` → `sizewe.svg`, `sb_v_double_arrow` → `sizens.svg`, `move` →
   `sizeall.svg`, `crossed_circle` → `no.svg`, `hand2` → `hand.svg`, `question_arrow` → `help.svg`,
   `pencil` → `pen.svg`, `person` → `person.svg`. The two animations keep their frames, numbered in
   the order the pack lists them: `wait/` (from `wait`) and `appstarting/` (from `left_ptr_watch`).
   Bibata's sources are symlinks, so its files come from `svg/groups/*` where the real bytes are.
2. **Raster filter chains and clip rects are stripped**, along with the `filter=` / `clip-path=`
   attributes referencing them, the `xmlns`, and inter-element whitespace. Every filter in these
   packs is a drop shadow, which flat cursor artwork cannot carry — so it is dropped at the copy
   rather than parsed and ignored on every build. Nothing else is touched: gradients and Figma's
   outside-stroke masks stay, because the loader reads both.

Everything else the loader does — the placeholder colours, the outside strokes, the even-odd fills —
happens at table build and is documented on `CursorPackLoader` itself.

## Re-syncing a pack

Clone the pack, copy its sixteen cursors and two frame folders under the names above, then re-run
the strip in step 2. Hotspots and frame delays are *not* stored here: they live in `CursorAssets`'
own pack table, read off each repository's `configs/x.build.toml` (`x_hotspot`/`y_hotspot`, quoted
against a 256-unit render, and `x11_delay`). Check those when a pack is updated.

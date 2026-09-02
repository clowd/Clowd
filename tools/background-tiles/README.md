# Background tile loop sheets

The little wallpaper swatches in the video editor's BACKGROUND inspector section. Three of the
library's ten styles animate (Moving Blob, Moving Corners, Breathing Field), and their tiles play a
pre-rendered loop out of a sprite sheet instead of animating the wallpaper live.

This folder holds the generator. The sheets are never hand-edited: change the artwork or this
program, re-run it, commit both.

## Why they exist

A still wallpaper is recorded once into an `SKPicture` and replayed for nothing. An animated one
cannot be: its geometry is a function of the loop phase, so every frame walks the SVG tree and
rebuilds its animated paths, and Breathing Field additionally rasterizes and Gaussian-blurs a
working surface on the CPU whatever size it is drawn at (`SvgGroup.BlurSigmaWorkingPx`,
`BoxGaussianBlur`). Measured with `--bench` on one 192x64 draw, Release:

| style | live | from the sheet |
| --- | --- | --- |
| moving-blob | 0.014 ms | 0.021 ms |
| moving-corners | 0.022 ms | 0.020 ms |
| breathing-field | 0.295 ms | 0.020 ms |

When the sheets were introduced Breathing Field's live draw cost 9.015 ms (its blur ran on a fixed
480px working surface, half a megapixel for a picture with no detail finer than 86 pixels), and all
three tiles are on screen together whenever the section is, so the picker was paying about 9.05 ms
per tick on Avalonia's render thread, the same thread that composes the video preview. The adaptive
frame budget in `BackgroundStylePreview.TileClock` kept that from pegging the thread by stretching
its interval to about 45 ms, which is a quarter of the render thread spent on three swatches. With
the sheets it is 0.066 ms and the clock sits at its 33 ms floor. The blur has since been resized by
its sigma (12 working pixels per sigma, a 115x93 surface for Breathing Field), which is what made the
main canvas affordable; the sheets stay because three live tiles would still be a millisecond of the
render thread per tick for nothing.

The sheet numbers above are measured on a raster surface, which actually filters the quad; on the
real leased canvas it is one textured quad against a texture the GPU already holds, so those are an
upper bound. Note also that the two cheap styles are not the point: they are on sheets for
consistency of motion and to stop rebuilding paths on the render thread, not because they cost
anything.

**The main canvas is not involved and must never be.** Its wallpaper phase comes from the project
timeline through `FrameComposer`, which is what makes a paused frame freeze, a scrub scrub the
wallpaper, and the export match the preview frame for frame. A pre-rendered loop is picker eye candy
and belongs nowhere near that.

## Where things live

| What | Path |
| --- | --- |
| Generator (this program) | `tools/background-tiles/Program.cs` |
| Frame grid, frame count rule, phase-to-frame map, cover-crop | `clowd_ui/Clowd.VideoSDK/Composition/Backgrounds/BackgroundTileSheet.cs` |
| Output sheets, embedded as Avalonia resources | `clowd_ui/Clowd.Ui/Assets/BackgroundTiles/<style>.webp` |
| The decode-once cache the tiles read them through | `clowd_ui/Clowd.Ui/VideoEditor/Inspector/BackgroundTileLoop.cs` |
| The tile control (clock, gating, draw) | `clowd_ui/Clowd.Ui/VideoEditor/Inspector/BackgroundStylePreview.cs` |
| Where the tiles are placed in the panel | `clowd_ui/Clowd.Ui/VideoEditor/Inspector/InspectorPanel.axaml` (the BACKGROUND section) |
| The wallpapers themselves | `clowd_ui/Clowd.VideoSDK/Composition/Backgrounds/Art/<style>/source.svg` |
| The catalog that declares which styles animate and for how long | `.../Backgrounds/BackgroundCatalog.cs` |
| Tests | `clowd_ui/Clowd.VideoSDK.Tests/BackgroundTileTests.cs` |

`BackgroundTileSheet` is in the SDK rather than beside the control on purpose: the frame grid has to
mean the same thing to the generator, to the tile and to the test, and a disagreement between any
two of them shows up as art sliced at the wrong offsets rather than as a build error.

## Tooling

- .NET only, no Python and no Pillow: the frames have to come from `BackgroundRenderer`, the same
  public entry point `FrameComposer` and the tiles call, so that a sheet cannot drift from what the
  canvas paints. Nothing here reimplements any part of the wallpaper drawing.
- Run from anywhere (it finds the repo by walking up to `Clowd.slnx`):
  - `dotnet run --project tools/background-tiles` regenerates all three into the assets folder.
  - `dotnet run --project tools/background-tiles breathing` regenerates only the styles whose id
    contains that word.
  - `dotnet run --project tools/background-tiles -- --bench` prints the table above and exits.
  - `--contact PNG` also writes a review contact sheet. `--out DIR` overrides the output folder,
    `--format png|jpg|webp` and `--quality N` exist to re-check the encoding choice.
- Always write a contact sheet and look at it before committing. Its three rows per style are the
  three ways a sheet goes wrong: frames across the loop (is this the right artwork, in the right
  order), the wrap seam (does the loop jump), and the same instants at the tile's real size with the
  sheet and a live render alternating (is the cover-crop taking the right band).

## The numbers, and why they are those numbers

- **Frame size 192x64.** The tile is 34 logical pixels tall and about 105 wide with the sidebar at
  its default 250px, so this is a little over the device size a 2x screen asks for. Deliberately not
  larger: the sheets stay decoded for the life of the process (about 10 MB for all three), and these
  three wallpapers are a poster blob, two gradient wedges and a Gaussian wash, none of which carry
  detail a wider frame would preserve.
- **3:1 frames, cover-cropped by the player.** The tile's own aspect runs from 3.1:1 at the
  sidebar's minimum width to about 8:1 at its 600px maximum. A frame proportionally *taller* than
  the narrowest tile means every tile shape crops the frame vertically, and a vertical crop of a
  full-width cover-fit is exactly the cover-fit the live draw would have produced. Widening the
  sidebar only ever samples less of the frame, so at the extreme the frame is upscaled past its own
  resolution, which on artwork with no detail in it reads as the same picture.
- **One frame per second of the artwork's own loop**, so 60, 60 and 90 frames. The tiles play at 12x
  real time (`TilePreviewSpeedup`), which makes that 12 displayed frames per second for every style
  whatever its period, rather than three tiles moving at three different smoothnesses. Raising
  `FramesPerArtworkSecond` is the knob if 12 fps ever reads as steppy; it costs frames, bytes and
  decoded memory in proportion.
- **Sampled evenly across exactly one period**, which is what makes the loop seamless with no blend
  and no hand-picked cut point: frame `i` of `n` is the artwork at `i * period / n`, so frame `n`
  would be the artwork at one full period, which `BackgroundRenderer.PhaseOf` wraps to phase 0,
  which is frame 0.
- **A 10-wide grid**, not one long strip: 90 frames stacked would be a 5760px tall image, and this
  keeps every sheet well inside any GPU's maximum texture dimension.
- **WebP, lossless where it fits.** Skia's WebP encoder is lossless at quality 100, and for artwork
  made of flat fills and hard edges that is *smaller* than encoding the same thing lossily, so
  Moving Blob (24 KB) and Moving Corners (53 KB) ship bit-identical to the renderer's output.
  Breathing Field's every pixel is unique and its lossless sheet is 634 KB, six times the entire
  wallpaper library, so it drops to quality 92 and 35 KB, at a mean error of 1.40 levels out of 255
  on a picture that is nothing but smooth gradients. 112 KB for all three, against 332 KB for the
  Art folder they come from.

## Adding a sheet for a new animated style

1. Give the style a non-zero `PeriodSeconds` in `BackgroundCatalog`. That is the only thing that
   makes it animated, and everything below follows from it.
2. Run the generator. It writes a sheet for every animated style with no per-style configuration.
3. Look at the contact sheet.
4. Run the tests. `BackgroundTileTests` will already be covering the new style: its theories
   enumerate the catalog.

Nothing needs to be added to the tile control, and nothing needs a sheet to work. A style with no
sheet simply animates live, which is slower and otherwise identical.

## Style rules

- No em-dashes anywhere, in copy, comments or this file. Use commas, colons or two sentences.
- The frames come from the renderer, never from a second implementation of the artwork.
- Never hand-edit a sheet. If it is wrong, the artwork or this program is wrong.

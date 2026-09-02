using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Platform;
using Clowd.UI.VideoEditor.Inspector;
using Clowd.VideoSDK.Composition;
using SkiaSharp;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The covering test for the pre-rendered loop sheets the video editor's background STYLE tiles
    /// play instead of animating an animated wallpaper live (see <see cref="BackgroundTileSheet"/>
    /// for the layout, <c>tools/background-tiles</c> for the generator).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A committed generated asset is exactly the kind of thing that rots quietly. The sheets are
    /// pixels frozen out of <see cref="BackgroundRenderer"/> at one moment in the artwork's life:
    /// touch an SVG, a palette, the SMIL reader, the blur, or the cover-fit, and the tiles keep
    /// playing the old picture with nothing anywhere to say so. Nothing at runtime notices either,
    /// because a missing or wrong sheet is by design a graceful degradation rather than an error.
    /// So the checks here are deliberately of the "regenerate and commit" kind: every frame is
    /// compared against what the renderer produces for that frame's own instant TODAY.
    /// </para>
    /// <para>
    /// The suite lives in this project for the reason <see cref="FileIconAssetTests"/> does:
    /// Clowd.Ui has no test project of its own and this one already references it, which is what
    /// makes <see cref="BackgroundTileLoop"/> and the embedded assets reachable at all.
    /// </para>
    /// </remarks>
    public class BackgroundTileTests
    {
        // The asset loader, bound before anything can reach for a sheet. See FileIconAssetTests
        // for why the explicit static constructor below is load-bearing rather than tidy: without
        // it the class is `beforefieldinit` and the runtime is free to defer this until the first
        // static FIELD access, which the test bodies below would never make.

        private static string _assetLoaderFailure;
        private static readonly bool _assetLoaderBound = AvaloniaAssetLoaderBind.TryBind(out _assetLoaderFailure);

        static BackgroundTileTests() { }

        /// <summary>
        /// The per-channel mean error a sheet is allowed to carry against a fresh render, out of
        /// 255. Moving Blob and Moving Corners ship losslessly encoded and their worst sampled
        /// frames measure 0.000 and 0.002; Breathing Field is a 90 frame Gaussian wash that would
        /// cost 634 KB lossless, so it ships at WebP quality 92 and its worst measures 1.628. Four
        /// is above the encoder with room to spare and far below anything that could be called a
        /// different picture: a sheet left behind by a change to the artwork lands in the tens.
        /// </summary>
        private const double MaxEncodingError = 4.0;

        /// <summary>
        /// The per-channel mean error allowed between a sheet frame drawn at tile size and a live
        /// render at that size, out of 255. The two pictures are genuinely resampled differently
        /// and neither is wrong: the sheet is a 192px render filtered down to the tile, the live
        /// draw is antialiased natively at 105px, and where the artwork has a hard edge (Moving
        /// Corners' amber wedge against its ground) the two disagree along it substantially on the
        /// few pixels it covers. Measured worst cases are 0.124 for Moving Blob, 0.487 for Moving
        /// Corners and 1.111 for Breathing Field, so four leaves more than threefold headroom for a
        /// Skia whose filtering differs slightly, while a cover-fit that framed the wrong band of
        /// the artwork lands in the tens.
        /// </summary>
        private const double MaxResamplingError = 4.0;

        /// <summary>Frames sampled per style for the comparison against a live render. Every third,
        /// which is 20 of Moving Blob's 60 and 30 of Breathing Field's 90, enough that a sheet
        /// generated at the wrong instants, in the wrong order or from the wrong artwork cannot
        /// slip through, without re-rendering 210 frames in a suite that runs on every
        /// build.</summary>
        private const int SampleEvery = 3;

        public static IEnumerable<object[]> AnimatedStyles
            => BackgroundCatalog.Styles.Where(s => s.IsAnimated).Select(s => new object[] { s.Id });

        [Fact]
        public void The_avalonia_asset_loader_binds_outside_a_running_app()
        {
            Assert.True(_assetLoaderBound,
                "Could not bind an IAssetLoader by reflection, so avares:// cannot resolve and every " +
                "sheet below would silently read as absent: " + _assetLoaderFailure);
        }

        /// <summary>
        /// Every animated style has a sheet, addressed the way the app addresses it. Without this
        /// a style added to the catalog with its period filled in but the generator never re-run
        /// simply animates live again, which is the slow path this whole mechanism exists to avoid
        /// and which looks perfectly correct on screen.
        /// </summary>
        [Theory]
        [MemberData(nameof(AnimatedStyles))]
        public void Every_animated_style_ships_a_loop_sheet(string styleId)
        {
            var uri = BackgroundTileLoop.UriOf(styleId);
            Assert.True(AssetLoader.Exists(uri),
                $"No loop sheet at {uri}. Regenerate with: dotnet run --project tools/background-tiles");
        }

        /// <summary>
        /// A still style must NOT have one. A sheet for a style that does not move is dead weight
        /// in the exe that nothing will ever draw, and its presence would mean the generator's idea
        /// of which styles animate has drifted from the catalog's.
        /// </summary>
        [Fact]
        public void A_still_style_ships_no_sheet()
        {
            foreach (var style in BackgroundCatalog.Styles.Where(s => !s.IsAnimated))
            {
                Assert.False(AssetLoader.Exists(BackgroundTileLoop.UriOf(style.Id)),
                    $"{style.Id} does not animate but carries a loop sheet; delete it.");
            }
        }

        /// <summary>
        /// The sheet's pixels are laid out the way the player slices them. Frame rectangles are
        /// computed, never stored, so a sheet one row short or generated at a different frame size
        /// would not fail to load: it would draw the art sliced at the wrong offsets, which reads
        /// as a wallpaper that jitters.
        /// </summary>
        [Theory]
        [MemberData(nameof(AnimatedStyles))]
        public void Each_sheet_is_the_size_the_layout_declares(string styleId)
        {
            var style = BackgroundCatalog.Find(styleId);
            using var sheet = LoadSheet(styleId);
            var expected = BackgroundTileSheet.SizeOf(style);

            Assert.Equal(expected.Width, sheet.Width);
            Assert.Equal(expected.Height, sheet.Height);
            // and the last frame is inside it, which is the same statement made from the other end
            var last = BackgroundTileSheet.RectOf(BackgroundTileSheet.FrameCountOf(style) - 1);
            Assert.True(last.Right <= sheet.Width && last.Bottom <= sheet.Height);
        }

        /// <summary>
        /// Every sampled frame is the renderer's own output for that frame's instant. This is the
        /// check that makes the sheets safe to commit: it fails the moment the artwork, the SVG
        /// reader, the blur, the palette resolution or the cover-fit changes under them, and the
        /// fix is always the same one line of regeneration.
        /// </summary>
        [Theory]
        [MemberData(nameof(AnimatedStyles))]
        public void Every_frame_is_what_the_renderer_draws_at_that_instant(string styleId)
        {
            var style = BackgroundCatalog.Find(styleId);
            using var sheet = LoadSheet(styleId);
            int frames = BackgroundTileSheet.FrameCountOf(style);

            var frameRect = SKRect.Create(0, 0, FrameInfo.Width, FrameInfo.Height);
            double worst = 0;
            int worstFrame = 0;

            for (int i = 0; i < frames; i += SampleEvery)
            {
                using var live = Render(FrameInfo, canvas => BackgroundRenderer.Draw(canvas, frameRect,
                    style.Id, null, BackgroundTileSheet.TimeSecondsOf(style, i)));
                double error = MeanError(sheet, BackgroundTileSheet.RectOf(i), live);
                if (error > worst)
                {
                    worst = error;
                    worstFrame = i;
                }
            }

            // Asserted once over the whole loop rather than per frame, so a failure names the worst
            // frame in the sheet instead of merely the first one past the line.
            Assert.True(worst <= MaxEncodingError,
                $"{styleId} frame {worstFrame} of {frames} differs from a fresh render by {worst:0.000} of 255 " +
                $"(limit {MaxEncodingError}). The artwork or the renderer has changed under the sheet; " +
                "regenerate with: dotnet run --project tools/background-tiles");
        }

        /// <summary>
        /// The loop wraps without a jump. The tile plays the sheet on repeat, so the step from the
        /// last frame back to the first is a step the viewer sees every few seconds and is the one
        /// place a pre-rendered loop can look wrong in a way no single frame reveals. It is seamless
        /// by construction (frame i is the artwork at <c>i * period / n</c>, so frame n would be one
        /// full period, which is phase 0, which is frame 0). This holds that construction to its
        /// promise by measuring the wrap step against every other step in the loop.
        /// </summary>
        [Theory]
        [MemberData(nameof(AnimatedStyles))]
        public void The_loop_wraps_without_a_jump(string styleId)
        {
            var style = BackgroundCatalog.Find(styleId);
            using var sheet = LoadSheet(styleId);
            int frames = BackgroundTileSheet.FrameCountOf(style);
            Assert.True(frames >= 4, "a loop this short says nothing about its own seam");

            var steps = new double[frames];
            for (int i = 0; i < frames; i++)
            {
                steps[i] = MeanError(sheet, BackgroundTileSheet.RectOf(i),
                    sheet, BackgroundTileSheet.RectOf((i + 1) % frames));
            }

            double wrap = steps[frames - 1];
            double typical = steps.Take(frames - 1).Average();

            // The animation actually moves between frames. Stated rather than assumed because it
            // is also what proves the comparison below is doing anything at all: a difference
            // measure that always answered zero would satisfy the seam check trivially, and would
            // equally have let a sheet of 60 identical frames through.
            Assert.True(typical > 0.05,
                $"{styleId}'s consecutive sheet frames are all but identical (mean step {typical:0.000} " +
                "of 255), so either the sheet does not animate or the comparison is broken.");
            // Twice the average step: the frames are evenly spaced in time, so every step is about
            // the same size and the wrap is just another one of them. A loop cut at the wrong point
            // does not miss this by a few percent, it misses it by a factor of the whole animation.
            Assert.True(wrap <= typical * 2.0,
                $"{styleId} jumps at the wrap: the step from frame {frames - 1} to frame 0 is " +
                $"{wrap:0.000} of 255 against a typical step of {typical:0.000}.");
        }

        /// <summary>
        /// The end of the chain: a frame drawn the way the tile draws it, at the size the tile is
        /// actually seen at, is the picture the live render would have put there.
        /// </summary>
        /// <remarks>
        /// The frames themselves being right (above) is not the same statement. Between the sheet
        /// and the screen sits a SECOND cover-fit: the frame's 3:1 rectangle placed into whatever
        /// shape the sidebar's width makes the tile. Getting that wrong crops a band of the
        /// wrong part of the artwork, or squashes it, while every frame in the sheet is still
        /// perfectly correct. That is a mistake no other check here can see.
        /// </remarks>
        [Theory]
        [MemberData(nameof(AnimatedStyles))]
        public void A_frame_at_tile_size_is_the_picture_a_live_draw_would_put_there(string styleId)
        {
            var style = BackgroundCatalog.Find(styleId);
            using var sheetBitmap = LoadSheet(styleId);
            using var sheet = SKImage.FromBitmap(sheetBitmap);
            int frames = BackgroundTileSheet.FrameCountOf(style);

            var info = new SKImageInfo(BackgroundTileSheet.NominalTileWidth, BackgroundTileSheet.NominalTileHeight,
                FrameInfo.ColorType, FrameInfo.AlphaType, FrameInfo.ColorSpace);
            var tile = SKRect.Create(0, 0, info.Width, info.Height);

            double worst = 0;
            int worstFrame = 0;

            for (int i = 0; i < frames; i += SampleEvery)
            {
                using var fromSheet = Render(info, canvas => canvas.DrawImage(sheet,
                    BackgroundTileSheet.SourceRectFor(i, tile), tile,
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), null));
                using var live = Render(info, canvas => BackgroundRenderer.Draw(canvas, tile, style.Id, null,
                    BackgroundTileSheet.TimeSecondsOf(style, i)));

                double error = MeanError(fromSheet, tile, live);
                if (error > worst)
                {
                    worst = error;
                    worstFrame = i;
                }
            }

            Assert.True(worst <= MaxResamplingError,
                $"{styleId} frame {worstFrame} at tile size differs from a live draw by {worst:0.000} of 255 " +
                $"(limit {MaxResamplingError}), which is more than resampling accounts for: the sheet is " +
                "being cropped or fitted differently from the way the renderer covers the same rectangle.");
        }

        /// <summary>
        /// The phase-to-frame map covers the sheet and only the sheet. It is the one piece of
        /// arithmetic between the clock and the pixels, and both of its ends are edges: phase 0 must
        /// be frame 0 (or the loop would not wrap onto itself) and a phase that has crept to 1
        /// through a rounding must not index past the last row of the grid.
        /// </summary>
        [Theory]
        [MemberData(nameof(AnimatedStyles))]
        public void The_phase_map_covers_every_frame_exactly_once(string styleId)
        {
            var style = BackgroundCatalog.Find(styleId);
            int frames = BackgroundTileSheet.FrameCountOf(style);

            Assert.Equal(0, BackgroundTileSheet.FrameIndexAt(style, 0.0));
            Assert.Equal(frames - 1, BackgroundTileSheet.FrameIndexAt(style, 1.0));
            Assert.Equal(frames - 1, BackgroundTileSheet.FrameIndexAt(style, 0.99999));

            // Sampling the middle of each frame's phase band must land on that frame, which is the
            // statement that the map is the exact inverse of the generator's own spacing.
            for (int i = 0; i < frames; i++)
                Assert.Equal(i, BackgroundTileSheet.FrameIndexAt(style, (i + 0.5) / frames));
        }

        // ------------------------------------------------------------------------------ helpers

        /// <summary>The pixel format every comparison happens in, so a decoder that hands back
        /// RGBA or unpremultiplied pixels cannot be mistaken for a difference in the art.</summary>
        private static SKImageInfo FrameInfo => new SKImageInfo(
            BackgroundTileSheet.FrameWidth, BackgroundTileSheet.FrameHeight,
            SKColorType.Bgra8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb());

        /// <summary>The style's sheet, decoded through the app's own asset URI and normalized into
        /// <see cref="FrameInfo"/>'s format.</summary>
        private static SKBitmap LoadSheet(string styleId)
        {
            var uri = BackgroundTileLoop.UriOf(styleId);
            Assert.True(AssetLoader.Exists(uri), "no sheet at " + uri);

            using var stream = AssetLoader.Open(uri);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            buffer.Position = 0;
            using var decoded = SKBitmap.Decode(buffer);
            Assert.NotNull(decoded);

            var normalized = new SKBitmap(new SKImageInfo(decoded.Width, decoded.Height,
                FrameInfo.ColorType, FrameInfo.AlphaType, FrameInfo.ColorSpace));
            using (var surface = SKSurface.Create(normalized.Info, normalized.GetPixels(), normalized.RowBytes))
            {
                surface.Canvas.Clear(SKColors.Black);
                surface.Canvas.DrawBitmap(decoded, 0, 0);
                surface.Canvas.Flush();
            }
            return normalized;
        }

        /// <summary>A bitmap of <paramref name="info"/> with <paramref name="draw"/> painted onto
        /// an opaque black ground, for comparing two ways of producing the same picture.</summary>
        private static SKBitmap Render(SKImageInfo info, Action<SKCanvas> draw)
        {
            var bitmap = new SKBitmap(info);
            using var surface = SKSurface.Create(info, bitmap.GetPixels(), bitmap.RowBytes);
            surface.Canvas.Clear(SKColors.Black);
            draw(surface.Canvas);
            surface.Canvas.Flush();
            return bitmap;
        }

        private static double MeanError(SKBitmap sheet, SKRect frame, SKBitmap other)
            => MeanError(sheet, frame, other, SKRect.Create(0, 0, other.Width, other.Height));

        /// <summary>The mean absolute per-channel difference (of 255) between two same-sized
        /// rectangles, over R, G and B; alpha is skipped because every wallpaper is opaque and the
        /// sheets carry no alpha channel at all.</summary>
        private static double MeanError(SKBitmap a, SKRect ra, SKBitmap b, SKRect rb)
        {
            int w = (int)ra.Width, h = (int)ra.Height;
            Assert.Equal(w, (int)rb.Width);
            Assert.Equal(h, (int)rb.Height);

            var pa = a.GetPixelSpan();
            var pb = b.GetPixelSpan();
            long total = 0;
            for (int y = 0; y < h; y++)
            {
                int oa = ((int)ra.Top + y) * a.RowBytes + (int)ra.Left * 4;
                int ob = ((int)rb.Top + y) * b.RowBytes + (int)rb.Left * 4;
                for (int x = 0; x < w * 4; x += 4)
                {
                    total += Math.Abs(pa[oa + x] - pb[ob + x]);
                    total += Math.Abs(pa[oa + x + 1] - pb[ob + x + 1]);
                    total += Math.Abs(pa[oa + x + 2] - pb[ob + x + 2]);
                }
            }
            return (double)total / (w * h * 3);
        }
    }
}

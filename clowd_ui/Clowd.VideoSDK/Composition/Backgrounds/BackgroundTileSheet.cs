using System;
using SkiaSharp;

namespace Clowd.VideoSDK.Composition
{
    /// <summary>
    /// The layout contract for the pre-rendered loop sheets the video editor's background STYLE
    /// tiles play instead of animating an animated wallpaper live. One grid of frames per animated
    /// style, sampled evenly across exactly one of the artwork's own periods.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the tiles do not just draw the wallpaper.</b> Every tile draw goes through
    /// <see cref="BackgroundRenderer.Draw(SKCanvas, SKRect, string, string, double, double)"/> on
    /// Avalonia's leased canvas, on the render thread, and an animated style cannot use
    /// <c>SvgBackgroundScene</c>'s recorded <see cref="SKPicture"/>: its geometry is a function
    /// of the phase, so each frame walks the element tree and rebuilds its animated paths. For
    /// Moving Blob and Moving Corners that measures 0.015 ms at tile size and is free. Breathing
    /// Field carries an <c>feGaussianBlur</c>, which <c>SvgGroup.DrawBlurred</c> renders on a
    /// fixed 480 px CPU raster and blurs with <c>BoxGaussianBlur</c> at a working sigma of 86
    /// whatever rectangle it lands in, and that measures 9.1 ms in a 34 px swatch, on the thread
    /// that also composes the video preview. A sheet turns that into one textured quad.
    /// </para>
    /// <para>
    /// <b>Why this lives in the SDK</b> rather than beside the control that plays it: the frame
    /// grid has to mean the same thing in three places that would otherwise each carry a copy of
    /// it (the generator under <c>tools/background-tiles</c> that writes the sheets, the inspector
    /// tile that reads them, and the test that holds the committed sheets to the renderer's current
    /// output), and a disagreement between any two of them shows up as art sliced at the wrong
    /// offsets rather than as a build error. The sheet PIXELS ship as an Avalonia resource in
    /// Clowd.Ui, since they are a UI asset; only the geometry is here.
    /// </para>
    /// <para>
    /// <b>Nothing in the composer or the render path reads any of this.</b> The main canvas and
    /// the export still draw the wallpaper live at the project's own tick, which is what keeps a
    /// scrub, a paused frame and the exported file identical. A pre-rendered loop is eye candy for
    /// a picker and would break that property everywhere else.
    /// </para>
    /// </remarks>
    public static class BackgroundTileSheet
    {
        /// <summary>
        /// One frame's width in the sheet. The tile is 34 logical pixels tall and, at the
        /// inspector sidebar's default 250 px width, about 105 wide, so 192x64 is a little over
        /// the device size a 2x screen asks for and comfortably over a 1.5x one. It is deliberately
        /// not larger: the sheets are held decoded for the life of the process, and these three
        /// wallpapers are a poster blob, a pair of gradient wedges and a Gaussian wash, none of
        /// which carry detail that a wider frame would preserve.
        /// </summary>
        public const int FrameWidth = 192;

        /// <summary>
        /// One frame's height, giving a 3:1 frame. The tile's own aspect runs from 3.1:1 at the
        /// sidebar's minimum width to about 8:1 when it is dragged to its 600 px maximum, and the
        /// player COVER-crops the frame into the tile, so a frame slightly TALLER in proportion
        /// than the narrowest tile is the safe choice: every tile width then crops the sheet
        /// vertically, which is exactly what <see cref="BackgroundRenderer.CoverMatrix"/> does to
        /// the artwork itself. A frame wider in proportion than the narrowest tile would instead
        /// crop horizontally, which the live render never does.
        /// </summary>
        public const int FrameHeight = 64;

        /// <summary>
        /// The tile's logical size with the inspector sidebar at its default 250px width: two
        /// columns of a 234px list, less the item margins, over the 34px the template fixes. The
        /// tile draws at whatever size layout hands it and never reads these; they are here so
        /// that <see cref="FrameWidth"/> and <see cref="FrameHeight"/> can be justified against a
        /// real number, and so the generator's review sheet and the tests can show a frame at the
        /// size it is actually looked at rather than at some flattering magnification.
        /// </summary>
        public const int NominalTileWidth = 105;

        /// <summary>See <see cref="NominalTileWidth"/>. Fixed by the item template, at every
        /// sidebar width.</summary>
        public const int NominalTileHeight = 34;

        /// <summary>Frames per row. A grid rather than one long strip keeps the sheet well inside
        /// every GPU's maximum texture dimension (90 frames stacked would be 5760 px tall) and
        /// keeps the encoded image at sane proportions.</summary>
        public const int Columns = 10;

        /// <summary>
        /// Frames per second OF THE ARTWORK'S OWN LOOP, which is what makes the three sheets play
        /// at one consistent rate rather than three: the tiles run their clock at 12x real time,
        /// so one frame per artwork second is 12 displayed frames per second whether the style
        /// loops over 60 seconds or 90.
        /// </summary>
        public const double FramesPerArtworkSecond = 1.0;

        /// <summary>The folder the sheets are embedded from, under Clowd.Ui's Assets.</summary>
        public const string AssetFolder = "BackgroundTiles";

        /// <summary>The sheet file name for a style id, WebP because these are smooth fields that
        /// a lossless PNG cannot compress and Skia's WebP encoder has both a lossless mode (which
        /// two of the three sheets fit inside) and a lossy one that costs under two levels of 255
        /// on the third. Decoded through <see cref="SKCodec"/>, which the process already carries
        /// for the video SDK.</summary>
        public static string FileNameOf(string styleId) => styleId + ".webp";

        /// <summary>
        /// How many frames a style's sheet holds: its period at
        /// <see cref="FramesPerArtworkSecond"/>, never fewer than one full row so the grid's first
        /// line is never ragged. 60 frames for the two 60 second styles, 90 for Breathing Field.
        /// </summary>
        public static int FrameCountOf(BackgroundStyle style)
        {
            if (style == null || !style.IsAnimated)
                return 0;
            return Math.Max(Columns, (int)Math.Round(style.PeriodSeconds * FramesPerArtworkSecond));
        }

        /// <summary>The sheet's pixel size for a style: a full <see cref="Columns"/> wide and as
        /// many rows as the frames need.</summary>
        public static SKSizeI SizeOf(BackgroundStyle style)
        {
            int frames = FrameCountOf(style);
            if (frames <= 0)
                return SKSizeI.Empty;
            return new SKSizeI(FrameWidth * Columns, FrameHeight * ((frames + Columns - 1) / Columns));
        }

        /// <summary>Where frame <paramref name="index"/> sits in the sheet, in sheet pixels: left
        /// to right, then top to bottom.</summary>
        public static SKRect RectOf(int index)
            => SKRect.Create((index % Columns) * FrameWidth, (index / Columns) * FrameHeight, FrameWidth, FrameHeight);

        /// <summary>
        /// The part of frame <paramref name="index"/> to sample when drawing it into
        /// <paramref name="dest"/>: the frame cover-fitted into that rectangle, centered, scaled by
        /// the larger of the two ratios, with the overflow cropped.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Deliberately the same placement rule as <see cref="BackgroundRenderer.CoverMatrix"/>,
        /// applied a second time to a picture that is already a cover-fit of the artwork. That is
        /// exact rather than approximate because a frame is proportionally TALLER than the
        /// narrowest a tile can be (see <see cref="FrameHeight"/>): every tile shape therefore
        /// crops the frame VERTICALLY, and a vertical crop of a full-width cover-fit is the
        /// cover-fit the live draw would have produced for that shape. Widening the tile only ever
        /// samples less of the frame, never more, so at the extreme the frame is upscaled past its
        /// own resolution, which on artwork with no detail in it reads as the same picture.
        /// </para>
        /// <para>
        /// The frame is inset half a texel first. SkiaSharp 3.119 exposes no <c>drawImageRect</c>
        /// overload carrying Skia's strict source-rect constraint, so a bilinear filter is free to
        /// reach half a texel past the source rectangle, which in a GRID of frames is the frame
        /// beside this one. The neighbours are one twelfth of a displayed second away so the bleed
        /// would be invisible anyway; the inset makes it impossible for a quarter of a percent of
        /// the picture.
        /// </para>
        /// </remarks>
        public static SKRect SourceRectFor(int index, SKRect dest)
        {
            var frame = SKRect.Inflate(RectOf(index), -0.5f, -0.5f);
            if (dest.Width <= 0 || dest.Height <= 0)
                return frame;

            float scale = Math.Max(dest.Width / frame.Width, dest.Height / frame.Height);
            float w = dest.Width / scale;
            float h = dest.Height / scale;
            return SKRect.Create(frame.MidX - w / 2f, frame.MidY - h / 2f, w, h);
        }

        /// <summary>
        /// The project-timeline instant frame <paramref name="index"/> was drawn at, which is what
        /// makes the loop seamless with no blend and no hand-picked cut: frame <c>i</c> of <c>n</c>
        /// is the artwork at <c>i * period / n</c>, so frame <c>n</c> would be the artwork at
        /// exactly one period, which <see cref="BackgroundRenderer.PhaseOf(BackgroundStyle, long, double)"/>
        /// wraps to phase 0, which is frame 0.
        /// </summary>
        public static double TimeSecondsOf(BackgroundStyle style, int index)
        {
            int frames = FrameCountOf(style);
            return frames <= 0 ? 0 : index * style.PeriodSeconds / frames;
        }

        /// <summary>The frame to show at a loop <paramref name="phase"/> in [0, 1), as
        /// <see cref="BackgroundRenderer.PhaseOf(BackgroundStyle, long, double)"/> returns it. A
        /// phase that has crept to exactly 1 through a rounding is clamped rather than wrapped, so
        /// the caller never has to reason about it.</summary>
        public static int FrameIndexAt(BackgroundStyle style, double phase)
        {
            int frames = FrameCountOf(style);
            if (frames <= 0)
                return 0;
            if (double.IsNaN(phase))
                return 0;
            return Math.Clamp((int)(phase * frames), 0, frames - 1);
        }
    }
}

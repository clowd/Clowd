using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace Clowd.VideoSDK.Composition
{
    /// <summary>
    /// A wallpaper SVG as an immutable tree that draws itself onto any canvas at any loop phase:
    /// the intermediate representation between <see cref="BackgroundSvgReader"/> and the
    /// composer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The tree is what the two things a recorded picture cannot do both need. Palette
    /// recoloring is per-element color substitution (<see cref="Recolor"/> derives a themed copy
    /// with every literal fill and gradient stop put through a <see cref="CursorPackPalette"/>,
    /// alpha preserved), and SMIL evaluation is per-frame geometry (<see cref="Draw"/> samples
    /// each node's tracks at the phase it is given). Group transforms stay live on the canvas
    /// rather than baked into paths as <c>CursorPackLoader</c> does, because a
    /// <c>userSpaceOnUse</c> gradient is in the shape's own user space and an animated translate
    /// replaces the group's static transform each frame.
    /// </para>
    /// <para>
    /// <b>Purity.</b> <see cref="Draw"/> is a function of (canvas, phase) and nothing else: no
    /// node holds a clock, a frame counter or a last-drawn state, so two callers asking for the
    /// same phase — the editor preview on Avalonia's render thread and an export on its composer
    /// thread — get the same picture by construction.
    /// </para>
    /// <para>
    /// <b>Thread safety.</b> A scene is shared process-wide from a cache and drawn concurrently.
    /// Draw allocates its own <see cref="SKPaint"/>s, layer surfaces and per-frame paths, and
    /// only reads the shared state, all of which is immutable once built: <see cref="SKPath"/>
    /// geometry and clips, <see cref="SKShader"/>s for static gradients, and the float tables the
    /// tracks sample. Nothing here is bound to a <c>GRContext</c>, so an Avalonia-leased canvas
    /// and a D3D12/Metal export surface may draw one scene at once.
    /// </para>
    /// </remarks>
    internal sealed class SvgScene
    {
        internal SvgScene(SKRect viewBox, SvgGroup root, long periodTicks, bool hasBlendModes,
            IReadOnlyList<string> skipped)
        {
            ViewBox = viewBox;
            Root = root;
            PeriodTicks = periodTicks;
            HasBlendModes = hasBlendModes;
            Skipped = skipped;
        }

        /// <summary>The file's <c>viewBox</c>; <see cref="Draw"/> paints in this space.</summary>
        internal SKRect ViewBox { get; }

        internal SvgGroup Root { get; }

        /// <summary>The <c>dur</c> shared by every animation the reader accepted, in 100ns
        /// ticks; 0 for a still.</summary>
        internal long PeriodTicks { get; }

        internal bool IsAnimated => PeriodTicks > 0;

        /// <summary>True when some shape blends other than source-over, in which case the scene
        /// must be composited through its own layer so the blend sees the wallpaper and not
        /// whatever is under it (Monterey Dark's two closing rects).</summary>
        internal bool HasBlendModes { get; }

        /// <summary>What the reader left out and why, one line each — empty for a file the
        /// reader understood completely. The tests hold every shipped asset to the handful of
        /// deliberate omissions.</summary>
        internal IReadOnlyList<string> Skipped { get; }

        /// <summary>Draws the scene in viewBox space at <paramref name="phase"/> in [0, 1).</summary>
        internal void Draw(SKCanvas canvas, double phase) => Root.Draw(canvas, phase);

        /// <summary>This scene with every color put through <paramref name="palette"/>; the
        /// scene itself for a null palette, since the parsed scene is the <c>source</c> theme.</summary>
        internal SvgScene Recolor(CursorPackPalette palette)
            => palette == null
                ? this
                : new SvgScene(ViewBox, (SvgGroup)Root.Recolor(palette), PeriodTicks, HasBlendModes, Skipped);
    }

    internal abstract class SvgNode
    {
        internal abstract void Draw(SKCanvas canvas, double phase);

        internal abstract SvgNode Recolor(CursorPackPalette palette);
    }

    /// <summary>
    /// A <c>&lt;g&gt;</c> (or the root, or a <c>&lt;use&gt;</c> of a group, or the wrapper the
    /// reader puts around a shape with its own transform, clip or filter): a transform, an
    /// optional clip, an optional opacity layer, an optional Gaussian blur, and children.
    /// </summary>
    internal sealed class SvgGroup : SvgNode
    {
        /// <summary>
        /// The most pixels a blurred group's raster may span along the viewBox's longer side.
        /// A blur is rasterized at a working resolution fixed by the artwork alone, never by
        /// the target — the same on every target, which is what makes the blur identical
        /// between a 400 px preview and a 4K export (where Breathing Field's sigma of 161
        /// viewBox units would be 687 device pixels, past the sigma Skia's own image-filter
        /// blur clamps at) and what bounds its cost. This is the ceiling; a wide blur works
        /// well below it, see <see cref="BlurSigmaWorkingPx"/>.
        /// </summary>
        internal const float BlurWorkingPx = 480f;

        /// <summary>
        /// Working pixels per sigma: a blurred group's raster is scaled so its sigma spans
        /// this many pixels, unless <see cref="BlurWorkingPx"/> caps it lower first (which is
        /// the case for every narrow blur, Monterey's included).
        /// </summary>
        /// <remarks>
        /// <para>
        /// The output of a Gaussian blur carries no detail finer than about its sigma, so the
        /// sigma, not the viewBox, is the right unit to size the raster by: pixels beyond a
        /// few per sigma add cost and nothing else, and the cost is linear in them. At the
        /// 480 px ceiling alone Breathing Field's blur was an 823x663 raster, 546 thousand
        /// pixels, six box passes over it, and a float-to-sRGB conversion of all of it, for a
        /// picture whose finest feature was 86 pixels wide: 8.6 ms per frame in Release
        /// (33 ms in Debug), charged on every composed frame, which made a preview with a
        /// Breathing Field on the timeline miss realtime. At 12 pixels per sigma the same
        /// group is a 115x93 raster and the whole draw is 0.30 ms; nothing else about the
        /// pipeline changed, so GPU and CPU composers still get bit-identical blurred pixels.
        /// </para>
        /// <para>
        /// <b>Why 12.</b> Measured against the 480 px render at 1920x1080 over six phases,
        /// the deviation is 0.6 of 255 mean and 5 at worst, with 1.5 percent of bytes off by
        /// more than 2, none of it visible: the second difference of the output along a row,
        /// which is where a bilinear upscale would show its texel grid as banding, stays at
        /// the 8-bit quantization floor. Three box passes approximate the Gaussian with
        /// integer widths, so their effective sigma drifts as the widths round: 1.4 percent
        /// low at 12, 4 percent at 8, and past that the raster is too coarse to place a circle
        /// edge (mean 1.2, worst 11 at 4 pixels per sigma). Twelve is also the smallest value
        /// that leaves Monterey exactly as recorded: its sigmas of 30, 39.5 and 44 in a 2000
        /// unit box already work at 7.2 to 10.6 pixels under the ceiling, so this cap never
        /// binds there. Going lower would buy a tenth of a millisecond and change a still.
        /// </para>
        /// <para>
        /// The reader applies both limits once, at parse time (<c>BlurScaleFor</c>), so the
        /// working scale is part of the immutable scene and a function of the file only.
        /// </para>
        /// </remarks>
        internal const float BlurSigmaWorkingPx = 12f;

        internal SvgGroup(SKMatrix transform, SmilTrack translateTrack, float opacity, SKPath clip,
            float blurStdDeviation, SKRect blurRegion, float blurScale, bool blurLinearRgb, SvgNode[] children)
        {
            Transform = transform;
            TranslateTrack = translateTrack;
            Opacity = opacity;
            Clip = clip;
            BlurStdDeviation = blurStdDeviation;
            BlurRegion = blurRegion;
            BlurScale = blurScale;
            BlurLinearRgb = blurLinearRgb;
            Children = children;
        }

        /// <summary>The static <c>transform</c>; identity when none.</summary>
        internal SKMatrix Transform { get; }

        /// <summary>An <c>&lt;animateTransform type="translate"&gt;</c>, which REPLACES
        /// <see cref="Transform"/> while it runs (SMIL's default <c>additive="replace"</c>);
        /// null when the group is not animated.</summary>
        internal SmilTrack TranslateTrack { get; }

        /// <summary>The group's own <c>opacity</c>: a layer, so its children composite as one.</summary>
        internal float Opacity { get; }

        /// <summary>The <c>clip-path</c> in this group's user space, already carrying the
        /// clipPath element's own transform; null when none.</summary>
        internal SKPath Clip { get; }

        /// <summary><c>feGaussianBlur stdDeviation</c> in user units; 0 for no filter.</summary>
        internal float BlurStdDeviation { get; }

        /// <summary>The user-space region rasterized for the blur (see the reader for how it is
        /// chosen); meaningless when <see cref="BlurStdDeviation"/> is 0.</summary>
        internal SKRect BlurRegion { get; }

        /// <summary>Working pixels per user unit for the blur surface: <see cref="BlurWorkingPx"/>
        /// over the viewBox's longer side.</summary>
        internal float BlurScale { get; }

        /// <summary>
        /// True when the filter mixes in linear light (<c>color-interpolation-filters</c> of
        /// <c>linearRGB</c>, which is SVG's default and what a browser does to a filter that
        /// says nothing); false for one that declares <c>sRGB</c>. The difference is not
        /// subtle where two colors meet under a wide blur: Breathing Field's green-over-purple
        /// wash is a teal-blue in linear light and a darker, more purple mix in sRGB, a gap of
        /// tens of levels over half the frame.
        /// </summary>
        internal bool BlurLinearRgb { get; }

        internal SvgNode[] Children { get; }

        internal override void Draw(SKCanvas canvas, double phase)
        {
            int save = canvas.Save();

            if (TranslateTrack != null)
            {
                Span<float> t = stackalloc float[2];
                TranslateTrack.Sample(phase, t);
                canvas.Translate(t[0], t[1]);
            }
            else if (!Transform.IsIdentity)
            {
                canvas.Concat(Transform);
            }

            if (Clip != null)
                canvas.ClipPath(Clip, SKClipOperation.Intersect, antialias: true);

            if (Opacity < 1f)
            {
                using var layer = new SKPaint { Color = SKColors.White.WithAlpha(AlphaByte(Opacity)) };
                canvas.SaveLayer(layer);
            }

            if (BlurStdDeviation > 0)
                DrawBlurred(canvas, phase);
            else
                DrawChildren(canvas, phase);

            canvas.RestoreToCount(save);
        }

        private void DrawChildren(SKCanvas canvas, double phase)
        {
            foreach (var child in Children)
                child.Draw(canvas, phase);
        }

        /// <summary>
        /// The filtered group rendered and blurred on a raster whose resolution the artwork
        /// fixes (<see cref="BlurSigmaWorkingPx"/> per sigma, at most <see cref="BlurWorkingPx"/>
        /// across), then drawn into the target as an image. The raster is plain CPU memory
        /// whichever backend the target is on, so the blurred pixels are bit-identical
        /// between GPU and CPU composers; only the final upscale is the target's, the same
        /// 1 LSB class as an image item. The two float buffers (170 KB each for Breathing
        /// Field, a few MB for Monterey's) come from the shared array pool, so a 60 fps
        /// preview does not churn the heap.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Color space.</b> The children are drawn by Skia onto an F32 surface tagged with
        /// the filter's working space, so for a <c>linearRGB</c> filter every paint color and
        /// gradient stop is converted to linear light on the way in and the blur mixes there,
        /// as a browser does; for an <c>sRGB</c> filter the tag is sRGB and the values land
        /// as encoded. The blurred floats are then converted by Skia to an 8-bit sRGB image
        /// (unpremultiply, encode, premultiply, in one <see cref="SKPixmap.ReadPixels(SKImageInfo, IntPtr, int)"/>)
        /// and drawn into the target as any other image. See <see cref="BoxGaussianBlur"/> for
        /// why the blur is not Skia's image filter.
        /// </para>
        /// <para>
        /// <b>The sigma is scaled to working pixels exactly once</b>, here. The first cut used
        /// Skia's image filter under the working canvas's <c>Scale(k, k)</c> and handed it
        /// <c>sigma * k</c> as well; Skia maps a filter sigma through the CTM, so the effective
        /// blur was <c>sigma * k</c> user units, Breathing Field's 161 came out as 86, and the
        /// shortfall depended on the viewBox (Monterey's would have been a quarter). A test now
        /// measures the effective sigma of a blurred edge.
        /// </para>
        /// </remarks>
        private void DrawBlurred(SKCanvas canvas, double phase)
        {
            float k = BlurScale;
            var region = BlurRegion;
            int w = (int)Math.Ceiling(region.Width * k);
            int h = (int)Math.Ceiling(region.Height * k);
            if (w <= 0 || h <= 0)
                return;

            using var space = BlurLinearRgb ? SKColorSpace.CreateSrgbLinear() : SKColorSpace.CreateSrgb();
            var info = new SKImageInfo(w, h, SKColorType.RgbaF32, SKAlphaType.Premul, space);
            int floats = w * h * 4;
            float[] pixels = ArrayPool<float>.Shared.Rent(floats);
            float[] scratch = ArrayPool<float>.Shared.Rent(floats);
            var pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                var address = pinned.AddrOfPinnedObject();
                using (var surface = SKSurface.Create(info, address, info.RowBytes))
                {
                    if (surface == null)
                        return;
                    var working = surface.Canvas;
                    working.Clear(SKColors.Transparent);   // pooled memory holds whatever it held
                    working.Translate(-region.Left * k, -region.Top * k);
                    working.Scale(k, k);
                    DrawChildren(working, phase);
                    working.Flush();
                }

                BoxGaussianBlur.Blur(pixels, scratch, w, h, BlurStdDeviation * k);

                using var srgb = SKColorSpace.CreateSrgb();
                using var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul, srgb));
                using (var pixmap = new SKPixmap(info, address, info.RowBytes))
                {
                    if (!pixmap.ReadPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes))
                        return;
                }
                // Immutable, so the image shares the bitmap's pixels rather than copying them.
                bitmap.SetImmutable();
                using var snapshot = SKImage.FromBitmap(bitmap);
                using var paint = new SKPaint { IsAntialias = true };
                canvas.DrawImage(snapshot, region, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), paint);
            }
            finally
            {
                pinned.Free();
                ArrayPool<float>.Shared.Return(pixels);
                ArrayPool<float>.Shared.Return(scratch);
            }
        }

        internal override SvgNode Recolor(CursorPackPalette palette)
        {
            var children = new SvgNode[Children.Length];
            for (int i = 0; i < children.Length; i++)
                children[i] = Children[i].Recolor(palette);
            return new SvgGroup(Transform, TranslateTrack, Opacity, Clip, BlurStdDeviation, BlurRegion, BlurScale, BlurLinearRgb, children);
        }

        internal static byte AlphaByte(double opacity)
            => (byte)Math.Clamp((int)Math.Round(opacity * 255.0), 0, 255);
    }

    /// <summary>
    /// A filled (and optionally stroked) path: <c>&lt;path&gt;</c>, <c>&lt;rect&gt;</c> and
    /// <c>&lt;ellipse&gt;</c>. Static geometry is one shared <see cref="SKPath"/>; an animated
    /// <c>d</c> is a skeleton plus a track and is rebuilt per draw.
    /// </summary>
    internal sealed class SvgShape : SvgNode
    {
        private readonly SKPath _static;
        private readonly SvgPathSkeleton _skeleton;
        private readonly SmilTrack _dTrack;
        private readonly SKShader _fillShader;
        private readonly SKShader _strokeShader;

        /// <summary>A shape with fixed geometry.</summary>
        internal SvgShape(SKPath path, SvgPaint fill, float fillAlpha, SvgPaint stroke, float strokeAlpha,
            float strokeWidth, SKBlendMode blendMode)
            : this(path, null, null, fill, fillAlpha, stroke, strokeAlpha, strokeWidth, blendMode)
        {
        }

        /// <summary>A shape whose <c>d</c> animates over <paramref name="skeleton"/>.</summary>
        internal SvgShape(SvgPathSkeleton skeleton, SmilTrack dTrack, SvgPaint fill, float fillAlpha,
            SvgPaint stroke, float strokeAlpha, float strokeWidth, SKBlendMode blendMode)
            : this(null, skeleton, dTrack, fill, fillAlpha, stroke, strokeAlpha, strokeWidth, blendMode)
        {
        }

        private SvgShape(SKPath path, SvgPathSkeleton skeleton, SmilTrack dTrack, SvgPaint fill, float fillAlpha,
            SvgPaint stroke, float strokeAlpha, float strokeWidth, SKBlendMode blendMode)
        {
            _static = path;
            _skeleton = skeleton;
            _dTrack = dTrack;
            Fill = fill;
            FillAlpha = fillAlpha;
            Stroke = stroke;
            StrokeAlpha = strokeAlpha;
            StrokeWidth = strokeWidth;
            BlendMode = blendMode;
            // A gradient over static geometry has fixed bounds, so its shader is built once and
            // shared by every draw; one over an animated path (none in the corpus) is rebuilt
            // per frame from that frame's bounds.
            if (path != null)
            {
                _fillShader = fill?.Gradient?.ToShader(path.Bounds);
                _strokeShader = stroke?.Gradient?.ToShader(path.Bounds);
            }
        }

        /// <summary>The fill; null for <c>fill="none"</c> (the shape is stroke only).</summary>
        internal SvgPaint Fill { get; }

        /// <summary><c>fill-opacity</c> times the element's own <c>opacity</c>.</summary>
        internal float FillAlpha { get; }

        internal SvgPaint Stroke { get; }

        internal float StrokeAlpha { get; }

        internal float StrokeWidth { get; }

        internal SKBlendMode BlendMode { get; }

        internal bool IsAnimated => _dTrack != null;

        /// <summary>The <c>d</c> animation; null for fixed geometry.</summary>
        internal SmilTrack DTrack => _dTrack;

        internal override void Draw(SKCanvas canvas, double phase)
        {
            if (_dTrack == null)
            {
                DrawPath(canvas, _static, _fillShader, _strokeShader);
                return;
            }

            int count = _skeleton.NumberCount;
            float[] rented = count > 256 ? new float[count] : null;
            Span<float> numbers = rented ?? stackalloc float[count];
            _dTrack.Sample(phase, numbers);
            using var path = _skeleton.Build(numbers);
            DrawPath(canvas, path, null, null);
        }

        private void DrawPath(SKCanvas canvas, SKPath path, SKShader fillShader, SKShader strokeShader)
        {
            if (Fill != null && FillAlpha > 0)
            {
                using var paint = SvgPaint.MakePaint(Fill, fillShader, path.Bounds, FillAlpha, BlendMode, out var owned);
                using (owned)
                    canvas.DrawPath(path, paint);
            }
            if (Stroke != null && StrokeAlpha > 0 && StrokeWidth > 0)
            {
                using var paint = SvgPaint.MakePaint(Stroke, strokeShader, path.Bounds, StrokeAlpha, BlendMode, out var owned);
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = StrokeWidth;
                using (owned)
                    canvas.DrawPath(path, paint);
            }
        }

        internal override SvgNode Recolor(CursorPackPalette palette)
            => new SvgShape(_static, _skeleton, _dTrack, Fill?.Recolor(palette), FillAlpha,
                Stroke?.Recolor(palette), StrokeAlpha, StrokeWidth, BlendMode);
    }

    /// <summary>A <c>&lt;circle&gt;</c>, whose <c>cx</c>, <c>cy</c> and <c>r</c> may each animate.</summary>
    internal sealed class SvgCircle : SvgNode
    {
        private readonly SKShader _fillShader;
        private readonly SKShader _strokeShader;

        internal SvgCircle(float cx, float cy, float r, SmilTrack cxTrack, SmilTrack cyTrack, SmilTrack rTrack,
            SvgPaint fill, float fillAlpha, SvgPaint stroke, float strokeAlpha, float strokeWidth, SKBlendMode blendMode)
        {
            Cx = cx;
            Cy = cy;
            R = r;
            CxTrack = cxTrack;
            CyTrack = cyTrack;
            RTrack = rTrack;
            Fill = fill;
            FillAlpha = fillAlpha;
            Stroke = stroke;
            StrokeAlpha = strokeAlpha;
            StrokeWidth = strokeWidth;
            BlendMode = blendMode;
            // As for SvgShape: a userSpaceOnUse gradient never depends on the geometry, and an
            // objectBoundingBox one can be fixed only while the circle is still.
            bool still = cxTrack == null && cyTrack == null && rTrack == null;
            var bounds = new SKRect(cx - r, cy - r, cx + r, cy + r);
            if (fill?.Gradient != null && (still || !fill.Gradient.ObjectBoundingBox))
                _fillShader = fill.Gradient.ToShader(bounds);
            if (stroke?.Gradient != null && (still || !stroke.Gradient.ObjectBoundingBox))
                _strokeShader = stroke.Gradient.ToShader(bounds);
        }

        internal float Cx { get; }
        internal float Cy { get; }
        internal float R { get; }
        internal SmilTrack CxTrack { get; }
        internal SmilTrack CyTrack { get; }
        internal SmilTrack RTrack { get; }
        internal SvgPaint Fill { get; }
        internal float FillAlpha { get; }
        internal SvgPaint Stroke { get; }
        internal float StrokeAlpha { get; }
        internal float StrokeWidth { get; }
        internal SKBlendMode BlendMode { get; }

        internal bool IsAnimated => CxTrack != null || CyTrack != null || RTrack != null;

        internal override void Draw(SKCanvas canvas, double phase)
        {
            float cx = Cx, cy = Cy, r = R;
            Span<float> one = stackalloc float[1];
            if (CxTrack != null) { CxTrack.Sample(phase, one); cx = one[0]; }
            if (CyTrack != null) { CyTrack.Sample(phase, one); cy = one[0]; }
            if (RTrack != null) { RTrack.Sample(phase, one); r = one[0]; }
            if (r <= 0)
                return;

            var bounds = new SKRect(cx - r, cy - r, cx + r, cy + r);
            if (Fill != null && FillAlpha > 0)
            {
                using var paint = SvgPaint.MakePaint(Fill, _fillShader, bounds, FillAlpha, BlendMode, out var owned);
                using (owned)
                    canvas.DrawCircle(cx, cy, r, paint);
            }
            if (Stroke != null && StrokeAlpha > 0 && StrokeWidth > 0)
            {
                using var paint = SvgPaint.MakePaint(Stroke, _strokeShader, bounds, StrokeAlpha, BlendMode, out var owned);
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = StrokeWidth;
                using (owned)
                    canvas.DrawCircle(cx, cy, r, paint);
            }
        }

        internal override SvgNode Recolor(CursorPackPalette palette)
            => new SvgCircle(Cx, Cy, R, CxTrack, CyTrack, RTrack, Fill?.Recolor(palette), FillAlpha,
                Stroke?.Recolor(palette), StrokeAlpha, StrokeWidth, BlendMode);
    }

    /// <summary>A fill or stroke: either one ARGB color or a gradient.</summary>
    internal sealed class SvgPaint
    {
        internal SvgPaint(uint argb)
        {
            Argb = argb;
        }

        internal SvgPaint(SvgGradientSpec gradient)
        {
            Gradient = gradient;
            Argb = 0xFFFFFFFF;
        }

        /// <summary>The flat color; white (a no-op multiplier) when the paint is a gradient.</summary>
        internal uint Argb { get; }

        internal SvgGradientSpec Gradient { get; }

        internal SvgPaint Recolor(CursorPackPalette palette)
            => Gradient != null ? new SvgPaint(Gradient.Recolor(palette)) : new SvgPaint(palette.Resolve(Argb));

        /// <summary>
        /// An <see cref="SKPaint"/> for this paint at <paramref name="alpha"/>. A gradient uses
        /// <paramref name="cachedShader"/> when the node built one, else a shader for
        /// <paramref name="bounds"/> that the caller disposes through <paramref name="owned"/>.
        /// The alpha rides on the paint color in both cases: Skia multiplies a shader by it.
        /// </summary>
        internal static SKPaint MakePaint(SvgPaint paint, SKShader cachedShader, SKRect bounds, float alpha,
            SKBlendMode blendMode, out SKShader owned)
        {
            owned = null;
            var result = new SKPaint { IsAntialias = true, BlendMode = blendMode };
            if (paint.Gradient != null)
            {
                var shader = cachedShader;
                if (shader == null)
                    owned = shader = paint.Gradient.ToShader(bounds);
                result.Shader = shader;
                result.Color = SKColors.White.WithAlpha(SvgGroup.AlphaByte(alpha));
            }
            else
            {
                var color = new SKColor(paint.Argb);
                result.Color = color.WithAlpha(SvgGroup.AlphaByte(alpha * color.Alpha / 255.0));
            }
            return result;
        }
    }

    /// <summary>
    /// A <c>&lt;linearGradient&gt;</c> or <c>&lt;radialGradient&gt;</c> as read: geometry in
    /// either unit system, the <c>gradientTransform</c>, and the stops with their opacity
    /// folded into the stop colors.
    /// </summary>
    internal sealed class SvgGradientSpec
    {
        internal SvgGradientSpec(bool radial, float x1, float y1, float x2, float y2, float r,
            bool objectBoundingBox, SKMatrix gradientTransform, uint[] stopArgb, float[] stopOffsets)
        {
            Radial = radial;
            X1 = x1;
            Y1 = y1;
            X2 = x2;
            Y2 = y2;
            R = r;
            ObjectBoundingBox = objectBoundingBox;
            GradientTransform = gradientTransform;
            StopArgb = stopArgb;
            StopOffsets = stopOffsets;
        }

        internal bool Radial { get; }

        /// <summary>Linear: the start point. Radial: the center.</summary>
        internal float X1 { get; }
        internal float Y1 { get; }

        /// <summary>Linear: the end point. Unused for radial.</summary>
        internal float X2 { get; }
        internal float Y2 { get; }

        /// <summary>Radial: the radius.</summary>
        internal float R { get; }

        /// <summary>True when the coordinates are fractions of the painted shape's bounding box
        /// (SVG's default); false for <c>userSpaceOnUse</c>.</summary>
        internal bool ObjectBoundingBox { get; }

        internal SKMatrix GradientTransform { get; }

        internal uint[] StopArgb { get; }

        internal float[] StopOffsets { get; }

        /// <summary>
        /// The shader for a shape whose bounds are <paramref name="bounds"/>. For
        /// objectBoundingBox the local matrix maps the unit square onto the bounds and then
        /// applies the gradient transform (SVG post-multiplies it, i.e. it acts in gradient
        /// space first); for userSpaceOnUse the local matrix is the gradient transform alone,
        /// since shapes are drawn under the live group CTM. The caller owns the result.
        /// </summary>
        internal SKShader ToShader(SKRect bounds)
        {
            var local = GradientTransform;
            if (ObjectBoundingBox)
            {
                var bbox = SKMatrix.CreateScaleTranslation(bounds.Width, bounds.Height, bounds.Left, bounds.Top);
                local = GradientTransform.IsIdentity ? bbox : SKMatrix.Concat(bbox, GradientTransform);
            }

            var colors = new SKColor[StopArgb.Length];
            for (int i = 0; i < colors.Length; i++)
                colors[i] = new SKColor(StopArgb[i]);

            return Radial
                ? SKShader.CreateRadialGradient(new SKPoint(X1, Y1), R, colors, StopOffsets, SKShaderTileMode.Clamp, local)
                : SKShader.CreateLinearGradient(new SKPoint(X1, Y1), new SKPoint(X2, Y2), colors, StopOffsets, SKShaderTileMode.Clamp, local);
        }

        internal SvgGradientSpec Recolor(CursorPackPalette palette)
        {
            var stops = new uint[StopArgb.Length];
            for (int i = 0; i < stops.Length; i++)
                stops[i] = palette.Resolve(StopArgb[i]);
            return new SvgGradientSpec(Radial, X1, Y1, X2, Y2, R, ObjectBoundingBox, GradientTransform, stops, StopOffsets);
        }
    }
}

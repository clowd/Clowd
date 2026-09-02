using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using Clowd.VideoSDK.Composition;
using SkiaSharp;

namespace Clowd.UI.VideoEditor.Inspector
{
    /// <summary>
    /// One background style's actual wallpaper, in one of its themes, drawn through the very
    /// <see cref="BackgroundRenderer"/> call <c>FrameComposer</c> makes — so a tile is a preview of
    /// the render rather than a picture of it. The style tiles show each style on its default
    /// theme, and the theme tiles below them show the picked style in each theme it offers, which
    /// is the whole of what that choice does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unlike <see cref="CursorStylePreview"/>, which can draw with Avalonia geometry because a
    /// cursor glyph is a handful of paths, a wallpaper is Skia's to draw: gradients, blend modes,
    /// clip paths and a raster blur, none of which survive a translation into
    /// Avalonia primitives. So the tile takes Avalonia's own Skia lease and hands the compositor's
    /// renderer the leased canvas, exactly as <c>PreviewDrawOperation</c> does for the main canvas.
    /// On a backend that is not Skia the lease is absent and the tile draws nothing, which is the
    /// same graceful nothing the preview draws there.
    /// </para>
    /// <para>
    /// <b>These tiles carry the only clock in the feature.</b> The main canvas has none and must
    /// never grow one: its wallpaper's phase comes from the player's project ticks through
    /// <c>FrameComposer</c>, which is what makes a paused frame freeze, a scrub scrub the wallpaper
    /// and the export match the preview. A tile has no timeline to read, so it runs a stopwatch —
    /// and speeds it up, because a 60 s loop in a 34px tile reads as a still picture. The speedup
    /// scales the tiles' own seconds only; <see cref="BackgroundRenderer"/> never learns about it,
    /// so nothing that reaches the canvas or the file is affected.
    /// </para>
    /// <para>
    /// <b>An animated tile plays a pre-rendered loop, not the wallpaper.</b> A still can be
    /// recorded into an <c>SKPicture</c> and replayed for nothing, but an animated scene cannot:
    /// its geometry is a function of the phase, so every frame walks the SVG tree, and Breathing
    /// Field additionally rasterizes and blurs a fixed 480px working surface on the CPU, which
    /// measures 9.1 ms in a 34px swatch (Moving Blob and Moving Corners measure 0.015 ms). Nine
    /// milliseconds every frame on the thread that also composes the video preview is what made
    /// the picker feel heavy. So an animated tile draws a frame out of
    /// <see cref="BackgroundTileLoop"/> instead: one quad from one texture, generated from this
    /// very renderer by <c>tools/background-tiles</c>. The live path stays as the fallback for a
    /// style with no sheet, for the theme row's recolored tiles, and for the moments before the
    /// sheet has finished decoding. Nothing here needs the sheet to exist.
    /// </para>
    /// <para>
    /// <b>None of this reaches the canvas.</b> The preview and the export still draw the wallpaper
    /// live at the project's own tick; a pre-rendered loop belongs to a picker and nowhere else.
    /// </para>
    /// </remarks>
    public sealed class BackgroundStylePreview : Control
    {
        /// <summary>The <c>BackgroundContent.Style</c> wire name to draw. An id this build does not
        /// know draws the default style rather than an empty tile, mirroring the fallback
        /// <see cref="CursorStylePreview"/> applies to an unknown cursor style.</summary>
        public static readonly StyledProperty<string> StyleNameProperty =
            AvaloniaProperty.Register<BackgroundStylePreview, string>(nameof(StyleName));

        /// <summary>The <c>BackgroundContent.Theme</c> to draw it in, or null for the style's
        /// default — which is all a style with one colorway ever has.</summary>
        public static readonly StyledProperty<string> ThemeNameProperty =
            AvaloniaProperty.Register<BackgroundStylePreview, string>(nameof(ThemeName));

        /// <summary>The <c>BackgroundContent.Color</c> to fill with, read only by the solid style
        /// — the one style with no artwork, whose tile is the item's own color. Null draws
        /// <c>BackgroundContent.DefaultColor</c>, so a tile is never blank.</summary>
        public static readonly StyledProperty<string> ColorProperty =
            AvaloniaProperty.Register<BackgroundStylePreview, string>(nameof(Color));

        /// <summary>
        /// Whether the tiles under this control are on show, as an inherited attached property the
        /// BACKGROUND section sets from <c>ShowBackground</c>.
        ///
        /// This exists because the panel <b>hides</b> its sections, it does not remove them: when
        /// the selection moves off a wallpaper the section's <c>IsVisible</c> goes false and every
        /// tile inside it stays attached, templated and joined to <see cref="TileClock"/> — so
        /// without a second gate an animated style, once picked, would keep a dispatcher timer and
        /// ten invalidations per frame running for the life of the window, drawing nothing anyone
        /// can see (and pinned at the fastest rate, since a tile that is never painted reports no
        /// cost for the adaptive budget to spend). Avalonia offers no public notification for a
        /// change in <c>IsEffectivelyVisible</c>, and a tile's own <c>IsVisible</c> never moves
        /// when an ancestor is the one being hidden, so the section states it outright and
        /// inheritance carries it down through the two ListBoxes into their item templates.
        /// </summary>
        public static readonly AttachedProperty<bool> TilesLiveProperty =
            AvaloniaProperty.RegisterAttached<BackgroundStylePreview, Control, bool>(
                "TilesLive", defaultValue: true, inherits: true);

        public static void SetTilesLive(Control control, bool value) =>
            control?.SetValue(TilesLiveProperty, value);

        public static bool GetTilesLive(Control control) =>
            control?.GetValue(TilesLiveProperty) ?? true;

        /// <summary>
        /// Holds this tile at phase 0 even when its style loops. The THEME row sets it: those
        /// tiles differ from each other only in color, so the motion says nothing about the choice
        /// being made and reads as a row of things twitching under the one the user is trying to
        /// look at. The STYLE row leaves it off, where the motion is the distinguishing feature.
        /// </summary>
        public static readonly StyledProperty<bool> StillProperty =
            AvaloniaProperty.Register<BackgroundStylePreview, bool>(nameof(Still));

        public bool Still
        {
            get => GetValue(StillProperty);
            set => SetValue(StillProperty, value);
        }

        static BackgroundStylePreview()
        {
            // StyleNameProperty is deliberately absent: it also joins and leaves the shared clock,
            // so it is handled in OnPropertyChanged rather than invalidating twice
            // (ClickHighlightPreview leaves its own Animation property out for the same reason).
            AffectsRender<BackgroundStylePreview>(ThemeNameProperty, ColorProperty);
        }

        /// <summary>How much faster than real time the tiles play. The source wallpapers loop over
        /// 60 or 90 seconds, which is right on a screen you are looking at for minutes and reads as
        /// a frozen picture in a 34px tile you are looking at for two seconds.</summary>
        private const double TilePreviewSpeedup = 12.0;

        private bool _attached;
        private bool _animating;

        /// <summary>The sheet frame this tile last drew, or -1 when it last drew live. Only used
        /// to skip the invalidations between one sheet frame and the next: the clock ticks at 30fps
        /// and a sheet holds 12 frames per displayed second, so without this two paints in three
        /// would repaint the identical picture and drag the rest of the panel through a render pass
        /// with them.</summary>
        private int _drawnFrame = -1;

        public string StyleName
        {
            get => GetValue(StyleNameProperty);
            set => SetValue(StyleNameProperty, value);
        }

        public string ThemeName
        {
            get => GetValue(ThemeNameProperty);
            set => SetValue(ThemeNameProperty, value);
        }

        public string Color
        {
            get => GetValue(ColorProperty);
            set => SetValue(ColorProperty, value);
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _attached = true;
            UpdateClock();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            _attached = false;
            UpdateClock();
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == StyleNameProperty)
            {
                UpdateClock();
                InvalidateVisual();
            }
            else if (change.Property == StillProperty)
            {
                UpdateClock();
                InvalidateVisual();
            }
            else if (change.Property == TilesLiveProperty)
            {
                // the section was hidden or shown again; nothing to redraw either way, only a
                // clock to leave or rejoin
                UpdateClock();
            }
        }

        public override void Render(DrawingContext context)
        {
            if (Bounds.Width <= 0 || Bounds.Height <= 0)
                return;

            string style = BackgroundCatalog.ResolveStyle(StyleName);
            // zero for a static style — which is every style but three — so a still tile is drawn
            // once and never invalidated again
            double seconds = _animating ? TileClock.Seconds * TilePreviewSpeedup : 0;

            var sheet = SheetFor(style, out int frame);
            _drawnFrame = sheet == null ? -1 : frame;
            context.Custom(sheet != null
                ? new TileDrawOperation(new Rect(Bounds.Size), sheet, frame)
                : new TileDrawOperation(new Rect(Bounds.Size), style, ThemeName, seconds, Color));
        }

        /// <summary>
        /// The pre-rendered sheet to draw from and the frame in it, or null to draw the wallpaper
        /// live. A tile plays a sheet only while it is animating and only when its theme is the one
        /// the sheet was rendered in. The style row passes no theme, so it resolves to the style's
        /// first colorway, which is what the generator draws. A theme-row tile (recolored, and
        /// <see cref="Still"/> anyway) and any style with no sheet fall through to the live draw.
        /// </summary>
        private SKImage SheetFor(string style, out int frame)
        {
            frame = 0;
            if (!_animating)
                return null;

            var resolved = BackgroundCatalog.Find(style);
            if (resolved == null || !string.Equals(BackgroundCatalog.ResolveTheme(style, ThemeName),
                    BackgroundCatalog.ResolveTheme(style, null), StringComparison.OrdinalIgnoreCase))
                return null;

            // Asked for on the UI thread, so the render thread is only ever handed a decoded image;
            // null while the first ask is still decoding, which draws live for a frame or two.
            var sheet = BackgroundTileLoop.Get(style);
            if (sheet == null)
                return null;

            frame = BackgroundTileSheet.FrameIndexAt(resolved, PhaseNow(resolved));
            return sheet;
        }

        /// <summary>Where in its loop this tile is, through the same
        /// <see cref="BackgroundRenderer.PhaseOf(BackgroundStyle, long, double)"/> the composer
        /// uses, so a sheet frame is indexed by the phase the live draw would have been given
        /// and the two agree about where the loop is.</summary>
        private static double PhaseNow(BackgroundStyle style)
            => BackgroundRenderer.PhaseOf(style,
                (long)Math.Round(TileClock.Seconds * TilePreviewSpeedup * TimeSpan.TicksPerSecond));

        /// <summary>
        /// One tick of the shared clock. A tile playing a sheet repaints only when the frame it
        /// would draw has actually changed; a tile drawing live has a new picture every tick and
        /// always repaints.
        /// </summary>
        private void Tick()
        {
            if (_drawnFrame >= 0)
            {
                var resolved = BackgroundCatalog.Find(BackgroundCatalog.ResolveStyle(StyleName));
                if (resolved != null && BackgroundTileSheet.FrameIndexAt(resolved, PhaseNow(resolved)) == _drawnFrame)
                    return;
            }

            InvalidateVisual();
        }

        /// <summary>Joins the shared clock exactly while the style has motion in it and the tile is
        /// on screen; a static tile (one the panel has thrown away, or one in a section that is
        /// hidden) costs nothing. Leaving is not optional in any of the three cases: without it
        /// every tile the theme row re-templates away, and every tile left behind a hidden
        /// section, would keep being repainted for the life of the window.</summary>
        private void UpdateClock()
        {
            bool wanted = _attached && !Still && GetTilesLive(this) &&
                (BackgroundCatalog.Find(BackgroundCatalog.ResolveStyle(StyleName))?.IsAnimated ?? false);
            if (wanted == _animating)
                return;

            _animating = wanted;
            if (wanted)
                TileClock.Join(this);
            else
                TileClock.Leave(this);
        }

        /// <summary>
        /// The one clock every animated tile shares, and the frame budget that keeps it affordable.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why shared rather than one stopwatch per tile.</b> Tiles showing the same artwork then
        /// move together instead of at whatever offsets they happened to be created at, which reads
        /// as one wallpaper seen seven ways rather than seven loops out of step; and the repaints
        /// land in one dispatcher pass instead of N.
        /// </para>
        /// <para>
        /// <b>Why the budget.</b> Tile cost is not proportional to tile size. Breathing Field draws
        /// its blur on a fixed 480px CPU raster whatever rectangle it lands in (see
        /// <c>SvgGroup.DrawBlurred</c>), so one 34px tile of it measures 9.1 ms live, which at a
        /// flat 30fps would spend more than a quarter of the render thread on a swatch. Playing a
        /// pre-rendered sheet (see <see cref="BackgroundTileLoop"/>) is what actually fixed that,
        /// and with the sheets in place the measured total is a rounding error and this never
        /// stretches the interval past its floor. The budget stays because the live path stays: a
        /// style whose sheet is missing, or the seconds before one has decoded, still draws the
        /// wallpaper, and the tiles then throttle themselves exactly as they used to rather than
        /// pegging the render thread. Adaptive rather than a hardcoded list of expensive styles,
        /// because the catalog is data and gains rows without this file hearing about it.
        /// </para>
        /// </remarks>
        private static class TileClock
        {
            /// <summary>~30fps, the rate the click-highlight tiles and the preview player both use,
            /// and the fastest this clock ever runs.</summary>
            private const double MinIntervalMs = 33.0;

            /// <summary>The slowest it ever runs. Below this the motion stops reading as motion, so
            /// a machine that cannot afford even this gets a choppy tile rather than a stopped
            /// one.</summary>
            private const double MaxIntervalMs = 200.0;

            /// <summary>The share of the interval the tiles' own drawing may take. One fifth leaves
            /// the render thread free for the timeline, the canvas and the rest of the panel.</summary>
            private const double FrameBudget = 0.2;

            /// <summary>How far the wanted interval must move before it is applied. Assigning
            /// <see cref="DispatcherTimer.Interval"/> restarts the timer, and the measurement is
            /// noisy by a fraction of a millisecond, so a bare comparison would rearm it every
            /// tick.</summary>
            private const double Hysteresis = 1.25;

            private static readonly Stopwatch Clock = new Stopwatch();
            private static readonly List<BackgroundStylePreview> Tiles = new List<BackgroundStylePreview>();
            private static DispatcherTimer _timer;

            // written from the render thread (the custom draw operation), read and cleared from the
            // UI thread on the next tick; in Stopwatch ticks so the add can be interlocked
            private static long _drawTicks;

            /// <summary>The instant the tiles draw, in real seconds since the first tile started
            /// animating. Never reset while any tile is animating, so tiles that come and go with
            /// the theme row join the motion already in progress rather than restarting it.</summary>
            public static double Seconds => Clock.Elapsed.TotalSeconds;

            public static void Join(BackgroundStylePreview tile)
            {
                if (Tiles.Contains(tile))
                    return;

                Tiles.Add(tile);
                if (Clock.IsRunning)
                    return;

                // starting from nothing: the accumulator has been collecting the static tiles'
                // one-off draws (including the first-use parse of a wallpaper) since the timer last
                // ran, and charging those to the first tick would stretch it to the floor rate for
                // no reason
                Interlocked.Exchange(ref _drawTicks, 0);
                Clock.Start();
                _timer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(MinIntervalMs),
                    DispatcherPriority.Background, (_, _) => OnTick());
                _timer.Interval = TimeSpan.FromMilliseconds(MinIntervalMs);
                _timer.Start();
            }

            public static void Leave(BackgroundStylePreview tile)
            {
                Tiles.Remove(tile);
                if (Tiles.Count > 0)
                    return;

                _timer?.Stop();
                // the clock stops with the last tile but keeps its reading, so a panel reopened a
                // moment later picks the loop up where it was rather than snapping to the top
                Clock.Stop();
            }

            /// <summary>What one tile's draw cost, from the render thread. Interlocked because the
            /// tiles of one pass are drawn there while the UI thread may be reading the total.</summary>
            public static void Spent(long stopwatchTicks) => Interlocked.Add(ref _drawTicks, stopwatchTicks);

            private static void OnTick()
            {
                long spent = Interlocked.Exchange(ref _drawTicks, 0);
                double spentMs = spent * 1000.0 / Stopwatch.Frequency;
                double wanted = Math.Clamp(spentMs / FrameBudget, MinIntervalMs, MaxIntervalMs);
                double current = _timer.Interval.TotalMilliseconds;
                if (wanted > current * Hysteresis || wanted * Hysteresis < current)
                    _timer.Interval = TimeSpan.FromMilliseconds(wanted);

                foreach (var tile in Tiles)
                    tile.Tick();
            }
        }

        /// <summary>
        /// The draw itself, on Avalonia's own leased Skia canvas: either one frame out of a
        /// pre-rendered sheet or, when there is no sheet to draw, the wallpaper live. Nested and
        /// private because nothing else will ever construct one. Unlike <c>PreviewDrawOperation</c>
        /// there is nothing here to keep alive, since the strings are immutable and the sheet is
        /// owned for the life of the process by <see cref="BackgroundTileLoop"/>, so there is no
        /// reference counting and <see cref="Dispose"/> has nothing to do.
        /// </summary>
        private sealed class TileDrawOperation : ICustomDrawOperation
        {
            private readonly string _style;
            private readonly string _theme;
            private readonly string _color;
            private readonly double _timeSeconds;
            private readonly SKImage _sheet;
            private readonly int _frame;

            public TileDrawOperation(Rect bounds, string style, string theme, double timeSeconds, string color)
            {
                Bounds = bounds;
                _style = style;
                _theme = theme;
                _timeSeconds = timeSeconds;
                _color = color;
            }

            /// <summary>Draws frame <paramref name="frame"/> of <paramref name="sheet"/> instead of
            /// the wallpaper. The image is already decoded, and every tile of one style hands over
            /// the same one, so Skia uploads a single texture and every frame after that is one
            /// quad out of it.</summary>
            public TileDrawOperation(Rect bounds, SKImage sheet, int frame)
            {
                Bounds = bounds;
                _sheet = sheet;
                _frame = frame;
            }

            public Rect Bounds { get; }

            public bool HitTest(Point p) => false;

            /// <summary>Never equal, so Avalonia never takes a previous pass's operation for this
            /// one — the same answer <c>PreviewDrawOperation</c> gives, and the one an animated tile
            /// needs.</summary>
            public bool Equals(ICustomDrawOperation other) => false;

            public void Dispose()
            {
            }

            public void Render(ImmediateDrawingContext context)
            {
                if (Bounds.Width < 1 || Bounds.Height < 1)
                    return;

                var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
                if (feature == null)
                    return; // not the Skia backend — nothing we can draw onto

                using var lease = feature.Lease();
                var canvas = lease.SkCanvas;
                if (canvas == null)
                    return;

                // the leased canvas is already transformed into the control's coordinate space, so
                // the destination is the control's own box at the origin
                var dest = SKRect.Create(0, 0, (float)Bounds.Width, (float)Bounds.Height);
                long started = Stopwatch.GetTimestamp();
                int save = canvas.Save();
                try
                {
                    canvas.ClipRect(dest);
                    if (_sheet != null)
                        DrawSheetFrame(canvas, dest);
                    else
                        BackgroundRenderer.Draw(canvas, dest, _style, _theme, _timeSeconds, 1.0, _color);
                }
                finally
                {
                    canvas.RestoreToCount(save);
                    TileClock.Spent(Stopwatch.GetTimestamp() - started);
                }
            }

            /// <summary>The sheet frame, cover-fitted into the tile by
            /// <see cref="BackgroundTileSheet.SourceRectFor"/>, which is where the reasoning about
            /// why a second cover-fit is exact lives.</summary>
            private void DrawSheetFrame(SKCanvas canvas, SKRect dest)
                => canvas.DrawImage(_sheet, BackgroundTileSheet.SourceRectFor(_frame, dest), dest,
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), Paint);

            /// <summary>Shared and never disposed: it carries no state beyond the antialias flag,
            /// and building one per frame per tile would be the only allocation in this path.</summary>
            private static readonly SKPaint Paint = new SKPaint { IsAntialias = true };
        }
    }
}

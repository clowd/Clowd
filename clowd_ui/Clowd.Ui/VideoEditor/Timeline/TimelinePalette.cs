using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace Clowd.UI.VideoEditor.Timeline
{
    /// <summary>
    /// Every brush and pen the timeline draws with, resolved once per <see cref="ThemeVariant"/>
    /// from the Semi theme resources (with hard-coded fallbacks for the ones a theme may not carry)
    /// and cached — the ruler, the surface and the track headers all share one instance, and a
    /// per-frame resource lookup on a control that repaints on every pointer move would be
    /// pointless work.
    ///
    /// The cache is keyed by variant only, exactly as the single-track control's was: the accent is
    /// read at build time, so a live accent change is picked up on the next theme switch. UI thread
    /// only.
    /// </summary>
    internal sealed class TimelinePalette
    {
        private static readonly Dictionary<ThemeVariant, TimelinePalette> _cache = new();

        /// <summary>The palette for a variant — <c>ActualThemeVariant</c> of the control being
        /// drawn, resolved by <see cref="Resolve"/>. Built on first use and kept.</summary>
        public static TimelinePalette ForVariant(ThemeVariant variant)
        {
            variant = Resolve(variant);
            if (_cache.TryGetValue(variant, out var cached))
                return cached;

            var palette = Build(variant);
            _cache[variant] = palette;
            return palette;
        }

        /// <summary>
        /// The variant to actually paint in. A control's <c>ActualThemeVariant</c> is null until it
        /// is attached to the tree, and the first <c>RefreshChrome</c> runs from the constructor —
        /// without this, that first pass builds (and caches under a bogus key) a light palette and
        /// paints the corner cell and the scroll host from it. <see cref="Application"/> resolves
        /// the system theme even when <c>RequestedThemeVariant</c> is Default, so it is the one to
        /// ask, exactly as the single-track control this replaced did via <c>AppStyles</c>.
        /// </summary>
        private static ThemeVariant Resolve(ThemeVariant variant)
        {
            if (variant != null && variant != ThemeVariant.Default)
                return variant;

            var appVariant = Application.Current?.ActualThemeVariant;
            return appVariant != null && appVariant != ThemeVariant.Default ? appVariant : ThemeVariant.Light;
        }

        // ------------------------------------------------------------------------------- surfaces

        /// <summary>Behind the rows, where no row reaches (short projects, below the last row).</summary>
        public IBrush SurfaceBackground { get; private init; }

        /// <summary>Row background, alternating so adjacent rows read apart without a heavy rule.</summary>
        public IBrush RowBackground { get; private init; }

        public IBrush RowBackgroundAlt { get; private init; }

        public Pen RowSeparatorPen { get; private init; }

        public IBrush RulerBackground { get; private init; }

        // ---------------------------------------------------------------------------------- ruler

        /// <summary>The ruler's notches and timestamps. Stronger than <see cref="LabelBrush"/> on
        /// purpose: the time strip is the timeline's scale, read at a glance while dragging
        /// something else, so it carries full-weight text colour rather than the muted chrome the
        /// track headers use.</summary>
        public Pen RulerTickPen { get; private init; }

        public Pen RulerMinorTickPen { get; private init; }

        public IBrush RulerLabelBrush { get; private init; }

        /// <summary>The track headers' link badge — orange, so the "this row moves with the
        /// recording" mark stands out from the neutral button cluster around it (and from the
        /// accent, which selection owns).</summary>
        public IBrush LinkBadgeBrush { get; private init; }

        /// <summary>Muted chrome text: the track headers' names. (The corner buttons use
        /// <see cref="RulerLabelBrush"/> — at this weight they read as disabled.)</summary>
        public IBrush LabelBrush { get; private init; }

        /// <summary>The track headers' drag grip. Deliberately stronger than
        /// <see cref="LabelBrush"/>: the dots are a small target and the only thing saying a row can
        /// be dragged, so at label weight they read as dirt on the screen rather than a handle.</summary>
        public IBrush GripBrush { get; private init; }

        /// <summary>The grip's dots under the pointer — the hover cue is the dots themselves
        /// brightening (near-white in the dark theme, near-black in the light), not a background
        /// behind them.</summary>
        public IBrush GripHoverBrush { get; private init; }

        // ---------------------------------------------------------------------------------- items

        private IBrush _videoFill;
        private IBrush _audioFill;
        private IBrush _textFill;
        private IBrush _imageFill;
        private IBrush _speedFill;
        private IBrush _zoomFill;
        private IBrush _cursorFill;
        private IBrush _keyboardFill;

        /// <summary>Body fill for an item on a row of this kind. Video keeps the accent (it is the
        /// recording, the thing the editor is about); the other kinds get their own hue so a glance
        /// down the timeline reads as rows of different things.</summary>
        public IBrush ItemFill(TimelineRowKind kind) => kind switch
        {
            TimelineRowKind.Audio => _audioFill,
            TimelineRowKind.Text => _textFill,
            TimelineRowKind.Image => _imageFill,
            TimelineRowKind.Speed => _speedFill,
            TimelineRowKind.Zoom => _zoomFill,
            TimelineRowKind.Cursor => _cursorFill,
            TimelineRowKind.Keyboard => _keyboardFill,
            _ => _videoFill,
        };

        public Pen ItemBorderPen { get; private init; }

        /// <summary>Item name / text-content label drawn inside the body.</summary>
        public IBrush ItemLabelBrush { get; private init; }

        /// <summary>Filmstrip placeholder — what a video item shows where a thumbnail has not been
        /// decoded yet.</summary>
        public IBrush FilmstripPlaceholderFill { get; private init; }

        /// <summary>Waveform body on an audio item.</summary>
        public IBrush WaveformBrush { get; private init; }

        private IBrush _edgeFadeLeftEven;
        private IBrush _edgeFadeLeftOdd;
        private IBrush _edgeFadeRightEven;
        private IBrush _edgeFadeRightOdd;

        /// <summary>Gradient to the row background laid over an item's end where the viewport cuts
        /// it off. Per row parity because the row fills alternate — and built from the row colour
        /// composited over the surface, since the Semi fills are translucent overlays and a
        /// gradient of the raw overlay would never fully dissolve the item.</summary>
        public IBrush ItemEdgeFade(bool evenRow, bool leftEdge) => leftEdge
            ? (evenRow ? _edgeFadeLeftEven : _edgeFadeLeftOdd)
            : (evenRow ? _edgeFadeRightEven : _edgeFadeRightOdd);

        // ------------------------------------------------------------------ selection and state

        /// <summary>Border of the selected item.</summary>
        public Pen SelectionPen { get; private init; }

        /// <summary>Accent the selection (and the header's active row) is drawn in.</summary>
        public Color SelectionAccent { get; private init; }

        /// <summary>Body of a trim handle on a merely hovered item — a translucent white slab, so
        /// the item's own fill still reads through and the handle does not claim the item is
        /// selected.</summary>
        public IBrush TrimHandleHoverFill { get; private init; }

        /// <summary>The two grip lines inside a hovered handle. Dark, because the hover slab is
        /// light whatever the theme.</summary>
        public IBrush TrimHandleHoverLine { get; private init; }

        /// <summary>Body of a trim handle on the selected item — the selection accent, so the
        /// handles read as part of the selection border rather than as separate chrome.</summary>
        public IBrush TrimHandleActiveFill { get; private init; }

        /// <summary>The two grip lines inside a selected handle, over the accent slab.</summary>
        public IBrush TrimHandleActiveLine { get; private init; }

        /// <summary>Hover highlight laid over an item body.</summary>
        public IBrush HoverOverlay { get; private init; }

        /// <summary>Wash over hidden/muted rows.</summary>
        public IBrush DimFill { get; private init; }

        /// <summary>Diagonal hatch over hidden/muted (and locked) items.</summary>
        public Pen HatchPen { get; private init; }

        // ---------------------------------------------------------------- transitions and guides

        /// <summary>Translucent tint of the entry/exit transition ramp triangles.</summary>
        public IBrush TransitionFill { get; private init; }

        /// <summary>Diagonal accent line along the ramp's hypotenuse.</summary>
        public Pen TransitionEdgePen { get; private init; }

        /// <summary>Vertical guide shown while a drag is snapped to a target.</summary>
        public Pen SnapGuidePen { get; private init; }

        /// <summary>The line the track headers lay across a row boundary to show where a row being
        /// dragged by its grip would land.</summary>
        public IBrush DropIndicatorBrush { get; private init; }

        public Pen PlayheadPen { get; private init; }


        /// <summary>The ghost playhead the ruler draws under the pointer — where a click would put
        /// the real one. Blue, so it never reads as the (red) playhead itself.</summary>
        public IBrush HoverPlayheadBrush { get; private init; }

        public Pen HoverPlayheadPen { get; private init; }

        // ------------------------------------------------------------------------------- building

        private static TimelinePalette Build(ThemeVariant variant)
        {
            var dark = variant == ThemeVariant.Dark;
            var accent = GetThemeColor(variant, "SemiColorPrimary", AppStyles.AccentColor);

            var fill0 = GetThemeColor(variant, "SemiColorFill0", dark ? Color.FromRgb(38, 38, 41) : Color.FromRgb(240, 241, 243));
            var fill1 = GetThemeColor(variant, "SemiColorFill1", dark ? Color.FromRgb(45, 45, 48) : Color.FromRgb(222, 224, 227));
            var text1 = GetThemeColor(variant, "SemiColorText1", dark ? Color.FromRgb(228, 228, 230) : Color.FromRgb(40, 42, 46));
            var text2 = GetThemeColor(variant, "SemiColorText2", dark ? Color.FromRgb(200, 200, 200) : Color.FromRgb(70, 72, 76));
            var text3 = GetThemeColor(variant, "SemiColorText3", dark ? Color.FromRgb(140, 140, 140) : Color.FromRgb(130, 133, 138));
            var border = GetThemeColor(variant, "SemiColorBorder", dark ? Color.FromRgb(60, 60, 64) : Color.FromRgb(200, 202, 206));

            // Per-kind hues. Audio/text/image/speed/zoom are fixed rather than accent-derived:
            // they have to stay apart from the accent (which the recording rows use) whatever the
            // accent is.
            var audio = dark ? Color.FromRgb(52, 140, 108) : Color.FromRgb(70, 165, 128);
            var text = dark ? Color.FromRgb(118, 92, 176) : Color.FromRgb(139, 112, 199);
            var image = dark ? Color.FromRgb(176, 122, 52) : Color.FromRgb(204, 148, 70);
            var speed = dark ? Color.FromRgb(184, 70, 92) : Color.FromRgb(206, 86, 108);
            var zoom = dark ? Color.FromRgb(46, 136, 150) : Color.FromRgb(54, 154, 170);

            // the two input-capture overlays. They sit right against the (accent) recording rows,
            // so both stay well off blue: orchid for the cursor and olive for the keys, which also
            // keeps them apart from the purple text row directly and from each other at a glance.
            var cursor = dark ? Color.FromRgb(158, 74, 158) : Color.FromRgb(182, 92, 182);
            var keyboard = dark ? Color.FromRgb(132, 144, 56) : Color.FromRgb(152, 166, 70);

            var playheadColor = dark ? Color.FromRgb(240, 82, 82) : Color.FromRgb(212, 48, 48);

            var surfaceColor = dark ? Color.FromRgb(30, 30, 32) : Color.FromRgb(232, 233, 236);
            var rowEven = CompositeOver(fill1, surfaceColor);
            var rowOdd = CompositeOver(fill0, surfaceColor);

            // fixed blue rather than the accent: the ghost has to stay distinct from the playhead
            // AND from the selection/snap chrome, whatever accent the theme carries.
            var hoverPlayheadColor = dark ? Color.FromRgb(88, 158, 255) : Color.FromRgb(32, 108, 220);

            return new TimelinePalette
            {
                SurfaceBackground = new SolidColorBrush(surfaceColor),
                RowBackground = new SolidColorBrush(fill1),
                RowBackgroundAlt = new SolidColorBrush(fill0),
                RowSeparatorPen = new Pen(new SolidColorBrush(border, 0.7), 1),
                RulerBackground = new SolidColorBrush(fill0),

                RulerTickPen = new Pen(new SolidColorBrush(text1), 1.5),
                RulerMinorTickPen = new Pen(new SolidColorBrush(text2, 0.75), 1),
                RulerLabelBrush = new SolidColorBrush(text1),
                LabelBrush = new SolidColorBrush(text3),
                LinkBadgeBrush = new SolidColorBrush(dark ? Color.FromRgb(255, 159, 67) : Color.FromRgb(224, 113, 22)),
                GripBrush = new SolidColorBrush(dark ? Color.FromRgb(215, 215, 218) : Color.FromRgb(70, 72, 78)),
                GripHoverBrush = new SolidColorBrush(dark ? Colors.White : Color.FromRgb(20, 22, 26)),

                _videoFill = new SolidColorBrush(accent, dark ? 0.85 : 0.9),
                _audioFill = new SolidColorBrush(audio, dark ? 0.85 : 0.9),
                _textFill = new SolidColorBrush(text, dark ? 0.85 : 0.9),
                _imageFill = new SolidColorBrush(image, dark ? 0.85 : 0.9),
                _speedFill = new SolidColorBrush(speed, dark ? 0.85 : 0.9),
                _zoomFill = new SolidColorBrush(zoom, dark ? 0.85 : 0.9),
                _cursorFill = new SolidColorBrush(cursor, dark ? 0.85 : 0.9),
                _keyboardFill = new SolidColorBrush(keyboard, dark ? 0.85 : 0.9),
                ItemBorderPen = new Pen(new SolidColorBrush(dark ? Colors.Black : Colors.White, 0.35), 1),
                ItemLabelBrush = new SolidColorBrush(dark ? Color.FromRgb(240, 240, 240) : Colors.White),
                FilmstripPlaceholderFill = new SolidColorBrush(dark ? Colors.Black : Colors.White, 0.12),
                WaveformBrush = new SolidColorBrush(dark ? Color.FromRgb(226, 244, 236) : Color.FromRgb(28, 62, 48), 0.8),
                _edgeFadeLeftEven = EdgeFadeBrush(rowEven, fromLeft: true),
                _edgeFadeLeftOdd = EdgeFadeBrush(rowOdd, fromLeft: true),
                _edgeFadeRightEven = EdgeFadeBrush(rowEven, fromLeft: false),
                _edgeFadeRightOdd = EdgeFadeBrush(rowOdd, fromLeft: false),

                SelectionAccent = accent,
                SelectionPen = new Pen(new SolidColorBrush(accent), 2),
                TrimHandleHoverFill = new SolidColorBrush(Colors.White, 0.65),
                TrimHandleHoverLine = new SolidColorBrush(Color.FromRgb(20, 22, 26), 0.85),
                TrimHandleActiveFill = new SolidColorBrush(accent),
                TrimHandleActiveLine = new SolidColorBrush(Colors.White, 0.95),
                HoverOverlay = new SolidColorBrush(dark ? Colors.White : Colors.Black, 0.08),
                DimFill = new SolidColorBrush(dark ? Colors.Black : Colors.White, 0.5),
                HatchPen = new Pen(new SolidColorBrush(dark ? Colors.White : Colors.Black, 0.18), 1),

                TransitionFill = new SolidColorBrush(dark ? Colors.Black : Colors.White, 0.45),
                TransitionEdgePen = new Pen(new SolidColorBrush(dark ? Colors.White : Colors.Black, 0.55), 1),
                SnapGuidePen = new Pen(new SolidColorBrush(accent), 1, new DashStyle(new double[] { 3, 3 }, 0)),
                DropIndicatorBrush = new SolidColorBrush(accent),

                // fully opaque, and an even whole-pixel width so the snapped centre puts both
                // halves of the stroke on real pixels (1.5 could only ever be antialiased).
                PlayheadPen = new Pen(new SolidColorBrush(playheadColor), 2),
                HoverPlayheadBrush = new SolidColorBrush(hoverPlayheadColor),
                HoverPlayheadPen = new Pen(new SolidColorBrush(hoverPlayheadColor), 2),
            };
        }

        /// <summary>
        /// A Semi colour token as a plain <see cref="Color"/>. Semi expresses its neutral tokens as
        /// <i>translucent overlays</i> — <c>SemiColorFill0</c> in the dark theme is White at
        /// <c>Opacity 0.12</c>, not a dark grey — so the brush's <see cref="IBrush.Opacity"/> is
        /// folded into the returned alpha. Reading <c>brush.Color</c> alone hands back opaque white
        /// and paints the ruler, the corner cell and the track headers a flat white slab inside the
        /// (hard-coded dark) editor chrome.
        /// </summary>
        private static Color GetThemeColor(ThemeVariant variant, string key, Color fallback)
        {
            var app = Application.Current;
            if (app != null && app.TryGetResource(key, variant, out var value))
            {
                if (value is ISolidColorBrush brush)
                    return WithOpacity(brush.Color, brush.Opacity);
                if (value is Color color)
                    return color;
            }

            return fallback;
        }

        /// <summary>Alpha-composites a translucent overlay onto an opaque backing colour.</summary>
        private static Color CompositeOver(Color over, Color under)
        {
            var a = over.A / 255.0;
            return Color.FromRgb(
                (byte)Math.Round(over.R * a + under.R * (1 - a)),
                (byte)Math.Round(over.G * a + under.G * (1 - a)),
                (byte)Math.Round(over.B * a + under.B * (1 - a)));
        }

        /// <summary>A horizontal gradient from an opaque colour to the same colour fully
        /// transparent — relative to whatever rect it fills.</summary>
        private static IBrush EdgeFadeBrush(Color color, bool fromLeft)
        {
            var clear = Color.FromArgb(0, color.R, color.G, color.B);
            return new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops = new GradientStops
                {
                    new GradientStop(fromLeft ? color : clear, 0),
                    new GradientStop(fromLeft ? clear : color, 1),
                },
            };
        }

        private static Color WithOpacity(Color color, double opacity)
        {
            if (Double.IsNaN(opacity) || opacity >= 1)
                return color;

            var alpha = color.A * Math.Clamp(opacity, 0, 1);
            return Color.FromArgb((byte)Math.Round(alpha), color.R, color.G, color.B);
        }
    }
}

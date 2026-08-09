using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Clowd.Video;

// The pixel<->time and hit-test math lives in the internal TimelineMath class below so the
// existing Clowd.Video.Tests project can unit-test it against the real production code.
[assembly: InternalsVisibleTo("Clowd.Video.Tests")]

namespace Clowd.UI.VideoEditor
{
    /// <summary>
    /// The video-editor timeline: a time ruler, a track bar showing kept/cut/trimmed regions of the
    /// source recording, draggable trim handles and cut edges, and a scrubbing playhead. All edits
    /// mutate the attached <see cref="VideoEditDocument"/> directly — its clamp/merge logic is the
    /// single source of truth; the control re-reads the document after every mutation (following the
    /// pointer-capture pattern of <c>ColorSlider</c>).
    /// </summary>
    public partial class TimelineControl : Control
    {
        public static readonly StyledProperty<TimeSpan> DurationProperty =
            AvaloniaProperty.Register<TimelineControl, TimeSpan>(nameof(Duration));

        /// <summary>Total duration of the source media. Positions are mapped against this.</summary>
        public TimeSpan Duration
        {
            get => GetValue(DurationProperty);
            set => SetValue(DurationProperty, value);
        }

        public static readonly StyledProperty<TimeSpan> PositionProperty =
            AvaloniaProperty.Register<TimelineControl, TimeSpan>(nameof(Position), defaultBindingMode: BindingMode.TwoWay);

        /// <summary>Current playhead position in source time. Two-way: scrubbing writes it back.</summary>
        public TimeSpan Position
        {
            get => GetValue(PositionProperty);
            set => SetValue(PositionProperty, value);
        }

        /// <summary>
        /// The edit document rendered and mutated by this control. Setting it re-subscribes
        /// PropertyChanged (detaching from any previous document) and invalidates the visual.
        /// </summary>
        public VideoEditDocument Document
        {
            get => _document;
            set
            {
                if (ReferenceEquals(_document, value))
                    return;

                if (_document != null)
                    _document.PropertyChanged -= OnDocumentPropertyChanged;

                _document = value;
                _selectedCut = null;
                _dragMode = DragMode.None;
                _dragBaseCuts = null;

                if (_document != null)
                    _document.PropertyChanged += OnDocumentPropertyChanged;

                InvalidateVisual();
            }
        }

        /// <summary>Raised when the user starts scrubbing (pressing the playhead or the empty track).</summary>
        public event EventHandler ScrubStarted;

        /// <summary>Raised for every position change while the user is scrubbing.</summary>
        public event EventHandler<TimeSpan> Scrubbed;

        /// <summary>Raised when the user releases a scrub drag, with the final position.</summary>
        public event EventHandler<TimeSpan> ScrubCompleted;

        /// <summary>
        /// Removes the currently selected cut region from the document. The window forwards the
        /// Delete key here. Returns false when nothing was selected (or nothing was removed).
        /// </summary>
        public bool DeleteSelectedCut()
        {
            if (_document == null || _selectedCut == null)
                return false;

            var cut = _selectedCut;
            _selectedCut = null;
            var removed = _document.RemoveCut(cut);
            InvalidateVisual();
            return removed;
        }

        static TimelineControl()
        {
            AffectsRender<TimelineControl>(DurationProperty, PositionProperty);
        }

        // ------------------------------------------------------------------ layout constants

        private const double RulerHeight = 20;      // top strip with ticks + labels; playhead triangle lives here
        private const double TrackGap = 3;          // space between ruler and track bar
        private const double EdgePad = 10;          // horizontal inset so edge grips are not clipped
        private const double BottomPad = 3;
        private const double GripWidth = 7;         // trim handle width
        private const double CutGripWidth = 3;      // cut-edge grip bar width

        private enum DragMode { None, Scrub, TrimStart, TrimEnd, CutStart, CutEnd, CutBody }

        private VideoEditDocument _document;
        private CutRegion _selectedCut;

        private DragMode _dragMode = DragMode.None;
        private List<CutRegion> _dragBaseCuts;      // cut list at drag start, minus the dragged cut
        private long _dragFixedEdgeMs;              // for edge resize: the edge that stays put
        private long _dragGrabOffsetMs;             // for body move: pointer-ms minus cut-start-ms
        private long _dragCutLengthMs;              // for body move: original cut length
        private bool _applyingEdit;                 // re-entrancy guard: our own mutation is raising Cuts changed

        private Cursor _cursorResize;
        private Cursor _cursorHand;

        private ThemeVariant _paletteVariant;
        private Palette _palette;

        // ------------------------------------------------------------------ geometry helpers

        private long DurationMs => (long)Duration.TotalMilliseconds;

        private double TrackX => EdgePad;
        private double TrackWidth => Math.Max(0, Bounds.Width - EdgePad * 2);
        private double TrackTop => RulerHeight + TrackGap;
        private double TrackBottom => Math.Max(TrackTop, Bounds.Height - BottomPad);

        private Rect TrackRect => new Rect(TrackX, TrackTop, TrackWidth, Math.Max(0, TrackBottom - TrackTop));

        private double TimeToX(long ms) => TimelineMath.TimeToX(ms, DurationMs, TrackX, TrackWidth);
        private long XToMs(double x) => TimelineMath.XToMs(x, DurationMs, TrackX, TrackWidth);

        private long EffectiveTrimEndMs
        {
            get
            {
                var doc = _document;
                return doc == null ? DurationMs : TimelineMath.EffectiveTrimEnd(doc.TrimEndMs, DurationMs);
            }
        }

        private long TrimStartMs
        {
            get
            {
                var doc = _document;
                return doc == null ? 0 : Math.Clamp(doc.TrimStartMs, 0, DurationMs);
            }
        }

        // ------------------------------------------------------------------ document events

        private void OnDocumentPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            // While a drag is applying edits the document may normalize/merge and raise Cuts
            // changed re-entrantly; the drag logic re-resolves the selection itself, so only
            // reconcile here for external mutations (e.g. the window's Add Cut button).
            if (e.PropertyName == nameof(VideoEditDocument.Cuts) && !_applyingEdit && _dragMode == DragMode.None)
            {
                if (_selectedCut != null && _document != null && !ContainsCut(_document.Cuts, _selectedCut))
                    _selectedCut = null;
            }

            InvalidateVisual();
        }

        private static bool ContainsCut(IReadOnlyList<CutRegion> cuts, CutRegion cut)
        {
            for (int i = 0; i < cuts.Count; i++)
                if (cuts[i] == cut)
                    return true;
            return false;
        }

        // ------------------------------------------------------------------ interaction

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);

            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                return;

            var durationMs = DurationMs;
            if (durationMs <= 0 || TrackWidth <= 0)
                return;

            var pos = e.GetPosition(this);
            var hit = HitTestAt(pos.X);

            e.Pointer.Capture(this);

            switch (hit.Kind)
            {
                case TimelineHitKind.Playhead:
                case TimelineHitKind.Track:
                    // Scrub: a press on the empty track jumps there, then drags.
                    if (_selectedCut != null)
                    {
                        _selectedCut = null;
                        InvalidateVisual();
                    }

                    _dragMode = DragMode.Scrub;
                    ScrubStarted?.Invoke(this, EventArgs.Empty);
                    ApplyScrub(pos.X);
                    break;

                case TimelineHitKind.TrimStart:
                    _dragMode = DragMode.TrimStart;
                    break;

                case TimelineHitKind.TrimEnd:
                    _dragMode = DragMode.TrimEnd;
                    break;

                case TimelineHitKind.CutStart:
                case TimelineHitKind.CutEnd:
                {
                    var cut = _document.Cuts[hit.CutIndex];
                    SelectCut(cut);
                    _dragMode = hit.Kind == TimelineHitKind.CutStart ? DragMode.CutStart : DragMode.CutEnd;
                    _dragFixedEdgeMs = hit.Kind == TimelineHitKind.CutStart ? cut.EndMs : cut.StartMs;
                    _dragBaseCuts = BuildBaseCuts(cut);
                    break;
                }

                case TimelineHitKind.CutBody:
                {
                    var cut = _document.Cuts[hit.CutIndex];
                    SelectCut(cut);
                    _dragMode = DragMode.CutBody;
                    _dragCutLengthMs = cut.DurationMs;
                    _dragGrabOffsetMs = TimelineMath.MsAtX(pos.X, durationMs, TrackX, TrackWidth) - cut.StartMs;
                    _dragBaseCuts = BuildBaseCuts(cut);
                    break;
                }
            }
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);

            var pos = e.GetPosition(this);

            if (!Equals(e.Pointer.Captured, this) || _dragMode == DragMode.None)
            {
                UpdateCursor(pos.X);
                return;
            }

            switch (_dragMode)
            {
                case DragMode.Scrub:
                    ApplyScrub(pos.X);
                    break;
                case DragMode.TrimStart:
                    ApplyTrimStart(pos.X);
                    break;
                case DragMode.TrimEnd:
                    ApplyTrimEnd(pos.X);
                    break;
                case DragMode.CutStart:
                case DragMode.CutEnd:
                    ApplyCutResize(pos.X);
                    break;
                case DragMode.CutBody:
                    ApplyCutMove(pos.X);
                    break;
            }
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);

            if (!Equals(e.Pointer.Captured, this))
                return;

            e.Pointer.Capture(null);

            var mode = _dragMode;
            EndDrag();

            if (mode == DragMode.Scrub)
                ScrubCompleted?.Invoke(this, Position);
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);

            var mode = _dragMode;
            EndDrag();

            if (mode == DragMode.Scrub)
                ScrubCompleted?.Invoke(this, Position);
        }

        private void EndDrag()
        {
            _dragMode = DragMode.None;
            _dragBaseCuts = null;
            InvalidateVisual();
        }

        private void SelectCut(CutRegion cut)
        {
            if (_selectedCut != cut)
            {
                _selectedCut = cut;
                InvalidateVisual();
            }
        }

        private List<CutRegion> BuildBaseCuts(CutRegion dragged)
        {
            // Snapshot the cut list minus the dragged cut. Every subsequent move recomputes the
            // full list from this same base, so the document's merge logic can never feedback-loop
            // with the drag (we never re-read a merged cut mid-drag to derive the next value).
            var list = new List<CutRegion>(_document.Cuts);
            list.Remove(dragged);
            return list;
        }

        private void ApplyScrub(double x)
        {
            var t = TimeSpan.FromMilliseconds(XToMs(x));
            SetCurrentValue(PositionProperty, t);
            Scrubbed?.Invoke(this, t);
        }

        private void ApplyTrimStart(double x)
        {
            var doc = _document;
            if (doc == null)
                return;

            // Keep at least one minimum segment between the handles; ordering is the control's
            // job (the document clamps values but does not know the media duration).
            var max = Math.Max(0, EffectiveTrimEndMs - VideoEditDocument.MinSegmentMs);
            doc.TrimStartMs = Math.Clamp(XToMs(x), 0, max);
        }

        private void ApplyTrimEnd(double x)
        {
            var doc = _document;
            if (doc == null)
                return;

            var durationMs = DurationMs;
            var min = Math.Min(durationMs, TrimStartMs + VideoEditDocument.MinSegmentMs);
            var ms = Math.Clamp(XToMs(x), min, durationMs);

            // 0 is the document's "to the end" sentinel: dragging the handle back to the very
            // end stores 0 so the edit survives a future re-open with a slightly different
            // probed duration.
            doc.TrimEndMs = ms >= durationMs ? 0 : ms;
        }

        private void ApplyCutResize(double x)
        {
            var doc = _document;
            if (doc == null || _dragBaseCuts == null)
                return;

            var ms = XToMs(x);
            var candidate = new CutRegion(Math.Min(ms, _dragFixedEdgeMs), Math.Max(ms, _dragFixedEdgeMs));
            ApplyCutCandidate(candidate);
        }

        private void ApplyCutMove(double x)
        {
            var doc = _document;
            if (doc == null || _dragBaseCuts == null)
                return;

            var durationMs = DurationMs;
            var len = _dragCutLengthMs;
            var start = TimelineMath.MsAtX(x, durationMs, TrackX, TrackWidth) - _dragGrabOffsetMs;
            start = Math.Clamp(start, 0, Math.Max(0, durationMs - len));
            ApplyCutCandidate(new CutRegion(start, start + len));
        }

        private void ApplyCutCandidate(CutRegion candidate)
        {
            var doc = _document;
            var next = new List<CutRegion>(_dragBaseCuts) { candidate };

            _applyingEdit = true;
            try
            {
                doc.SetCuts(next);
            }
            finally
            {
                _applyingEdit = false;
            }

            // The document may have merged, clamped or dropped the candidate — re-resolve the
            // selection from what actually survived.
            _selectedCut = FindCutContaining(doc.Cuts, candidate.StartMs + candidate.DurationMs / 2);
            InvalidateVisual();
        }

        private static CutRegion FindCutContaining(IReadOnlyList<CutRegion> cuts, long ms)
        {
            for (int i = 0; i < cuts.Count; i++)
                if (ms >= cuts[i].StartMs && ms < cuts[i].EndMs)
                    return cuts[i];
            return null;
        }

        private TimelineHit HitTestAt(double x)
        {
            var durationMs = DurationMs;
            var doc = _document;

            double playheadX = TimeToX(Math.Clamp((long)Position.TotalMilliseconds, 0, durationMs));
            double trimStartX = double.NaN, trimEndX = double.NaN;
            List<(double StartX, double EndX)> cutsX = null;

            if (doc != null)
            {
                trimStartX = TimeToX(TrimStartMs);
                trimEndX = TimeToX(EffectiveTrimEndMs);
                var cuts = doc.Cuts;
                cutsX = new List<(double, double)>(cuts.Count);
                foreach (var cut in cuts)
                    cutsX.Add((TimeToX(Math.Clamp(cut.StartMs, 0, durationMs)), TimeToX(Math.Clamp(cut.EndMs, 0, durationMs))));
            }

            return TimelineMath.HitTest(x, playheadX, trimStartX, trimEndX, cutsX, TimelineMath.HitTolerance);
        }

        private void UpdateCursor(double x)
        {
            if (DurationMs <= 0 || TrackWidth <= 0)
            {
                Cursor = null;
                return;
            }

            switch (HitTestAt(x).Kind)
            {
                case TimelineHitKind.TrimStart:
                case TimelineHitKind.TrimEnd:
                case TimelineHitKind.CutStart:
                case TimelineHitKind.CutEnd:
                case TimelineHitKind.Playhead:
                    Cursor = _cursorResize ??= new Cursor(StandardCursorType.SizeWestEast);
                    break;
                case TimelineHitKind.CutBody:
                    Cursor = _cursorHand ??= new Cursor(StandardCursorType.Hand);
                    break;
                default:
                    Cursor = null;
                    break;
            }
        }

        // ------------------------------------------------------------------ rendering

        protected override Size MeasureOverride(Size availableSize)
        {
            // Width stretches with the parent; the height is the ruler plus a usable track bar.
            return new Size(0, 56);
        }

        public override void Render(DrawingContext context)
        {
            var palette = GetPalette();
            var track = TrackRect;
            var durationMs = DurationMs;

            context.DrawRectangle(palette.TrackBackground, null, track, 3, 3);

            if (durationMs <= 0 || track.Width <= 0 || track.Height <= 0)
                return;

            var doc = _document;
            var trimStartMs = TrimStartMs;
            var trimEndMs = EffectiveTrimEndMs;

            RenderRuler(context, palette, durationMs);

            // Kept regions in accent. With no document the whole track is "kept".
            if (doc != null)
            {
                foreach (var seg in doc.GetKeepSegments(durationMs))
                {
                    var rect = SegmentRect(track, seg.StartMs, seg.EndMs, durationMs);
                    if (rect.Width > 0)
                        context.DrawRectangle(palette.KeepFill, null, rect, 2, 2);
                }
            }
            else
            {
                context.DrawRectangle(palette.KeepFill, null, track.Deflate(new Thickness(0, 1)), 2, 2);
            }

            // Cut regions: darkened + diagonal hatch; the selected one gets an accent border.
            if (doc != null)
            {
                foreach (var cut in doc.Cuts)
                {
                    var rect = SegmentRect(track, cut.StartMs, cut.EndMs, durationMs);
                    if (rect.Width <= 0)
                        continue;

                    var selected = cut == _selectedCut;
                    context.DrawRectangle(palette.CutFill, null, rect, 2, 2);
                    RenderHatch(context, palette, rect);
                    RenderCutGrips(context, palette, rect, selected);

                    if (selected)
                        context.DrawRectangle(null, palette.SelectionPen, rect.Deflate(0.75), 2, 2);
                }
            }

            // Dimmed shading outside [TrimStart, TrimEnd].
            if (doc != null)
            {
                var trimStartX = TimeToX(trimStartMs);
                var trimEndX = TimeToX(trimEndMs);

                if (trimStartX > track.X)
                    context.DrawRectangle(palette.DimFill, null, new Rect(track.X, track.Y, trimStartX - track.X, track.Height));
                if (trimEndX < track.Right)
                    context.DrawRectangle(palette.DimFill, null, new Rect(trimEndX, track.Y, track.Right - trimEndX, track.Height));

                RenderTrimHandle(context, palette, trimStartX, track);
                RenderTrimHandle(context, palette, trimEndX, track);
            }

            RenderPlayhead(context, palette, durationMs);
        }

        private static Rect SegmentRect(Rect track, long startMs, long endMs, long durationMs)
        {
            var x1 = TimelineMath.TimeToX(Math.Clamp(startMs, 0, durationMs), durationMs, track.X, track.Width);
            var x2 = TimelineMath.TimeToX(Math.Clamp(endMs, 0, durationMs), durationMs, track.X, track.Width);
            return new Rect(x1, track.Y + 1, Math.Max(0, x2 - x1), Math.Max(0, track.Height - 2));
        }

        private void RenderRuler(DrawingContext context, Palette palette, long durationMs)
        {
            var trackX = TrackX;
            var trackWidth = TrackWidth;
            var step = TimelineMath.PickTickStepMs(durationMs, trackWidth);
            if (step <= 0)
                return;

            var minorStep = step / 5;
            var minorSpacingPx = trackWidth * minorStep / (double)durationMs;
            var drawMinor = minorStep > 0 && minorSpacingPx >= 5;

            var typeface = new Typeface(FontFamily.Default);

            for (long ms = 0; ms <= durationMs; ms += drawMinor ? minorStep : step)
            {
                var x = TimelineMath.TimeToX(ms, durationMs, trackX, trackWidth);
                var isMajor = !drawMinor || ms % step == 0;
                var tickHeight = isMajor ? 6.0 : 3.0;
                context.DrawLine(palette.TickPen, new Point(x, RulerHeight - tickHeight), new Point(x, RulerHeight));

                if (!isMajor)
                    continue;

                var text = new FormattedText(TimelineMath.FormatTick(ms), CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, typeface, 10, palette.LabelBrush);

                // Center the label on the tick, but keep it inside the control.
                var tx = Math.Clamp(x - text.Width / 2, 0, Math.Max(0, Bounds.Width - text.Width));
                context.DrawText(text, new Point(tx, RulerHeight - tickHeight - text.Height - 1));
            }
        }

        private void RenderHatch(DrawingContext context, Palette palette, Rect rect)
        {
            using (context.PushClip(rect))
            {
                const double spacing = 6;
                for (double x = rect.X - rect.Height; x < rect.Right; x += spacing)
                    context.DrawLine(palette.HatchPen, new Point(x, rect.Bottom), new Point(x + rect.Height, rect.Y));
            }
        }

        private void RenderCutGrips(DrawingContext context, Palette palette, Rect rect, bool selected)
        {
            // Edge grips only make sense when the cut is wide enough to show them apart.
            if (rect.Width < CutGripWidth * 2 + 6)
                return;

            var brush = selected ? palette.SelectionPen.Brush : palette.CutGripFill;
            var gripHeight = rect.Height * 0.55;
            var gy = rect.Y + (rect.Height - gripHeight) / 2;
            context.DrawRectangle(brush, null, new Rect(rect.X + 1.5, gy, CutGripWidth, gripHeight), 1, 1);
            context.DrawRectangle(brush, null, new Rect(rect.Right - CutGripWidth - 1.5, gy, CutGripWidth, gripHeight), 1, 1);
        }

        private void RenderTrimHandle(DrawingContext context, Palette palette, double x, Rect track)
        {
            var rect = new Rect(x - GripWidth / 2, track.Y - 1, GripWidth, track.Height + 2);
            context.DrawRectangle(palette.TrimHandleFill, palette.TrimHandlePen, rect, 2, 2);

            // Small center ridge so the grip reads as draggable.
            var ridgeTop = rect.Y + rect.Height * 0.3;
            var ridgeBottom = rect.Y + rect.Height * 0.7;
            context.DrawLine(palette.TrimRidgePen, new Point(x, ridgeTop), new Point(x, ridgeBottom));
        }

        private void RenderPlayhead(DrawingContext context, Palette palette, long durationMs)
        {
            var ms = Math.Clamp((long)Position.TotalMilliseconds, 0, durationMs);
            var x = TimeToX(ms);

            context.DrawLine(palette.PlayheadPen, new Point(x, 2), new Point(x, TrackBottom + 1));

            // Triangle grab in the ruler strip, pointing down at the track (mirrors ColorSlider's
            // triangle thumb idiom).
            const double half = 5;
            var geo = new StreamGeometry();
            using (var gc = geo.Open())
            {
                gc.BeginFigure(new Point(x - half, 0), true);
                gc.LineTo(new Point(x + half, 0));
                gc.LineTo(new Point(x, 7));
                gc.EndFigure(true);
            }

            context.DrawGeometry(palette.PlayheadPen.Brush, palette.PlayheadOutlinePen, geo);
        }

        // ------------------------------------------------------------------ theme palette

        private sealed class Palette
        {
            public IBrush TrackBackground;
            public IBrush KeepFill;
            public IBrush CutFill;
            public IBrush DimFill;
            public IBrush CutGripFill;
            public IBrush LabelBrush;
            public IBrush TrimHandleFill;
            public Pen TrimHandlePen;
            public Pen TrimRidgePen;
            public Pen HatchPen;
            public Pen TickPen;
            public Pen SelectionPen;
            public Pen PlayheadPen;
            public Pen PlayheadOutlinePen;
        }

        private Palette GetPalette()
        {
            var variant = ActualThemeVariant;
            if (_palette != null && Equals(_paletteVariant, variant))
                return _palette;

            var dark = AppStyles.IsDarkTheme;
            var accent = AppStyles.AccentColor;

            var trackBg = GetThemeColor("SemiColorFill1", dark ? Color.FromRgb(45, 45, 48) : Color.FromRgb(222, 224, 227));
            var text2 = GetThemeColor("SemiColorText2", dark ? Color.FromRgb(200, 200, 200) : Color.FromRgb(70, 72, 76));
            var text3 = GetThemeColor("SemiColorText3", dark ? Color.FromRgb(140, 140, 140) : Color.FromRgb(130, 133, 138));

            var playheadColor = dark ? Color.FromRgb(240, 82, 82) : Color.FromRgb(212, 48, 48);

            _palette = new Palette
            {
                TrackBackground = new SolidColorBrush(trackBg),
                KeepFill = new SolidColorBrush(accent, dark ? 0.85 : 0.9),
                CutFill = new SolidColorBrush(dark ? Color.FromRgb(28, 28, 30) : Color.FromRgb(120, 124, 130)),
                DimFill = new SolidColorBrush(dark ? Colors.Black : Colors.White, 0.55),
                CutGripFill = new SolidColorBrush(dark ? Colors.White : Colors.Black, 0.45),
                LabelBrush = new SolidColorBrush(text3),
                TrimHandleFill = new SolidColorBrush(dark ? Color.FromRgb(235, 235, 235) : Color.FromRgb(250, 250, 250)),
                TrimHandlePen = new Pen(new SolidColorBrush(dark ? Color.FromRgb(20, 20, 20) : Color.FromRgb(120, 124, 130), 0.8), 1),
                TrimRidgePen = new Pen(new SolidColorBrush(dark ? Color.FromRgb(90, 90, 90) : Color.FromRgb(150, 153, 158)), 1.5),
                HatchPen = new Pen(new SolidColorBrush(dark ? Colors.White : Colors.Black, 0.18), 1),
                TickPen = new Pen(new SolidColorBrush(text2, 0.6), 1),
                SelectionPen = new Pen(new SolidColorBrush(accent), 2),
                PlayheadPen = new Pen(new SolidColorBrush(playheadColor), 1.5),
                PlayheadOutlinePen = new Pen(new SolidColorBrush(dark ? Colors.Black : Colors.White, 0.6), 1),
            };
            _paletteVariant = variant;
            return _palette;
        }

        private static Color GetThemeColor(string key, Color fallback)
        {
            var app = Application.Current;
            if (app != null && app.TryGetResource(key, app.ActualThemeVariant, out var value))
            {
                if (value is ISolidColorBrush brush)
                    return brush.Color;
                if (value is Color color)
                    return color;
            }

            return fallback;
        }
    }

    /// <summary>What part of the timeline a pointer x-coordinate lands on.</summary>
    internal enum TimelineHitKind
    {
        Track,      // empty track (or ruler): scrub jump + drag
        Playhead,
        TrimStart,
        TrimEnd,
        CutStart,
        CutEnd,
        CutBody,
    }

    internal readonly record struct TimelineHit(TimelineHitKind Kind, int CutIndex);

    /// <summary>
    /// Pure pixel&lt;-&gt;time mapping and hit-test math for <see cref="TimelineControl"/>, kept free
    /// of Avalonia types so Clowd.Video.Tests can unit-test it directly (via InternalsVisibleTo).
    /// </summary>
    internal static class TimelineMath
    {
        /// <summary>Pointer slop, in device-independent pixels, for grabbing grips and edges.</summary>
        public const double HitTolerance = 6.0;

        /// <summary>Maps a source time to an x-coordinate inside the track. Duration&lt;=0 or a
        /// degenerate track collapses to the track origin rather than dividing by zero.</summary>
        public static double TimeToX(long ms, long durationMs, double trackX, double trackWidth)
        {
            if (durationMs <= 0 || trackWidth <= 0)
                return trackX;

            return trackX + (double)ms / durationMs * trackWidth;
        }

        /// <summary>Maps an x-coordinate to source milliseconds, clamped to [0, duration].</summary>
        public static long XToMs(double x, long durationMs, double trackX, double trackWidth)
        {
            return Math.Clamp(MsAtX(x, durationMs, trackX, trackWidth), 0, Math.Max(0, durationMs));
        }

        /// <summary>Unclamped variant of <see cref="XToMs"/> — used for drag deltas, where clamping
        /// the pointer first would distort the grab offset at the track edges.</summary>
        public static long MsAtX(double x, long durationMs, double trackX, double trackWidth)
        {
            if (durationMs <= 0 || trackWidth <= 0)
                return 0;

            return (long)Math.Round((x - trackX) / trackWidth * durationMs);
        }

        /// <summary>Resolves the document's TrimEndMs sentinel (0 = to-end) against the media
        /// duration, clamping a stale value that exceeds the media.</summary>
        public static long EffectiveTrimEnd(long trimEndMs, long durationMs)
        {
            if (trimEndMs <= 0)
                return durationMs;

            return Math.Clamp(trimEndMs, 0, durationMs);
        }

        /// <summary>
        /// Picks the ruler tick step so labels do not crowd: the smallest of 1/5/10/30/60s whose
        /// pixel spacing is at least <paramref name="minSpacingPx"/>; for very long media the step
        /// keeps doubling from 60s until it fits. Returns 0 when there is nothing to draw.
        /// </summary>
        public static long PickTickStepMs(long durationMs, double trackWidth, double minSpacingPx = 60)
        {
            if (durationMs <= 0 || trackWidth <= 0)
                return 0;

            foreach (var step in new long[] { 1_000, 5_000, 10_000, 30_000, 60_000 })
            {
                if (trackWidth * step / durationMs >= minSpacingPx)
                    return step;
            }

            var big = 60_000L;
            while (trackWidth * big / durationMs < minSpacingPx && big < durationMs)
                big *= 2;
            return big;
        }

        public static string FormatTick(long ms)
        {
            var t = TimeSpan.FromMilliseconds(ms);
            return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        }

        /// <summary>
        /// Hit-tests an x-coordinate against the interactive parts of the timeline, in priority
        /// order: playhead &gt; trim handles &gt; cut edges &gt; cut body &gt; empty track. All
        /// coordinates are x-positions previously produced by <see cref="TimeToX"/>; NaN inputs
        /// (no document attached) never match. Ties inside the tolerance go to the nearest edge.
        /// </summary>
        public static TimelineHit HitTest(double x, double playheadX, double trimStartX, double trimEndX,
            IReadOnlyList<(double StartX, double EndX)> cuts, double tolerance)
        {
            if (Math.Abs(x - playheadX) <= tolerance)
                return new TimelineHit(TimelineHitKind.Playhead, -1);

            var dStart = Math.Abs(x - trimStartX);
            var dEnd = Math.Abs(x - trimEndX);
            if (dStart <= tolerance || dEnd <= tolerance)
            {
                // NaN comparisons are false, so a NaN handle can never win here.
                if (dStart <= tolerance && (!(dEnd <= tolerance) || dStart <= dEnd))
                    return new TimelineHit(TimelineHitKind.TrimStart, -1);
                return new TimelineHit(TimelineHitKind.TrimEnd, -1);
            }

            if (cuts != null)
            {
                var bestDist = double.PositiveInfinity;
                var best = new TimelineHit(TimelineHitKind.Track, -1);

                for (int i = 0; i < cuts.Count; i++)
                {
                    var ds = Math.Abs(x - cuts[i].StartX);
                    var de = Math.Abs(x - cuts[i].EndX);
                    if (ds <= tolerance && ds < bestDist)
                    {
                        bestDist = ds;
                        best = new TimelineHit(TimelineHitKind.CutStart, i);
                    }

                    if (de <= tolerance && de < bestDist)
                    {
                        bestDist = de;
                        best = new TimelineHit(TimelineHitKind.CutEnd, i);
                    }
                }

                if (best.Kind != TimelineHitKind.Track)
                    return best;

                for (int i = 0; i < cuts.Count; i++)
                {
                    if (x > cuts[i].StartX && x < cuts[i].EndX)
                        return new TimelineHit(TimelineHitKind.CutBody, i);
                }
            }

            return new TimelineHit(TimelineHitKind.Track, -1);
        }
    }
}

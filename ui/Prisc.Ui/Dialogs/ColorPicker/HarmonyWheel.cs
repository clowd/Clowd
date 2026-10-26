using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;

namespace Prisc.UI.Dialogs.ColorPicker
{
    /// <summary>
    /// The Palette tab's wheel (brief §7.2): one marker per palette color, each joined to the centre
    /// by a thin white line, the base's larger than the rest, and the highlighted one (the bar being
    /// hovered or edited) wearing an accent halo. Dragging a marker reports where it was taken
    /// (<see cref="MemberDragged"/>); the tab re-derives the whole palette from that, so the
    /// preset's relationship holds and every marker follows.
    /// </summary>
    /// <remarks>
    /// The wheel's own <see cref="ColorWheel.CurrentColor"/> is not used. Positions come from hue
    /// and HSV saturation alone, so a palette dimmed by the brightness slider keeps its shape. Hues
    /// are laid out round the artistic wheel the harmonies are defined on
    /// (<see cref="ColorHarmony.ToArtistic"/>), wheel and markers alike, so a preset's angles read
    /// true: a triad is an equilateral triangle, a complement sits straight across.
    /// </remarks>
    public class HarmonyWheel : ColorWheel
    {
        /// <summary>The base marker, larger than the rest (the mockup's 28px).</summary>
        public const double BaseMarkerDiameter = 28;

        public static readonly StyledProperty<IBrush> HaloBrushProperty =
            AvaloniaProperty.Register<HarmonyWheel, IBrush>(nameof(HaloBrush));

        /// <summary>The highlighted marker's halo: the theme's strong accent.</summary>
        public IBrush HaloBrush
        {
            get => GetValue(HaloBrushProperty);
            set => SetValue(HaloBrushProperty, value);
        }

        // the spokes: a white line over a soft dark one, so a spoke still reads where it crosses
        // the wheel's white centre (the mockup's 1.5px line with a 2px dark glow)
        private static readonly IPen SpokePen = new Pen(new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF)), 1.5);
        private static readonly IPen SpokeShadowPen = new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0x00, 0x00, 0x00)), 3.5);

        private static readonly Cursor GrabCursor = new(StandardCursorType.Hand);

        private IReadOnlyList<HsvColor> _palette;
        private int _baseIndex = -1;
        private int _highlightIndex = -1;

        // the marker being dragged, and where on it the drag took hold (so taking a marker by its
        // edge does not first jump its centre to the pointer)
        private int _dragIndex = -1;
        private Vector _grabOffset;

        // the markers' places for the current palette and size, worked out on first use
        private Point[] _layout;
        private Size _layoutSize;

        static HarmonyWheel()
        {
            AffectsRender<HarmonyWheel>(HaloBrushProperty);
        }

        /// <summary>The palette's colors, one marker each.</summary>
        public IReadOnlyList<HsvColor> Palette
        {
            get => _palette;
            set
            {
                _palette = value;
                _layout = null;
                InvalidateVisual();
            }
        }

        /// <summary>The member drawn with the larger base marker.</summary>
        public int BaseIndex
        {
            get => _baseIndex;
            set
            {
                _baseIndex = value;
                _layout = null;
                InvalidateVisual();
            }
        }

        /// <summary>The member whose marker wears the halo, or -1.</summary>
        public int HighlightIndex
        {
            get => _highlightIndex;
            set
            {
                if (_highlightIndex == value)
                    return;

                _highlightIndex = value;
                InvalidateVisual();
            }
        }

        /// <summary>A press took hold of a marker (or, off every marker, of the highlighted one).</summary>
        public event EventHandler<int> DragStarted;

        /// <summary>The dragged marker's new hue and saturation, live through the drag. Value is
        /// left as the member had it; the wheel does not show brightness.</summary>
        public event EventHandler<(int Index, double Hue, double Saturation)> MemberDragged;

        protected override double AngleOfHue(double hue) => ColorHarmony.ToArtistic(hue);

        protected override double HueAtAngle(double angle) => ColorHarmony.FromArtistic(angle);

        private double DiameterOf(int index) => index == _baseIndex ? BaseMarkerDiameter : MarkerDiameter;

        /// <summary>Where member <paramref name="index"/>'s own color sits on the wheel.</summary>
        private Point ColorCenter(int index) =>
            PointFor(_palette[index].Hue, _palette[index].Saturation);

        /// <summary>Where member <paramref name="index"/>'s marker is drawn: at its color, unless
        /// that would stack it on another (see <see cref="LayoutMarkers"/>).</summary>
        private Point MarkerCenter(int index)
        {
            if (_layout == null || _layoutSize != Bounds.Size)
            {
                _layout = LayoutMarkers();
                _layoutSize = Bounds.Size;
            }

            return _layout[index];
        }

        /// <summary>
        /// The markers' places. Members that share a hue sit on one radius, and several presets put
        /// two or more at the very same spot there (Shades differ only in brightness, which the
        /// wheel does not show; Monochromatic and Complementary repeat the base's saturation). Each
        /// such radius is laid out the way the preset glyphs are: in order of saturation, then
        /// brightness, out from the centre (so Shades darken and Monochromatic desaturates toward
        /// it), with neighbours pushed apart until they no longer overlap. The base stays at its
        /// color wherever the rim and the centre leave room, and when a radius is too short for
        /// all of them they share what there is.
        /// </summary>
        private Point[] LayoutMarkers()
        {
            var count = _palette.Count;
            var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
            var centers = new Point[count];
            var radius = new double[count];
            for (var i = 0; i < count; i++)
            {
                centers[i] = ColorCenter(i);
                var v = centers[i] - c;
                radius[i] = Math.Sqrt(v.X * v.X + v.Y * v.Y);
            }

            var placed = new bool[count];
            for (var first = 0; first < count; first++)
            {
                if (placed[first])
                    continue;

                var group = Enumerable.Range(first, count - first)
                    .Where(i => !placed[i] && HueDistance(_palette[i].Hue, _palette[first].Hue) < 0.01)
                    .OrderBy(i => _palette[i].Saturation).ThenBy(i => _palette[i].Value).ThenBy(i => i)
                    .ToArray();
                foreach (var i in group)
                    placed[i] = true;
                if (group.Length < 2)
                    continue;

                double Gap(int a, int b) => (DiameterOf(a) + DiameterOf(b)) / 2;

                // out from the anchor (the base, else the outermost), each at its own radius or
                // just clear of the one before, whichever is further from the anchor
                var anchor = Array.IndexOf(group, _baseIndex);
                if (anchor < 0)
                    anchor = group.Length - 1;

                var p = new double[group.Length];
                p[anchor] = radius[group[anchor]];
                for (var k = anchor + 1; k < group.Length; k++)
                    p[k] = Math.Max(radius[group[k]], p[k - 1] + Gap(group[k - 1], group[k]));
                for (var k = anchor - 1; k >= 0; k--)
                    p[k] = Math.Min(radius[group[k]], p[k + 1] - Gap(group[k], group[k + 1]));

                // then back inside the wheel: the outermost no further out than its rim allows,
                // the innermost not past the centre, and squeezed evenly if both cannot hold
                var last = group.Length - 1;
                var outer = PointFor(_palette[group[last]].Hue, 1) - c;
                var hi = Math.Sqrt(outer.X * outer.X + outer.Y * outer.Y);
                const double lo = 0;
                var shift = Math.Min(0, hi - p[last]);
                shift = Math.Max(shift, lo - p[0]);
                for (var k = 0; k < group.Length; k++)
                    p[k] += shift;
                if (p[last] > hi)
                {
                    var span = p[last] - p[0];
                    for (var k = 0; k < group.Length; k++)
                        p[k] = lo + (p[k] - p[0]) / span * (hi - lo);
                }

                var angle = AngleOfHue(_palette[first].Hue) * Math.PI / 180;
                for (var k = 0; k < group.Length; k++)
                    centers[group[k]] = new Point(c.X + Math.Cos(angle) * p[k], c.Y - Math.Sin(angle) * p[k]);
            }

            return centers;
        }

        private static double HueDistance(double a, double b)
        {
            var d = Math.Abs(a - b) % 360;
            return Math.Min(d, 360 - d);
        }

        /// <summary>Back to front: the plain markers, then the base, then the highlighted one, so
        /// the marker that matters is never under another.</summary>
        private IEnumerable<int> DrawOrder()
        {
            var count = _palette?.Count ?? 0;
            for (var i = 0; i < count; i++)
            {
                if (i != _baseIndex && i != _highlightIndex)
                    yield return i;
            }

            if (_baseIndex >= 0 && _baseIndex < count && _baseIndex != _highlightIndex)
                yield return _baseIndex;
            if (_highlightIndex >= 0 && _highlightIndex < count)
                yield return _highlightIndex;
        }

        protected override void RenderMarkers(DrawingContext context)
        {
            if (_palette == null)
                return;

            var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
            foreach (var i in DrawOrder())
                context.DrawLine(SpokeShadowPen, center, MarkerCenter(i));
            foreach (var i in DrawOrder())
                context.DrawLine(SpokePen, center, MarkerCenter(i));

            foreach (var i in DrawOrder())
                DrawMarker(context, MarkerCenter(i), DiameterOf(i), _palette[i].ToColor(), i == _highlightIndex ? HaloBrush : null);
        }

        /// <summary>The topmost marker under <paramref name="point"/>, or -1.</summary>
        private int HitTestMarker(Point point)
        {
            if (_palette == null)
                return -1;

            var hit = -1;
            foreach (var i in DrawOrder())
            {
                // a couple of pixels of slack, since the plain markers are small targets
                var reach = DiameterOf(i) / 2 + 2;
                var d = point - MarkerCenter(i);
                if (d.X * d.X + d.Y * d.Y <= reach * reach)
                    hit = i;
            }

            return hit;
        }

        /// <summary>
        /// A press takes the marker under it, or — off every marker — the highlighted one, which
        /// jumps to the press (so a click on the wheel moves the color being edited). Each move
        /// then reports the marker's new hue and saturation; anywhere from the rim outwards is full
        /// saturation (<see cref="ColorWheel.PointFor"/>).
        /// </summary>
        protected override void OnPick(Point point, bool isStart)
        {
            if (_palette == null)
                return;

            if (isStart)
            {
                _dragIndex = HitTestMarker(point);
                // the drag moves the member's own color by however far the pointer goes, which
                // for a marker laid out off its color (LayoutMarkers) is not where it is drawn
                _grabOffset = _dragIndex >= 0 ? ColorCenter(_dragIndex) - point : default;
                if (_dragIndex < 0)
                    _dragIndex = _highlightIndex >= 0 && _highlightIndex < _palette.Count ? _highlightIndex : _baseIndex;
                if (_dragIndex < 0)
                    return;

                DragStarted?.Invoke(this, _dragIndex);
            }

            if (_dragIndex < 0 || _dragIndex >= _palette.Count)
                return;

            var target = point + _grabOffset;
            var (hue, saturation) = HueSaturationAt(target);

            var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
            var rim = PointFor(hue, 1) - c;
            var reach = target - c;
            if (reach.X * reach.X + reach.Y * reach.Y >= rim.X * rim.X + rim.Y * rim.Y - 0.01)
                saturation = 1;

            MemberDragged?.Invoke(this, (_dragIndex, hue, saturation));
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (!Equals(e.Pointer.Captured, this))
                Cursor = HitTestMarker(e.GetPosition(this)) >= 0 ? GrabCursor : null;
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            _dragIndex = -1;
        }
    }
}

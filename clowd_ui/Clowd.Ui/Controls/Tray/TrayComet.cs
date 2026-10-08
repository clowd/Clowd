using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace Clowd.UI.Controls.Tray
{
    /// <summary>
    /// The geometry and timing of the accent comet that circles a floating tray when it first appears.
    /// Descended from clowd_capture's hint comet (src/ui/components/hints/trail.rs) — the same
    /// rounded-rect parameterisation and tail curve — but run once rather than forever. The comet never
    /// dims or grows: it is always at full speed, strength and length, and only the part of it on its one
    /// lap is drawn — it flies out of the start point (where the top-left arc meets the top edge), round
    /// the tray, and back through that point, gone.
    /// <para>
    /// Speed and length are in logical px, not fractions of a lap, so the comet looks and moves the same
    /// on every strip: a long row takes longer to go round than a short column, rather than being raced
    /// round in a fixed time. Everything here is a pure function of the tray rect and the elapsed time,
    /// so the control below keeps no state between frames beyond its start time.
    /// </para>
    /// </summary>
    public static class TrayCometMath
    {
        /// <summary>How fast the head travels along the rim, in logical px per second.</summary>
        public const double Speed = 650;

        /// <summary>The comet's length along the rim, in logical px.</summary>
        public const double TrailPx = 220;

        /// <summary>The most of the rim the comet may cover at once: on a small strip the full
        /// <see cref="TrailPx"/> would chase its own head.</summary>
        public const double MaxTrailFraction = 0.45;

        /// <summary>Samples around the perimeter.</summary>
        public const int Samples = 256;

        /// <summary>The soft edge the comet passes through at the start point, coming and going, in logical
        /// px along the rim: short enough to read as the comet appearing and vanishing there, long enough
        /// not to read as a hard end cap.</summary>
        public const double GateEdge = 24;

        /// <summary>
        /// The band across the rim, as (signed offset from the tray's edge in logical px, positive outward;
        /// opacity) pairs. A solid core three rings wide — the comet is a line of the accent, not a haze of
        /// it, which over the graphite tray read as muddy — with a short, faint halo either side. The
        /// outermost stays inside the compact shadow's 7 px top reserve, so the window edge never clips it.
        /// </summary>
        public static readonly (double Offset, double Opacity)[] Band =
        {
            (-4.5, 0), (-2.5, 0.28), (-1.25, 1), (0, 1), (1.25, 1), (2.5, 0.28), (4.5, 0),
        };

        /// <summary>The tray's 1 px highlight ring: the inner half of the band starts inside it, as the hint
        /// comet's does inside a chip's border.</summary>
        public const double BorderWidth = 1;

        /// <summary>How thin the band gets at the very end of the tail, as a fraction of its full width: the
        /// tail tapers rather than going see-through, so it stays the accent all the way down.</summary>
        public const double TailWidth = 0.10;

        /// <summary>The rounded rect's perimeter, in the rect's own units.</summary>
        public static double Perimeter(Rect rect, double radius)
        {
            var r = Math.Clamp(radius, 0, Math.Min(rect.Width, rect.Height) / 2);
            return 2 * (rect.Width - 2 * r) + 2 * (rect.Height - 2 * r) + 2 * Math.PI * r;
        }

        /// <summary>The comet's length on a rim <paramref name="perimeter"/> long, as a fraction of a lap.</summary>
        public static double Trail(double perimeter)
            => perimeter > 0 ? Math.Min(TrailPx / perimeter, MaxTrailFraction) : 0;

        /// <summary>
        /// The point at normalised arc length <paramref name="s"/> around the rounded rect, and the outward
        /// unit normal there. <paramref name="s"/> runs clockwise on screen from where the top-left arc
        /// meets the top edge.
        /// </summary>
        public static (Point Point, Vector Normal) PerimeterPoint(Rect rect, double radius, double s)
        {
            var r = Math.Clamp(radius, 0, Math.Min(rect.Width, rect.Height) / 2);
            // a zero radius still has to divide by something; the arcs are zero-length then, so the
            // value never reaches a result.
            var rd = Math.Max(r, 1e-3);
            var sw = rect.Width - 2 * r;
            var sh = rect.Height - 2 * r;
            var arc = Math.PI / 2 * r;
            var d = Wrap(s) * (2 * sw + 2 * sh + 4 * arc);

            (Point, Vector) OnArc(Point c, double a0, double along)
            {
                var a = a0 + along / rd;
                var n = new Vector(Math.Cos(a), Math.Sin(a));
                return (c + n * r, n);
            }

            if (d < sw)
                return (new Point(rect.Left + r + d, rect.Top), new Vector(0, -1));
            d -= sw;
            if (d < arc)
                return OnArc(new Point(rect.Right - r, rect.Top + r), -Math.PI / 2, d);
            d -= arc;
            if (d < sh)
                return (new Point(rect.Right, rect.Top + r + d), new Vector(1, 0));
            d -= sh;
            if (d < arc)
                return OnArc(new Point(rect.Right - r, rect.Bottom - r), 0, d);
            d -= arc;
            if (d < sw)
                return (new Point(rect.Right - r - d, rect.Bottom), new Vector(0, 1));
            d -= sw;
            if (d < arc)
                return OnArc(new Point(rect.Left + r, rect.Bottom - r), Math.PI / 2, d);
            d -= arc;
            if (d < sh)
                return (new Point(rect.Left, rect.Bottom - r - d), new Vector(-1, 0));
            d -= sh;
            return OnArc(new Point(rect.Left + r, rect.Top + r), Math.PI, d);
        }

        /// <summary>
        /// The comet's brightness at arc length <paramref name="s"/> with its head at
        /// <paramref name="head"/> and a tail <paramref name="length"/> long: full at the head, fading
        /// behind it, dark elsewhere.
        /// </summary>
        public static double Intensity(double s, double head, double length)
        {
            if (length <= 0)
                return 0;
            var behind = Wrap(head - s);
            return Math.Pow(Math.Max(1 - behind / length, 0), 1.5);
        }

        /// <summary>
        /// How far the comet's head has travelled <paramref name="elapsed"/> into the run on a rim
        /// <paramref name="perimeter"/> long, in laps from the start point, so 1.2 is a fifth of the way
        /// round again. Null once the whole tail is back through the start point.
        /// </summary>
        public static double? Frame(TimeSpan elapsed, double perimeter)
        {
            if (perimeter <= 0)
                return null;
            var travelled = Math.Max(elapsed.TotalSeconds * Speed / perimeter, 0);
            return travelled < 1 + Trail(perimeter) ? travelled : null;
        }

        /// <summary>
        /// The comet's brightness at arc length <paramref name="s"/>: <see cref="Intensity"/> over a tail
        /// <paramref name="trail"/> long, drawn only where that part of the comet is on its one lap — the
        /// tail still "behind" the start point at the beginning, and the head already through it again at
        /// the end, are cut off over a soft edge <paramref name="edge"/> long. All in fractions of a lap.
        /// </summary>
        public static double Brightness(double s, double travelled, double trail, double edge)
        {
            var lit = Intensity(s, travelled, trail);
            if (lit <= 0)
                return 0;

            // where this sample sits on the run, unwrapped: the head's own distance less how far behind it is
            var along = travelled - Wrap(travelled - s);
            if (edge <= 0)
                return along >= 0 && along < 1 ? lit : 0;
            return lit * Math.Clamp(along / edge, 0, 1) * Math.Clamp((1 - along) / edge, 0, 1);
        }

        /// <summary>
        /// How a sample <paramref name="brightness"/> (0..1) along the comet is drawn: the band's width as a
        /// fraction of full, its opacity, and how far its colour has moved from the body towards the hot
        /// head. Opacity saturates early so most of the tail is solid accent and only the last of it
        /// thins out; the head tint is confined to the very front.
        /// </summary>
        public static (double Width, double Opacity, double Heat) Look(double brightness)
        {
            var t = Math.Clamp(brightness, 0, 1);
            return (TailWidth + (1 - TailWidth) * t, Math.Min(1, 2.5 * t), t * t * t * t);
        }

        private static double Wrap(double v) => v - Math.Floor(v);
    }

    /// <summary>
    /// The entrance comet: an overlay laid over the whole tray window that draws <see cref="TrayCometMath"/>
    /// around the target's bounds for one run, then hides itself. It contributes nothing to layout (a
    /// size-to-content window must still size to the tray alone) and nothing to hit-testing (every
    /// pixel of it sits over a tile or the shadow reserve). Frames come from the top level's animation
    /// clock, so nothing ticks once the run is over or the window is gone.
    /// </summary>
    public sealed class TrayComet : Control
    {
        private readonly Control _target;
        private TimeSpan? _start;
        private TimeSpan _elapsed;
        private bool _running;

        public TrayComet(Control target)
        {
            _target = target ?? throw new ArgumentNullException(nameof(target));
            IsHitTestVisible = false;
            IsVisible = false;
        }

        /// <summary>The accent the comet's colours are derived from (<see cref="AccentColors.CometColors"/>):
        /// the user's pick, not the contrast-darkened fill.</summary>
        public Color Accent { get; set; } = AppStyles.CapturePickedAccentColor;

        /// <summary>
        /// Starts a run from the beginning, or does nothing when the user has turned Windows' animation
        /// effects off. A run already under way restarts.
        /// </summary>
        public void Start()
        {
            if (!AnimationsEnabled())
                return;

            _start = null;
            _elapsed = TimeSpan.Zero;
            IsVisible = true;
            if (!_running)
            {
                _running = true;
                RequestFrame();
            }
        }

        protected override Size MeasureOverride(Size availableSize) => default;

        public override void Render(DrawingContext context)
        {
            var tray = _target.Bounds;
            var perimeter = TrayCometMath.Perimeter(tray, TrayTokens.Radius);
            var travelled = TrayCometMath.Frame(_elapsed, perimeter);
            if (travelled == null)
                return;

            var (body, head) = AccentColors.CometColors(Accent);
            context.Custom(new CometDrawOperation(new Rect(Bounds.Size), tray, body, head,
                travelled.Value, TrayCometMath.Trail(perimeter), TrayCometMath.GateEdge / perimeter));
        }

        private void RequestFrame()
        {
            var top = TopLevel.GetTopLevel(this);
            if (top == null)
            {
                Stop();
                return;
            }

            top.RequestAnimationFrame(OnFrame);
        }

        private void OnFrame(TimeSpan now)
        {
            _start ??= now;
            _elapsed = now - _start.Value;

            // the run's length follows the tray's size (the head moves at a fixed speed), so the end is
            // asked of the same function the frame is drawn from
            if (TrayCometMath.Frame(_elapsed, TrayCometMath.Perimeter(_target.Bounds, TrayTokens.Radius)) == null)
            {
                Stop();
                return;
            }

            InvalidateVisual();
            RequestFrame();
        }

        private void Stop()
        {
            _running = false;
            IsVisible = false;
        }

        /// <summary>Windows' "Animation effects" setting. Every other platform animates.</summary>
        private static bool AnimationsEnabled()
        {
            if (!OperatingSystem.IsWindows())
                return true;
            return !SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, out var enabled, 0) || enabled;
        }

        private const uint SPI_GETCLIENTAREAANIMATION = 0x1042;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, [MarshalAs(UnmanagedType.Bool)] out bool pvParam, uint fWinIni);

        /// <summary>
        /// The comet as one triangle mesh around the tray: a strip of rings across the band
        /// (<see cref="TrayCometMath.Band"/>) sampled around the outline, each vertex carrying the band's
        /// opacity there, the comet's <see cref="TrayCometMath.Look"/> at that sample, and its colour
        /// between body and head. Avalonia has no per-vertex colour, so it is a Skia draw; off Skia there
        /// is nothing to draw.
        /// </summary>
        private sealed class CometDrawOperation : ICustomDrawOperation
        {
            private readonly Rect _tray;
            private readonly Color _body, _head;
            private readonly double _travelled, _trail, _edge;

            public CometDrawOperation(Rect bounds, Rect tray, Color body, Color head, double travelled, double trail, double edge)
            {
                Bounds = bounds;
                _tray = tray;
                _body = body;
                _head = head;
                _travelled = travelled;
                _trail = trail;
                _edge = edge;
            }

            public Rect Bounds { get; }

            public bool HitTest(Point p) => false;

            public bool Equals(ICustomDrawOperation other) => false;

            public void Dispose()
            { }

            public void Render(ImmediateDrawingContext context)
            {
                var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
                if (feature == null)
                    return;

                using var lease = feature.Lease();
                var canvas = lease.SkCanvas;
                if (canvas == null)
                    return;

                var band = TrayCometMath.Band;
                var r = band.Length;
                var n = TrayCometMath.Samples;
                var positions = new SKPoint[n * r];
                var colors = new SKColor[positions.Length];
                for (var i = 0; i < n; i++)
                {
                    var s = (double)i / n;
                    var (pt, normal) = TrayCometMath.PerimeterPoint(_tray, TrayTokens.Radius, s);
                    var (width, opacity, heat) = TrayCometMath.Look(TrayCometMath.Brightness(s, _travelled, _trail, _edge));
                    var red = Lerp(_body.R, _head.R, heat);
                    var green = Lerp(_body.G, _head.G, heat);
                    var blue = Lerp(_body.B, _head.B, heat);
                    for (var k = 0; k < r; k++)
                    {
                        var (d, bandOpacity) = band[k];
                        var offset = d * width;
                        if (offset < 0)
                            offset -= TrayCometMath.BorderWidth;
                        var p = pt + normal * offset;
                        positions[i * r + k] = new SKPoint((float)p.X, (float)p.Y);
                        colors[i * r + k] = new SKColor(red, green, blue, (byte)Math.Round(255 * Math.Clamp(opacity * bandOpacity, 0, 1)));
                    }
                }

                var indices = new ushort[n * (r - 1) * 6];
                var x = 0;
                for (var i = 0; i < n; i++)
                {
                    var j = (i + 1) % n;
                    for (var k = 0; k < r - 1; k++)
                    {
                        ushort a = (ushort)(i * r + k), b = (ushort)(i * r + k + 1), c = (ushort)(j * r + k), d = (ushort)(j * r + k + 1);
                        indices[x++] = a; indices[x++] = b; indices[x++] = c;
                        indices[x++] = b; indices[x++] = d; indices[x++] = c;
                    }
                }

                using var vertices = SKVertices.CreateCopy(SKVertexMode.Triangles, positions, null, colors, indices);
                using var paint = new SKPaint { IsAntialias = true };
                canvas.DrawVertices(vertices, SKBlendMode.Dst, paint);
            }

            private static byte Lerp(byte from, byte to, double t) => (byte)Math.Round(from + (to - from) * t);
        }
    }
}

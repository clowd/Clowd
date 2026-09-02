using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Clowd.Drawing;
using Clowd.PlatformUtil;
using Clowd.UI.Helpers;

namespace Clowd.UI
{
    /// <summary>
    /// The hit-testable overlay that owns a share-region resize gesture: the accent frame around
    /// the region, a translucent wash over it, and eight standard grab handles.
    /// <para>Resize mode is a SWAP, not a stack (spec addendum 8.1): <see cref="BorderWindow"/> is
    /// hidden for as long as this window is up, and comes back on the region the helper actually
    /// applied. Nothing else is left to mark the mirrored rectangle while that is true, which is
    /// why this window draws the border's own frame — same colours, same widths, same outward
    /// offset — rather than only the wash and the handles.</para>
    /// <para>This window is focused and activated (addendum 8.2), unlike every other surface in a
    /// share session: on macOS a window that never activates does not get its cursor rects
    /// honoured, so the per-handle resize cursors — the whole affordance — never appear on hover.
    /// Taking focus also buys Esc, which is wired in <see cref="OnKeyDown"/>.</para>
    /// <para>A separate window rather than a mode on the border, for mechanical reasons:
    /// <c>WindowNativeExtensions.AddExStyles</c> registers an unretained lambda whose
    /// WS_EX_TRANSPARENT Avalonia can re-apply at any time, WS_EX_LAYERED must stay live or the
    /// border stops repainting entirely, and BorderWindow is shared with the recording and
    /// scrolling-capture pages, which can never use any of this.</para>
    /// <para>Everything the drag computes is CAPTURE space integer arithmetic derived from
    /// <c>PointToScreen</c>, which is scaling-independent by construction — so a DPI change or a
    /// monitor crossing mid-drag cannot corrupt the geometry. The only logical-space quantities in
    /// this window are the handle sizes, the wash and the frame widths.</para>
    /// <para>The wash and the inner half of every handle land INSIDE the rectangle being mirrored
    /// into the meeting — the frame does not, it sits in the pad outside the region exactly where
    /// the border's frame sat — which every other surface in a share session is forbidden from
    /// doing (that is why
    /// <see cref="BorderWindow.SetOverlayText"/> is never called from a share session). This is the
    /// one deliberate, bounded exception: <c>ShareRegionPage</c> shows this window only after the
    /// helper has acknowledged an <c>obscure hide</c> — or after its 400 ms arm timer gives up —
    /// so the wash and the handles are drawn over a region the meeting is already being shown as a
    /// black card, and the window is closed again before the hide is lifted.</para>
    /// </summary>
    internal sealed class ShareResizeWindow : Window
    {
        /// <summary>Drawn handle diameter, logical px. The two editors draw this control at 12
        /// (TransformGizmoControl.HandleSize / GraphicBase.UnscaledControlSize); this window draws
        /// it at 15 because it is the one place the handles sit on a 5px accent frame rather than
        /// on a hairline selection outline, and at 12 they read as bumps in the frame instead of as
        /// grabbable controls. 15 rather than more: 18 was tried and overshot, crowding a small
        /// region's edges. NOT DPI-scaled: the image editor's "* uiscale.DpiScaleX" exists only to
        /// cancel its canvas render transform.</summary>
        public const double HandleSize = 15;

        /// <summary>Square hit box side, logical px — deliberately larger than the drawn circle, in
        /// the same 4:3 proportion TransformGizmoControl uses (16 around a 12px circle).</summary>
        public const double HandleHitSize = 20;

        /// <summary>How far outside the region this window is inflated on every side, so the outer
        /// half of a corner handle is inside the window bounds and therefore pressable — Avalonia
        /// routes a press only to a window whose rect contains it.
        /// <para>The handles are centred on the frame band rather than on the region edge (see
        /// <see cref="ResizeSurface.HandleAnchorRect"/>), so the outermost hit pixel now sits at
        /// roughly 3.5 + HandleHitSize/2 = 13.5 logical px out. 20 covers that with slack, and
        /// covers the frame itself — which reaches at most 6 logical px out (see
        /// <see cref="BorderLogicalWidth"/> and the outset computed in <see cref="ApplyGeometry"/>)
        /// — several times over.</para></summary>
        private const int PadLogical = 20;

        /// <summary>The accent frame's width and the width of the white line inside it, logical px.
        /// The same two numbers <see cref="BorderWindow"/> lays its frame out from
        /// (<c>AccentLogicalWidth</c> / <c>InnerLogicalWidth</c> there), duplicated only because
        /// they are private to that window. As far as the user is concerned the two are one frame
        /// that must not flicker, jump or change thickness when resize mode swaps the windows, so a
        /// change on either side has to be made on both.</summary>
        private const int AccentLogicalWidth = 3;

        private const int InnerLogicalWidth = 2;

        private const int BorderLogicalWidth = AccentLogicalWidth + InnerLogicalWidth;

        /// <summary>Fired on every accepted drag step with the new region (already clamped to the
        /// helper's rule). The page tracks the draft rectangle from it — the border window is
        /// hidden for the whole of resize mode (addendum 8.1) and this window draws the frame in
        /// its place — and does NOT touch its own <c>_region</c>, which may only be written from an
        /// applied ack.</summary>
        public event EventHandler<ScreenRect> RegionPreview;

        /// <summary>The user asked to leave resize mode WITHOUT committing: Esc outside a drag, per
        /// spec addendum 8.2. The page aborts the mode — border restored on the last applied region,
        /// obscure state restored, no move command written. Never raised during a drag (Esc there reverts
        /// the drag instead) and never raised for a commit, which is always the RESIZE tile's job.</summary>
        public event EventHandler CancelRequested;

        /// <summary>The rect the drag has arrived at, in capture space. Always &gt;= 64 per side and
        /// even, so the helper's ack should equal it.</summary>
        public ScreenRect Region { get; private set; }

        /// <summary>The control that both draws the chrome and takes the input. Held as a field
        /// because <see cref="ApplyGeometry"/> hands it the region's position within the window on
        /// every geometry pass — the window is inflated by the pad, so the drawn rectangle is not
        /// the window's own bounds.</summary>
        private readonly ResizeSurface _surface;

        /// <summary><see cref="PadLogical"/> converted to capture units at the scaling of the last
        /// geometry pass. Position and size are computed from this one value so the drawn region
        /// lands exactly on the mirrored pixels rather than a rounding away from them.</summary>
        private int _padCapture;

        public ShareResizeWindow(ScreenRect region)
        {
            if (region == null)
                throw new ArgumentNullException(nameof(region));

            // Clamped on the way in for the same reason every drag result is: the helper floors
            // each side at 64 and rounds down to even, so starting from an unclamped rect would
            // make the very first drag step appear to snap the region for no reason the user did.
            Region = ShareRegionGeometry.Clamp(region);

            Title = "Clowd Region Resize";
            WindowDecorations = WindowDecorations.None;
            Background = Brushes.Transparent;
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
            Topmost = true;
            ShowInTaskbar = false;
            CanResize = false;

            // Focused and activated, per addendum 8.2, and the only surface in a share session that
            // is: on macOS a window that never activates does not get its cursor rects honoured, so
            // the per-handle resize cursors never appear and the feature's main affordance is
            // invisible. Focus also gives this window key input, which is what makes Esc possible
            // (OnKeyDown). Activating it deactivates whatever the user was in, usually the meeting
            // client — harmless, because the helper mirrors a region of the desktop rather than the
            // focused window — and the page keeps the toolbar clickable above it with
            // RaiseTopmostNoActivate after Show().
            ShowActivated = true;
            Focusable = true;

            // Still nearly the inverse of BorderWindow's mask: TOOLWINDOW so the overlay stays out
            // of the taskbar and the alt-tab list, but pointedly NOT WS_EX_TRANSPARENT, not
            // WS_EX_LAYERED, no WM_NCHITTEST hook and no setIgnoresMouseEvents:. This window exists
            // to take clicks, and it must never gain the layered style either: a window that
            // becomes layered without a live SetLayeredWindowAttributes is never repainted by
            // Avalonia's swapchain at all. WS_EX_NOACTIVATE is gone with 8.2 — it would defeat the
            // activation the cursor rects need, and the alt-tab exclusion TOOLWINDOW gives is the
            // only part of it this window ever wanted.
            WindowNativeExtensions.AddExStyles(this, WindowNativeExtensions.WS_EX_TOOLWINDOW);

            _surface = new ResizeSurface(this);
            Content = _surface;

            // Pre-show geometry from the region's screen scaling as a best guess; Opened re-applies
            // from the window's ACTUAL RenderScaling, which is the only trustworthy value (Windows
            // assigns per-monitor-v2 DPI by majority intersection, not by the region's center).
            var guessScaling = 1.0;
            try
            {
                guessScaling = Screens.ScreenFromPoint(new PixelPoint(Region.Center.X, Region.Center.Y))?.Scaling ?? 1.0;
            }
            catch
            {
                // Screens is best-effort pre-show; Opened corrects regardless.
            }

            ApplyGeometry(guessScaling);

            Opened += OnOpened;
            ScalingChanged += OnScalingChanged;
        }

        private void OnOpened(object sender, EventArgs e)
        {
            // Raise above the menu bar BEFORE re-applying geometry, exactly as BorderWindow does:
            // at the default level AppKit constrains the frame away from the menu bar at Show(),
            // so a position set below would not stick and the handles would sit off the region
            // near the top of the screen (issue #56).
            WindowNativeExtensions.SetCanCoverMenuBar(this);
            ApplyGeometry(RenderScaling);

            // Activation is the page's job (Activate() right after Show(), because ShowActivated
            // alone is not reliable for a topmost tool window); giving the keyboard a target inside
            // the window is this window's. Avalonia routes a key to the focused element, so without
            // this Esc could land nowhere at all. The surface is the only focusable content, and
            // OnKeyDown below sees the event as it bubbles out of it.
            _surface.Focus();
        }

        private void OnScalingChanged(object sender, EventArgs e)
        {
            ApplyGeometry(RenderScaling);
        }

        /// <summary>
        /// Moves the overlay onto <see cref="Region"/> and tells the surface where within the
        /// window that region now is. Called from the constructor with a guessed scaling, from
        /// <c>Opened</c> and <c>ScalingChanged</c> with the real one, and after every accepted drag
        /// step — the window is repositioned under the pointer as the region changes, which is
        /// precisely why the drag anchors in capture space and never accumulates deltas.
        /// <para>There is deliberately NO edge suppression here (BorderWindow suppresses edges
        /// flush with a monitor edge). The pad must exist on every side or that side's handle stops
        /// being pressable. Where the pad runs past the outermost monitor it is simply not
        /// composited; the inner half of every handle is still inside the region and still inside a
        /// 16 px hit box, so every handle stays grabbable. Addendum 8.1 extends that to the frame
        /// this window now draws as well: an edge flush with a monitor edge is clipped by the
        /// desktop, which is what the pad is for, and is NOT suppressed the way the border
        /// suppresses it.</para>
        /// </summary>
        private void ApplyGeometry(double scaling)
        {
            // logical -> capture: physical px on Windows; on macOS the region is CG points, which ARE
            // logical units, so the factor is 1 even on Retina.
            var toCapture = OperatingSystem.IsMacOS() ? 1.0 : scaling;
            _padCapture = (int)Math.Ceiling(PadLogical * toCapture);

            Position = new PixelPoint(Region.X - _padCapture, Region.Y - _padCapture);   // CAPTURE units
            Width = (Region.Width + 2 * _padCapture) / toCapture;                        // LOGICAL units
            Height = (Region.Height + 2 * _padCapture) / toCapture;

            // How far outside the region the frame's OUTER edge sits, derived exactly as
            // BorderWindow.ApplyGeometry derives its own inflation — whole capture units plus one of
            // slack, so logical->capture rounding can never put a frame pixel inside the mirrored
            // region — and then converted back to logical for the surface to draw from. Deriving it
            // the same way from the same scaling is what makes the swap invisible: the frame lands
            // on the capture-space pixels the border occupied a moment ago, at the same thickness.
            var frameOutset = ((int)Math.Ceiling(BorderLogicalWidth * toCapture) + 1) / toCapture;

            // derived from _padCapture, not from PadLogical, so it lines up bit-for-bit with Position.
            var pad = _padCapture / toCapture;

            // The surface also gets the LAYOUT scaling — not toCapture, which is 1 on macOS — because
            // BorderWindow's frame is two Avalonia Borders, and a Border snaps its BorderThickness to
            // whole device pixels before drawing it. See ResizeSurface.RoundedFrameWidth: the rings
            // filled here have to be snapped the same way or the frame changes thickness on the swap.
            // Avalonia's layout scaling is the top level's RenderScaling (Controls.PresentationSource:
            // LayoutScaling => RenderScaling), which is what this method is handed.
            _surface.SetItemRect(new Rect(pad, pad, Region.Width / toCapture, Region.Height / toCapture), frameOutset, scaling);
        }

        /// <summary>Re-homes the overlay onto a region that changed underneath it — an applied
        /// <c>region_changed</c> the page did not originate from this drag. Ignored while a drag is
        /// in progress: the user's pointer is the authority then, and yanking the rect mid-drag
        /// would fight them. Does NOT raise <see cref="RegionPreview"/> — the caller already knows.
        /// </summary>
        public void ResetRegion(ScreenRect region)
        {
            if (region == null || _surface.IsDragging)
                return;

            // Clamped for the same reason the constructor clamps its argument: an applied region
            // should already satisfy the helper's rule, and if one ever does not, the first drag
            // step from it would appear to snap the region for no reason the user did.
            var next = ShareRegionGeometry.Clamp(region);
            if (next == Region)
                return;

            Region = next;
            ApplyGeometry(RenderScaling);
        }

        /// <summary>
        /// Esc, which this window only receives because addendum 8.2 made it focusable. During a
        /// drag it reverts to the rectangle the drag started from, through the same path the
        /// secondary button uses. Outside a drag it asks the page to abort the whole mode
        /// (<see cref="CancelRequested"/>). Neither ever commits — committing is the toolbar's
        /// RESIZE tile's job and nothing else's.
        /// </summary>
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            if (e.Handled || e.Key != Key.Escape)
                return;

            if (!_surface.TryCancelDrag())
                CancelRequested?.Invoke(this, EventArgs.Empty);

            e.Handled = true;
        }

        /// <summary>
        /// Accepts a drag step: re-homes the window, repaints the chrome and announces the draft
        /// region. The argument is always a rectangle the geometry helper produced (or a rectangle
        /// this window previously accepted from it), so it is already floored at 64 per side and
        /// rounded down to even — nothing here re-clamps it.
        /// <para>A step that lands on the rectangle already held is dropped whole. Pointer moves
        /// arrive far faster than the even-rounded region actually changes, and every raised
        /// <see cref="RegionPreview"/> costs a full geometry pass on the page side that changes
        /// nothing.</para>
        /// </summary>
        private void SetPendingRegion(ScreenRect region)
        {
            // ScreenRect is a record, so this is value equality on X/Y/Width/Height.
            if (region == null || region == Region)
                return;

            Region = region;

            // ApplyGeometry hands the surface its new item rect, which invalidates the visual, so
            // the wash and the handles are repainted as part of this call.
            ApplyGeometry(RenderScaling);

            RegionPreview?.Invoke(this, Region);
        }

        /// <summary>
        /// Draws the resize chrome and owns the gesture. Both jobs belong to one control because
        /// the handle positions are the same eight points either way: drawing them somewhere the
        /// hit test does not agree with is the classic way for a gizmo to become subtly unusable.
        /// </summary>
        private sealed class ResizeSurface : Control
        {
            // cached because each StandardCursorType allocates a native handle and the cursor is
            // re-evaluated on every pointer move. CursorResources.GetCursor caches internally and
            // flushes on ScalingChanged, so the Size* properties may be touched freely.
            private static readonly Cursor MoveCursor = new Cursor(StandardCursorType.SizeAll);
            private static readonly Cursor ArrowCursor = new Cursor(StandardCursorType.Arrow);

            private readonly ShareResizeWindow _owner;

            /// <summary>Where the region sits inside this control, in logical units — the control
            /// fills the whole window, which is inflated by the pad on every side, so this is
            /// always inset from the control's own bounds.</summary>
            private Rect _itemRect;

            /// <summary>Logical distance from the region's edge out to the OUTER edge of the accent
            /// frame, handed down by <see cref="ApplyGeometry"/> so it is derived from the same
            /// scaling as the window's position rather than re-guessed here.</summary>
            private double _frameOutset;

            /// <summary>The layout scaling the frame's ring widths are snapped by, handed down by
            /// <see cref="ApplyGeometry"/> for the same reason <see cref="_frameOutset"/> is: it has
            /// to be the scaling the geometry pass used, not one re-guessed here.</summary>
            private double _layoutScale = 1;

            /// <summary>The handle being dragged, or -1 when no drag is in progress. This is the
            /// entire drag state machine: a gesture that cannot be interrupted by anything but a
            /// release, a cancel or a lost capture needs no more.</summary>
            private int _handle = -1;

            /// <summary>Pointer position in CAPTURE space at press time, and the region as it was
            /// then. Every step recomputes the delta against these rather than accumulating, so a
            /// dropped move event, the window sliding under the pointer, and a mid-drag DPI change
            /// are all incapable of drifting the result.</summary>
            private PixelPoint _anchorCapture;

            private ScreenRect _anchorRegion;

            /// <summary>True while a handle is being dragged. The window consults it before
            /// re-homing itself onto a region that changed underneath the drag.</summary>
            public bool IsDragging => _handle >= 0;

            public ResizeSurface(ShareResizeWindow owner)
            {
                _owner = owner;

                // The window is focusable now (addendum 8.2) and this is its only content, so this
                // is what actually holds keyboard focus and what a key event bubbles out of on its
                // way to ShareResizeWindow.OnKeyDown.
                Focusable = true;
            }

            /// <summary>Called by the window on every geometry pass. Always invalidates: the item
            /// rect changing is exactly the thing the chrome draws.</summary>
            public void SetItemRect(Rect rect, double frameOutset, double layoutScale)
            {
                _itemRect = rect;
                _frameOutset = frameOutset;
                _layoutScale = layoutScale;
                InvalidateVisual();
            }

            public override void Render(DrawingContext context)
            {
                // A transparent fill over the WHOLE surface, first and unconditionally. Avalonia's
                // compositing renderer hit-tests a visual against the draw operations it actually
                // recorded rather than against its bounds — which is exactly why the video editor's
                // gizmo assigns Background = Brushes.Transparent to itself. Without this the pad
                // ring records nothing, so a press in it would never be routed here (the two paths
                // below that depend on that are the pad-ring swallow, and the 2px of hit slack
                // around each handle that falls outside its drawn 12px circle).
                context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

                var r = _itemRect;
                if (r.Width <= 0 || r.Height <= 0)
                    return;

                // CaptureAccentColor (not AppStyles.AccentColor, which the two editors use): this window is
                // part of the capture-surface family the border and the toolbar belong to, and while resize
                // mode is up it draws that family's frame itself. Read once per Render — every AppStyles
                // brush property allocates.
                var color = AppStyles.CaptureAccentColor;
                var accent = new SolidColorBrush(color);

                // The region frame, first and entirely OUTSIDE the region: BorderWindow is hidden
                // for the whole of resize mode (addendum 8.1), so nothing else marks the mirrored
                // rectangle. Same two rings in the same order the border stacks them — 3 logical px
                // of accent outermost, 2 of white inside it — at the same outward offset, so the
                // swap neither moves the frame nor changes its thickness. Both widths go through
                // RoundedFrameWidth, and the white ring stacks on the ROUNDED accent width because
                // that is where the border's inner Border lands. No monitor-edge suppression: an
                // edge flush with a monitor edge is simply clipped by the desktop.
                var accentWidth = RoundedFrameWidth(AccentLogicalWidth);
                var outer = r.Inflate(_frameOutset);
                FillRing(context, outer, accentWidth, accent);
                FillRing(context, outer.Deflate(accentWidth), RoundedFrameWidth(InnerLogicalWidth), Brushes.White);

                context.FillRectangle(new SolidColorBrush(color, 0.25), r);

                // Handles last: each one is centred on the frame band (HandleAnchorRect) rather than
                // on the region edge, and it has to win over the frame there the same way it won
                // when it was a separate window sitting above the border.
                foreach (var (_, p) in HandlePoints(HandleAnchorRect()))
                    DrawHandle(context, p, accent);
            }

            /// <summary>One of the frame's two ring widths, snapped to whole device pixels exactly
            /// the way <see cref="BorderWindow"/>'s frame is.
            /// <para>That frame is two nested Avalonia <c>Border</c>s, and a <c>Border</c> does not
            /// draw the <c>BorderThickness</c> it was given: with <c>UseLayoutRounding</c> on — the
            /// inherited default — it draws <c>LayoutHelper.RoundLayoutThickness</c> of it, and
            /// arranges its child inside the rounded thickness too, so the white line starts at the
            /// rounded accent width rather than at a literal 3. Filling the raw logical widths here
            /// would make the frame change thickness and shift the white line by half a physical
            /// pixel every time the swap happened at 125/150/175 percent scaling. Calling Avalonia's
            /// own helper rather than restating <c>Math.Round(t * scale) / scale</c> keeps the two
            /// in step if the rule ever changes.</para>
            /// <para>Rounding can only ever add half a device pixel per ring, so the two rounded
            /// widths still sum to at most 5 + 1/scale logical px, which is what
            /// <c>frameOutset</c> already reserves. Invariant I1 — no frame ink inside the mirrored
            /// region — survives this.</para></summary>
            private double RoundedFrameWidth(double thickness)
                => LayoutHelper.RoundLayoutThickness(new Thickness(thickness), _layoutScale).Top;

            /// <summary>Fills the ring between <paramref name="outer"/> and the rectangle
            /// <paramref name="thickness"/> logical px inside it, as four filled rectangles rather
            /// than one stroked rectangle: a pen is centred on its own rect, so its coverage would
            /// depend on rounding the outset onto a half pixel, and the frame must not change
            /// thickness when the border window hands over to this one.</summary>
            private static void FillRing(DrawingContext context, Rect outer, double thickness, IBrush brush)
            {
                if (outer.Width <= 2 * thickness || outer.Height <= 2 * thickness)
                    return;

                var inner = outer.Deflate(thickness);
                context.FillRectangle(brush, new Rect(outer.X, outer.Y, outer.Width, thickness));
                context.FillRectangle(brush, new Rect(outer.X, inner.Bottom, outer.Width, thickness));
                context.FillRectangle(brush, new Rect(outer.X, inner.Y, thickness, inner.Height));
                context.FillRectangle(brush, new Rect(inner.Right, inner.Y, thickness, inner.Height));
            }

            /// <summary>TransformGizmoControl.DrawHandle's control, scaled to this window's larger
            /// <see cref="HandleSize"/>: three concentric FILLED ellipses — accent, then opaque
            /// white, then accent again — with no pen, no stroke and no shadow. The two inner radii
            /// are the editor's 1px white ring and 3px inset expressed as fractions of the radius
            /// (r-r/6, r-r/2, which at the editor's 12px reproduce its 5 and 3 exactly), so the
            /// control keeps its proportions at any size rather than turning into a white disc with
            /// a dot in it.</summary>
            private static void DrawHandle(DrawingContext context, Point center, IBrush accent)
            {
                var radius = HandleSize / 2;
                context.DrawEllipse(accent, null, center, radius, radius);
                context.DrawEllipse(Brushes.White, null, center, radius - radius / 6, radius - radius / 6);
                context.DrawEllipse(accent, null, center, radius / 2, radius / 2);
            }

            /// <summary>
            /// The rectangle the eight handles are centred on: the CENTRE LINE of the drawn frame,
            /// not the region edge.
            /// <para>Centred on the region edge — where a gizmo in either editor puts them, because
            /// there the selection outline is a hairline sitting exactly on that edge — a handle
            /// here lands against the inner lip of a 5px band and reads as visibly off-centre, the
            /// frame crowding it on one side and nothing balancing it on the other. Offsetting by
            /// half the frame's ink puts the handle's centre on the band's centre, which is what
            /// "on the border" looks like.</para>
            /// <para>Both the drawing and the hit test go through this, so they cannot disagree —
            /// the one failure mode that makes a gizmo subtly unusable.</para>
            /// </summary>
            private Rect HandleAnchorRect()
            {
                // _frameOutset reaches the frame's OUTER edge; the ink runs inward from there by the
                // two rounded ring widths. Half of that back off the outer edge is the centre line.
                var ink = RoundedFrameWidth(AccentLogicalWidth) + RoundedFrameWidth(InnerLogicalWidth);
                return _itemRect.Inflate(Math.Max(0, _frameOutset - ink / 2));
            }

            /// <summary>The eight handle centres, corners FIRST. The order is the hit-test order,
            /// and corners have to win: a 64x64 region — the smallest the helper allows — puts a
            /// corner centre and an edge centre only 32 logical px apart, well inside the reach of
            /// two overlapping 20 px hit boxes.</summary>
            private static (int Index, Point Center)[] HandlePoints(Rect b) => new (int Index, Point Center)[]
            {
                (ShareRegionGeometry.HandleTopLeft, b.TopLeft),
                (ShareRegionGeometry.HandleTopRight, b.TopRight),
                (ShareRegionGeometry.HandleBottomLeft, b.BottomLeft),
                (ShareRegionGeometry.HandleBottomRight, b.BottomRight),
                (ShareRegionGeometry.HandleLeft, new Point(b.Left, b.Center.Y)),
                (ShareRegionGeometry.HandleTop, new Point(b.Center.X, b.Top)),
                (ShareRegionGeometry.HandleRight, new Point(b.Right, b.Center.Y)),
                (ShareRegionGeometry.HandleBottom, new Point(b.Center.X, b.Bottom)),
            };

            /// <summary>Which handle the point is over, <see cref="ShareRegionGeometry.HandleBody"/>
            /// for the region's interior, or -1 for the pad ring outside it. The box is an
            /// axis-aligned SQUARE (an absolute test per axis), not a radial distance — the same
            /// test both editors use, so a handle feels the same size here as it does there.</summary>
            private int HitHandle(Point p)
            {
                foreach (var (index, pt) in HandlePoints(HandleAnchorRect()))
                    if (Math.Abs(p.X - pt.X) <= HandleHitSize / 2 && Math.Abs(p.Y - pt.Y) <= HandleHitSize / 2)
                        return index;
                return _itemRect.Contains(p) ? ShareRegionGeometry.HandleBody : -1;
            }

            /// <summary>The cursor for a handle. A shared region never rotates, so the image
            /// editor's angle formula — <c>(45*handle + rotation + 272.5) / 5 % 36</c> — collapses
            /// to these four constants, which also sidesteps that formula's negative-angle
            /// ArgumentOutOfRangeException entirely.</summary>
            private static Cursor CursorFor(int handle) => handle switch
            {
                ShareRegionGeometry.HandleTopLeft or ShareRegionGeometry.HandleBottomRight
                    => CursorResources.GetResizeCursor(27),
                ShareRegionGeometry.HandleTop or ShareRegionGeometry.HandleBottom
                    => CursorResources.GetResizeCursor(0),
                ShareRegionGeometry.HandleTopRight or ShareRegionGeometry.HandleBottomLeft
                    => CursorResources.GetResizeCursor(9),
                ShareRegionGeometry.HandleLeft or ShareRegionGeometry.HandleRight
                    => CursorResources.GetResizeCursor(18),
                ShareRegionGeometry.HandleBody => MoveCursor,
                _ => ArrowCursor,      // in the pad ring, off every handle
            };

            protected override void OnPointerPressed(PointerPressedEventArgs e)
            {
                base.OnPointerPressed(e);

                var props = e.GetCurrentPoint(this).Properties;

                // The secondary button cancels a drag in progress — one of two ways in since
                // addendum 8.2 made this window focusable, the other being Esc. Outside a drag it
                // still does nothing at all: leaving the mode without committing is Esc's job now,
                // and committing is the toolbar's RESIZE tile's.
                if (props.IsRightButtonPressed)
                {
                    TryCancelDrag();
                    e.Handled = true;
                    return;
                }

                if (!props.IsLeftButtonPressed)
                    return;

                var h = HitHandle(e.GetPosition(this));
                if (h < 0)
                {
                    // the pad ring: swallow the press so it cannot reach whatever is underneath,
                    // but start nothing. The ring exists to make the outer half of the handles
                    // pressable, not to be a drag surface of its own.
                    e.Handled = true;
                    return;
                }

                _handle = h;
                _anchorRegion = _owner.Region;
                _anchorCapture = _owner.PointToScreen(e.GetPosition(_owner));
                e.Pointer.Capture(this);
                e.Handled = true;
            }

            protected override void OnPointerMoved(PointerEventArgs e)
            {
                base.OnPointerMoved(e);

                if (_handle < 0)
                {
                    // hover feedback that tells the truth about what a press will do.
                    Cursor = CursorFor(HitHandle(e.GetPosition(this)));
                    return;
                }

                // PointToScreen returns a PixelPoint already in capture space on both platforms
                // (the recipe FloatingToolbarWindow.DragHandleMoved uses), so this delta needs no
                // scaling, and the window being repositioned under the pointer on the previous step
                // cancels out exactly. Never accumulate deltas frame to frame.
                var now = _owner.PointToScreen(e.GetPosition(_owner));
                var next = ShareRegionGeometry.ApplyDrag(_anchorRegion, _handle,
                                                         now.X - _anchorCapture.X, now.Y - _anchorCapture.Y);
                _owner.SetPendingRegion(next);
                e.Handled = true;
            }

            protected override void OnPointerReleased(PointerReleasedEventArgs e)
            {
                base.OnPointerReleased(e);

                EndDrag();
                e.Pointer.Capture(null);
                e.Handled = true;
            }

            protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
            {
                base.OnPointerCaptureLost(e);

                // never leave the window glued to the pointer: a capture stolen by anything else
                // ends the gesture where it stands, keeping whatever the last accepted step drew.
                EndDrag();
            }

            /// <summary>Reverts to the rectangle the drag started from and ends it, reporting false
            /// when there was no drag to cancel — which is how the window tells an Esc that reverts
            /// a drag from an Esc that leaves the mode. The revert goes through the same
            /// accepted-step path as any other move, so everything watching the preview follows it
            /// back. Both cancel routes, the secondary button and Esc, land here.</summary>
            public bool TryCancelDrag()
            {
                if (_handle < 0)
                    return false;

                _owner.SetPendingRegion(_anchorRegion);
                EndDrag();
                return true;
            }

            private void EndDrag()
            {
                _handle = -1;
            }
        }
    }
}

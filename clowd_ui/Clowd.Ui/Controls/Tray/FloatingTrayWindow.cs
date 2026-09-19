using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Clowd.PlatformUtil;
using Clowd.UI.Helpers;

namespace Clowd.UI.Controls.Tray
{
    /// <summary>Construction-time choices for a <see cref="FloatingTrayWindow"/>. Immutable on purpose:
    /// every one of these is baked into the native window before it is first shown.</summary>
    public sealed record FloatingTrayOptions
    {
        public string Title { get; init; } = "Clowd";

        /// <summary>False builds a strip with no <see cref="TrayGrip"/>: not draggable, not rotatable,
        /// default cursor everywhere. Mutually exclusive with <see cref="FixedTraySize"/> (the constructor
        /// throws): the chassis cannot honour a hand-placed or rotated strip whose size is pinned.</summary>
        public bool HasGrip { get; init; } = true;

        /// <summary>
        /// Logical size of the tray when the owner must know it BEFORE the window is ever laid out —
        /// a strip that has to decide whether it can be shown outside a region at all. Set ⇒ the
        /// window is <see cref="SizeToContent.Manual"/> and <see cref="FloatingTrayWindow.TryShowNear"/>
        /// is the show path; unset ⇒ size-to-content and <see cref="FloatingTrayWindow.ShowNear"/>.
        /// Mutually exclusive with <see cref="HasGrip"/> (the constructor throws): a fixed size is
        /// re-placed unconditionally and does not follow a rotation.
        /// </summary>
        public Size? FixedTraySize { get; init; }

        /// <summary>Inserts the placement cascade's optional "centred above" rung ahead of the two
        /// vertical ones (<see cref="TrayPlacement.Near"/>).</summary>
        public bool PreferAboveBeforeVertical { get; init; }

        public BoxShadows Shadow { get; init; } = TrayTokens.ShadowCompact;
    }

    /// <summary>
    /// The window chassis common to every floating strip: a topmost, non-activating, transparent,
    /// undecorated window holding one <see cref="FloatingTray"/>, plus everything that was byte-identical
    /// between the old strips — the native ex-styles, the pre-show parking that fixes DPI, the placement
    /// cascade plumbing, the grip's drag/rotate wiring, the tooltip anchoring and the status blip.
    /// Subclasses add their controls to <see cref="Tray"/>.Items in their constructor and then call
    /// <see cref="ShowNear"/> (or <see cref="TryShowNear"/> for a fixed-size strip).
    /// <para>
    /// The window is a plain <see cref="Window"/>, never a themed one: a themed window drives the
    /// transparency hint and repaints its background from the app theme, which would fight a
    /// transparent tray. On Windows WS_EX_NOACTIVATE keeps a click on a tile from stealing focus from the
    /// app underneath; on macOS a plain Avalonia window still activates the app on click.
    /// </para>
    /// </summary>
    public abstract class FloatingTrayWindow : Window
    {
        private static readonly Uri PopupStylesUri = new("avares://Clowd.Ui/Controls/Tray/TrayPopups.axaml");

        // the transparent band TrayPopups.axaml keeps around the menu's visible border for its shadow;
        // see ShowMenu.
        private const string MenuShadowReserveKey = "TrayMenuShadowReserve";

        /// <summary>
        /// Breathing room between a fixed-size strip's PAINTED tray and the region, in logical px (the
        /// shadow reserve overlaps it, see <see cref="TrayPlacement.Outside"/>). The accent frame drawn
        /// around a region inflates itself outward by roughly 6 logical px; this is an independent copy
        /// of that clearance and does NOT track it (the frame's constants are private to its file and
        /// nothing in the build catches a divergence), so it is set deliberately wider than the
        /// inflation to leave real clear space between the frame and the strip rather than merely
        /// avoiding an overlap.
        /// </summary>
        private const int GapLogical = 10;

        private readonly FloatingTrayOptions _options;
        private readonly Panel _root;

        // the region the strip is placed around, in the platform capture space (physical px in
        // virtual-desktop coordinates on Windows, CG points on macOS) — which is also what PixelPoint uses.
        private ScreenRect _region;

        // set by a drag that crossed the threshold or a rotate click, never by a bare press; cleared by
        // ShowNear, and by UpdateRegion when the moved region now covers the strip.
        private bool _manuallyPositioned;
        private PixelPoint _dragOrigin;

        // true while the ONE extra pass a rotation is allowed to queue is in flight; see Reposition.
        private bool _rotatedPassPending;

        // true when a placement pass was refused because the grip was held: the refused pass is owed back
        // once that press ends without a drag, or a scaling / region change arriving inside a click that
        // never crosses the threshold would leave an auto-placed strip stale for the rest of the run.
        private bool _repositionDeferred;

        // one-shot timer behind ShowStatusBlip; built lazily together with its popup because most
        // strips never have anything to say this way.
        private Popup _blip;
        private ContentControl _blipContent;
        private DispatcherTimer _blipTimer;

        /// <summary>The panel. Subclasses add their controls to its Items before showing the window.</summary>
        public FloatingTray Tray { get; }

        /// <summary>The drag/rotate handle, always the first item; null when the options asked for none.</summary>
        public TrayGrip Grip { get; }

        /// <summary>The strip's axis. The chassis is the only writer: the placement cascade, or a rotate click.</summary>
        public Orientation Orientation => Tray.Orientation;

        private bool IsFixedSize => _options.FixedTraySize.HasValue;

        /// <summary>
        /// Every line of the window setup below is load-bearing, and the order matters: the ex-styles must
        /// be registered before Show(), the parking spot must be set before Show(), and the transparency
        /// pair (brush AND hint) must both be set or the window composites against an opaque ground.
        /// </summary>
        protected FloatingTrayWindow(FloatingTrayOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));

            // the pair cannot be honoured, so it is refused rather than half-implemented: the fixed-size
            // placement branch runs before the manual latch is even consulted (a dragged strip snaps back),
            // and a rotate flips the axis while the pinned Width/Height stay put (the strip clips).
            if (options.HasGrip && options.FixedTraySize.HasValue)
                throw new ArgumentException("A fixed-size tray cannot carry a grip: placement ignores the manual latch and the size does not follow rotation.", nameof(options));

            Title = options.Title;
            WindowDecorations = WindowDecorations.None;

            // the strip's size IS its content, and rotation works by re-measure — except for a strip
            // whose owner must know the size before the window exists, which declares it up front.
            SizeToContent = IsFixedSize ? SizeToContent.Manual : SizeToContent.WidthAndHeight;

            Topmost = true;
            // load-bearing on macOS, not cosmetic: the dock icon appears for any visible window that is
            // in the taskbar, and a dock resize mid-capture shifts the very content being captured.
            ShowInTaskbar = false;
            CanResize = false;
            ShowActivated = false;
            Background = Brushes.Transparent;
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };

            // never steal focus from the app beneath; no taskbar/alt-tab entry. A one-way door: these
            // styles cannot be removed for the life of the window, so this is the only place they are set.
            WindowNativeExtensions.AddExStyles(this, WindowNativeExtensions.WS_EX_NOACTIVATE | WindowNativeExtensions.WS_EX_TOOLWINDOW);

            // the one runtime value in the tray's palette: the user's capture accent, read once here and
            // reached by every tray theme through a DynamicResource.
            Resources[TrayTokens.AccentBrushKey] = new ImmutableSolidColorBrush(AppStyles.CaptureAccentColor);

            // the dark tooltip / menu look is scoped to tray windows (and their popups, whose styling
            // parent chain reaches this window) rather than merged app-wide.
            Styles.Add(new StyleInclude(PopupStylesUri) { Source = PopupStylesUri });

            // the window reserves transparent room around the tray for its shadow. The reserve is always
            // read back off the live Margin by the placement code, never off the option, so the
            // transparency fallback below can take it away without anything else noticing.
            Tray = new FloatingTray
            {
                Shadow = options.Shadow,
                Margin = TrayPlacement.ShadowReserve(options.Shadow),
            };

            // several tiles are disabled exactly when their tip is the only explanation, and ToolTipService
            // shows no tip on a disabled control unless this is set. The property inherits, so this single
            // write covers every current and future item; the :disabled themes already pin the hover veil to 0.
            ToolTip.SetShowOnDisabled(Tray, true);

            if (IsFixedSize)
            {
                var size = options.FixedTraySize.Value;
                Tray.Width = size.Width;
                Tray.Height = size.Height;
                Width = size.Width + Tray.Margin.Left + Tray.Margin.Right;
                Height = size.Height + Tray.Margin.Top + Tray.Margin.Bottom;
            }

            // the tray plus, lazily, the blip popup: a Popup contributes nothing to layout, so the
            // window still sizes to the tray and its margin, and the popup gets a logical parent inside
            // this window so its content resolves the tray themes.
            _root = new Panel { Children = { Tray } };
            Content = _root;

            if (options.HasGrip)
            {
                Grip = new TrayGrip();
                ToolTip.SetTip(Grip, "Drag to move");
                Tray.Items.Add(Grip);

                // the manual latch is set when the threshold is crossed, not on press: a press-release
                // under the threshold does nothing, and must not opt the strip out of auto-placement.
                Grip.DragStarted += (s, e) =>
                {
                    _dragOrigin = Position;
                    _manuallyPositioned = true;
                };
                Grip.DragDelta += (s, delta) => Position = _dragOrigin + delta;

                // the pass Reposition refused while the grip was held: a press that never became a drag
                // moved nothing and latched nothing, so the strip still owes itself that placement.
                Grip.PressEnded += (s, e) =>
                {
                    if (_repositionDeferred && !_manuallyPositioned)
                    {
                        _repositionDeferred = false;
                        QueueReposition();
                    }
                };

                // top-left anchored: Position is untouched and size-to-content re-lays out the strip.
                Grip.RotateRequested += (s, e) =>
                {
                    _manuallyPositioned = true;
                    SetOrientation(Orientation == Orientation.Horizontal ? Orientation.Vertical : Orientation.Horizontal);
                };
            }

            // the app's mark heads every strip, after the grip and before the owner's first item. Added
            // here, unconditionally, so no strip can forget it; a fixed-size owner has to count
            // TrayTokens.EmblemLength (plus a gap) into the size it declares.
            Tray.Items.Add(new TrayEmblem());

            // an item added after the window is up gets its tooltip aimed once its template exists.
            Tray.ContainerPrepared += (s, e) =>
            {
                if (IsVisible)
                    Dispatcher.UIThread.Post(AimToolTips, DispatcherPriority.Loaded);
            };

            // a DPI change moves the strip in capture space even though the region has not moved,
            // and can take the placement that fitted away entirely.
            ScalingChanged += (s, e) => QueueReposition();

            Opened += (s, e) =>
            {
                // macOS: lift the window above the menu-bar level BEFORE any positioning, or a
                // position that overlaps the menu bar does not stick. No-op on Windows.
                WindowNativeExtensions.SetCanCoverMenuBar(this);

                // a platform that cannot composite a transparent window would paint the shadow's
                // reserve as a solid band: drop the shadow and give the room back.
                if (ActualTransparencyLevel != WindowTransparencyLevel.Transparent)
                {
                    Tray.Shadow = default;
                    Tray.Margin = new Thickness(0);
                    if (IsFixedSize)
                    {
                        Width = _options.FixedTraySize.Value.Width;
                        Height = _options.FixedTraySize.Value.Height;
                    }
                    QueueReposition();
                }
            };
        }

        /// <summary>
        /// Shows the strip placed via the cascade (centred below the region → [centred above] →
        /// vertical right → vertical left → horizontally inside near its bottom), clamped to the
        /// monitor's working area. The region is in physical px on Windows / CG points on macOS —
        /// the same space Avalonia PixelPoint positioning uses.
        /// </summary>
        public void ShowNear(ScreenRect region)
        {
            if (IsFixedSize)
                throw new InvalidOperationException("A fixed-size tray is shown with TryShowNear, which places it before it is ever visible.");

            _region = region ?? throw new ArgumentNullException(nameof(region));
            _manuallyPositioned = false;

            if (!IsVisible)
            {
                ParkOnRegionScreen(region);
                SeedClientSizeForShow();
                Show();

                // the seed above is a pair of exact measure constraints, so it has to be given back
                // immediately: the strip's length changes while it runs (a tile appearing, a chevron
                // going away) and only size-to-content can follow that.
                Width = Double.NaN;
                Height = Double.NaN;

                // Show() has already run the initial layout pass, so Tray.Bounds is real by now —
                // place the strip before the first frame can be presented at the parking pixel.
                AimToolTips();
                Reposition();
            }

            // position after the size-to-content layout pass so Bounds is real
            QueueReposition();
        }

        /// <summary>
        /// Shows a fixed-size strip outside <paramref name="region"/>, or returns false without ever
        /// showing it when no placement clears the region on the region's own monitor. The position
        /// is applied BEFORE <see cref="Window.Show"/>, so there is no frame in which the strip exists
        /// somewhere provisional — the point of a fixed size is that the search can run before the
        /// window has ever been laid out.
        /// </summary>
        public bool TryShowNear(ScreenRect region)
        {
            if (!IsFixedSize)
                throw new InvalidOperationException("A size-to-content tray is shown with ShowNear; TryShowNear needs FixedTraySize.");

            _region = region ?? throw new ArgumentNullException(nameof(region));

            // pre-show guess: the window has no RenderScaling of its own yet, and the posted
            // Reposition corrects from the real value once it does. Called bare, exactly like the same
            // helper in ParkOnRegionScreen and Reposition: no screen resolves to a scaling of 1 through
            // the null check, and a wrong guess costs a reposition, not a frame inside the region.
            var scaling = DesktopScreens.FromPoint(this, new PixelPoint(region.Center.X, region.Center.Y))?.Scaling ?? 1.0;

            var position = ComputeOutside(region, scaling);
            if (position == null)
                return false;

            Position = position.Value;
            Show();
            AimToolTips();

            QueueReposition();
            return true;
        }

        /// <summary>
        /// Follows a region that MOVED while the strip is up. Deliberately not <see cref="ShowNear"/>:
        /// that resets the manual latch, so using it here would yank a strip the user had placed by
        /// hand back into the cascade every time they nudged the region.
        /// A hand-placed strip therefore keeps its position, with one exception — if the moved region
        /// now covers the strip's own window rect, the placement is dropped and the cascade re-runs.
        /// A strip sitting inside a region whose pixels are being captured continuously is in the
        /// picture for the rest of the run, and that beats honouring where it was put.
        /// </summary>
        public void UpdateRegion(ScreenRect region)
        {
            if (region == null)
                return;

            _region = region;

            if (_manuallyPositioned)
            {
                var scaling = OperatingSystem.IsMacOS() ? 1.0 : RenderScaling;
                var w = (int)Math.Ceiling(Bounds.Width * scaling);
                var h = (int)Math.Ceiling(Bounds.Height * scaling);
                var self = new ScreenRect(Position.X, Position.Y, Math.Max(w, 1), Math.Max(h, 1));
                if (!self.IntersectsWith(region))
                    return;

                // the moved region now covers the strip: being out of the picture beats being where
                // the user put it.
                _manuallyPositioned = false;
            }

            QueueReposition();
        }

        /// <summary>
        /// Says something beside the strip for a few seconds. The strip has no other surface for a
        /// message, and a dialog is exactly what must not appear while the desktop is being captured.
        /// A newer blip replaces the previous one outright — restart, not extend. The popup is a
        /// non-activating top-level exactly like the tooltips already on the strip, and it is not
        /// hit-testable, so it never eats a click meant for the tiles.
        /// </summary>
        public void ShowStatusBlip(string text)
        {
            if (_blip == null)
            {
                _blipContent = new ContentControl { IsHitTestVisible = false };
                if (this.TryFindResource("TrayTipTheme", out var theme) && theme is ControlTheme tipTheme)
                    _blipContent.Theme = tipTheme;

                _blip = new Popup
                {
                    PlacementTarget = Tray,
                    IsLightDismissEnabled = false,
                    WindowManagerAddShadowHint = false,
                    Child = _blipContent,
                };
                _root.Children.Add(_blip);

                _blipTimer = new DispatcherTimer { Interval = TrayTokens.BlipDuration };
                _blipTimer.Tick += (s, e) =>
                {
                    _blipTimer.Stop();
                    _blip.IsOpen = false;
                };
            }

            // re-aimed on every call: the strip may have rotated since the last one, and the popup
            // is re-opened rather than moved so the new side is applied for certain.
            _blip.IsOpen = false;
            AimBlip();
            _blipContent.Content = text;
            _blip.IsOpen = true;

            // restart rather than extend: a second message replaces the first outright.
            _blipTimer.Stop();
            _blipTimer.Start();
        }

        /// <summary>
        /// Aims the blip popup at the strip's free side — below a row, to the right of a column. The gap
        /// is the chip theme's own margin (<see cref="TrayTokens.TipMargin"/>), so no offset is set here
        /// and a blip the positioner flips to the other side keeps the same clearance. A Popup applies its
        /// Placement when it opens, so anything that re-aims an already-open blip has to close and re-open it.
        /// </summary>
        private void AimBlip()
        {
            var horizontal = Orientation == Orientation.Horizontal;
            _blip.Placement = horizontal ? PlacementMode.Bottom : PlacementMode.Right;
        }

        /// <summary>Re-runs placement after the size-to-content layout pass. Call after anything that
        /// changes the tray's size (a tile appearing, a chevron going away): with size-to-content the
        /// window grows or shrinks in place, and only the cascade can re-centre it.</summary>
        protected void QueueReposition()
        {
            Dispatcher.UIThread.Post(Reposition, DispatcherPriority.Loaded);
        }

        /// <summary>Opens a menu off a tile on the strip's free side: below it, left-aligned, in a row;
        /// to its right, top-aligned, in a column — always with the spec's 8 px gap.</summary>
        /// <remarks>
        /// The gap, and the edge alignment, are measured from the border people SEE. The menu presenter
        /// (TrayPopups.axaml) carries a transparent margin around that border, because a popup root sizes
        /// itself to its child's layout bounds and a BoxShadow is painted outside them — without the
        /// margin the shadow falls off the popup window and is never drawn. That margin sits between the
        /// presenter's layout edge, which is what these offsets position, and the visible edge, so the
        /// left/top of it is subtracted here. The value is read back from the same resource the template
        /// applies (it is not a second copy of the numbers), and a missing resource degrades to the raw
        /// gap — the placement is off by the shadow's reach, nothing throws.
        /// </remarks>
        protected void ShowMenu(MenuFlyout flyout, Control anchor)
        {
            if (flyout == null)
                throw new ArgumentNullException(nameof(flyout));
            if (anchor == null)
                throw new ArgumentNullException(nameof(anchor));

            var reserve = this.TryFindResource(MenuShadowReserveKey, out var value) && value is Thickness t
                ? t
                : default;

            var horizontal = Orientation == Orientation.Horizontal;
            flyout.Placement = horizontal ? PlacementMode.BottomEdgeAlignedLeft : PlacementMode.RightEdgeAlignedTop;
            flyout.VerticalOffset = horizontal ? TrayTokens.MenuGap - reserve.Top : -reserve.Top;
            flyout.HorizontalOffset = horizontal ? -reserve.Left : TrayTokens.MenuGap - reserve.Left;
            flyout.ShowAt(anchor);
        }

        /// <summary>
        /// Takes any open blip down with the strip. The blip is its own top-level, so hiding this window
        /// leaves it floating beside nothing — and an owner that tears the strip down typically hides it
        /// first and closes it a good deal later (after an async teardown, or a message of its own), well
        /// inside the blip's few seconds. The next <see cref="ShowStatusBlip"/> re-opens it normally.
        /// The fixed-size placement failure path hides the window too, and dropping the blip there is
        /// equally correct: nothing should be left pointing at a strip that is no longer on screen.
        /// </summary>
        public override void Hide()
        {
            CloseBlip();
            base.Hide();
        }

        protected override void OnClosed(EventArgs e)
        {
            CloseBlip();
            base.OnClosed(e);
        }

        /// <summary>Stops the countdown and takes the popup down; shared by <see cref="Hide"/> and
        /// <see cref="OnClosed"/> so the two teardown paths cannot drift.</summary>
        private void CloseBlip()
        {
            _blipTimer?.Stop();
            if (_blip != null)
                _blip.IsOpen = false;
        }

        /// <summary>
        /// Gives the native window its content's size BEFORE it is created on screen, so that the first
        /// size-to-content pass has nothing left to resize.
        /// </summary>
        /// <remarks>
        /// Without this the window is created at the platform's default size (a fraction of the working
        /// area — 2564x984 in a launch traced on a 3440x1440 monitor at 100%) and shrinks to the strip on a
        /// later layout pass. That resize goes through Win32 SetWindowPlacement, which fits a window whose
        /// rect does not lie inside the monitor's working area back inside it — and the parking spot below
        /// is deliberately the screen's LAST pixel, so a default-sized window there hangs far outside it.
        /// The platform therefore moved the strip to the working area's bottom-right corner on its way to
        /// being resized (the stray "window 2702,408" / "2932,408" triples in that trace: exactly
        /// workArea.Right − the resized width, workArea.Bottom − the pre-resize height). Nothing our own
        /// placement computed was ever wrong — <see cref="TrayPlacement.Near"/> cannot produce those
        /// positions for any tray size — but the window did visit a position it was never told to, between
        /// the park and the placement.
        /// <para>
        /// Avalonia's Resize returns early when the client rect already matches what is asked for, so a
        /// window that is created at its content size is never handed to SetWindowPlacement at all. The
        /// tray is measured here rather than read off Bounds because no layout pass has run yet; it is in
        /// the window's logical tree from the constructor (Content was set there), so its theme is applied
        /// and the measurement is the real one. A zero measurement — no render platform, nothing added to
        /// the tray — is left alone rather than forced onto the window.
        /// </para>
        /// </remarks>
        private void SeedClientSizeForShow()
        {
            Tray.Measure(Size.Infinity);

            // DesiredSize includes the Margin, which is the shadow's reserve: the window's extents.
            var desired = Tray.DesiredSize;
            if (desired.Width <= 0 || desired.Height <= 0)
                return;

            Width = desired.Width;
            Height = desired.Height;
        }

        /// <summary>
        /// Pre-show parking spot, and the only thing that makes SizeToContent produce a correctly
        /// scaled window on a non-100% monitor. Window.ShowCore sizes the platform window during
        /// Show() using the scaling of the screen under Position (WindowStartupLocation is Manual
        /// here), and the Win32 impl seeds its DPI from the monitor nearest the window rect.
        /// Parking off the virtual desktop resolved both lookups to no screen / the top-left
        /// monitor, so on a scaled target monitor the strip was created at the wrong scale and the
        /// buttons were clipped until a monitor change forced a WM_DPICHANGED to rescale it.
        /// The target monitor's last pixel resolves both lookups to the right monitor while keeping
        /// all but one pixel of the strip off-screen for the frame before Reposition runs.
        /// </summary>
        private void ParkOnRegionScreen(ScreenRect region)
        {
            var screen = DesktopScreens.FromPoint(this, new PixelPoint(region.Center.X, region.Center.Y)) ?? DesktopScreens.Primary(this);
            if (screen == null)
                return;

            Position = new PixelPoint(screen.Bounds.Right - 1, screen.Bounds.Bottom - 1);
        }

        /// <summary>
        /// Applies a placement to the live window. Which slot is chosen, and what is measured against
        /// what, belongs to the cascades and is documented there: <see cref="TrayPlacement.Near"/> for a
        /// size-to-content strip, <see cref="TrayPlacement.Outside"/> for a fixed-size one. What is the
        /// chassis's own is the state around them — all math in capture px on the monitor under the
        /// region's centre; skipped outright once the strip has been dragged or rotated by hand; deferred
        /// while the grip is held and owed back exactly once when that press ends without a drag; a single
        /// bounded re-pass when the cascade flips the axis, so the rotated tray can be re-measured; and,
        /// for a fixed-size strip whose slot no longer fits at the real scaling, hidden outright — being
        /// invisible is always better than being in the picture.
        /// </summary>
        private void Reposition()
        {
            // a rotation below re-queues this method once so the rotated tray can be re-measured; the
            // flag is read and cleared here, before any early return, so an abandoned pass cannot leave
            // a later external request looking like a second pass.
            var isSecondPass = _rotatedPassPending;
            _rotatedPassPending = false;

            if (_region == null || !IsVisible)
                return;

            if (IsFixedSize)
            {
                var fixedPosition = ComputeOutside(_region, RenderScaling);
                if (fixedPosition != null)
                    Position = fixedPosition.Value;
                else
                    Hide();
                return;
            }

            if (_manuallyPositioned)
                return;

            // auto-placement is deferred while the grip is held so the strip cannot move out from under
            // a press that may become a drag: the drag delta is measured from the PRESS point, so a
            // posted pass (ShowNear's, ScalingChanged, UpdateRegion) landing between the press and the
            // first over-threshold move would be undone by the very first delta. The latch itself still
            // moves only on DragStarted, so a sub-threshold click still changes nothing.
            if (Grip?.IsPressed == true)
            {
                // owed back, not dropped: the grip reports a press that ended without a drag, and the
                // wiring in the constructor re-queues this pass then.
                _repositionDeferred = true;
                return;
            }

            // this pass is going through, so nothing is owed any more.
            _repositionDeferred = false;

            // logical → capture space: physical px on Windows; on macOS the region and Position
            // are CG points == logical units, so no scaling applies even on Retina.
            var scaling = OperatingSystem.IsMacOS() ? 1.0 : RenderScaling;
            var trayWidth = (int)Math.Ceiling(Tray.Bounds.Width * scaling);
            var trayHeight = (int)Math.Ceiling(Tray.Bounds.Height * scaling);
            if (trayWidth <= 0 || trayHeight <= 0)
                return;

            if (!TryGetScreenAreas(_region, out var screenBounds, out var workArea))
                return;

            // the window is one shadow reserve deeper than the tray on every side, and every
            // fits-here test asks for the room the WINDOW needs, not the tray. Read off the live
            // margin rather than the option: the reserve is only there while the shadow is.
            var reserve = TrayInsets.FromLogical(Tray.Margin, scaling);

            var minDistance = (int)Math.Ceiling(2 * scaling);
            var maxDistance = (int)Math.Ceiling(15 * scaling);

            var result = TrayPlacement.Near(_region, screenBounds, workArea,
                Math.Max(trayWidth, trayHeight), Math.Min(trayWidth, trayHeight),
                reserve, minDistance, maxDistance, _options.PreferAboveBeforeVertical);

            if (result.Orientation != Orientation)
            {
                SetOrientation(result.Orientation);

                // the rotated tray can have a different short edge (48 vs 48 today, 40 vs 42 before); a second pass re-measures it,
                // exactly as the old code's one immediate + one posted pass did. Bounded to a single
                // extra pass on purpose: a region whose free space falls between the two short edges
                // (e.g. 39 px below, 42 px right, nothing to the left) is picked Vertical when measured
                // at one and Horizontal when measured at the other, so an unbounded self-requeue rotates the
                // strip forever at DispatcherPriority.Loaded (TrayPlacementTests pins that geometry).
                // The second pass may still rotate back, but it never queues a third.
                if (!isSecondPass)
                {
                    _rotatedPassPending = true;
                    QueueReposition();
                }
            }

            Position = new PixelPoint(result.X, result.Y);
        }

        /// <summary>The window's top-left for a fixed-size strip outside the region, or null when
        /// nothing fits — never inside. The scaling is the caller's guess or the real value.</summary>
        private PixelPoint? ComputeOutside(ScreenRect region, double scaling)
        {
            // logical → capture space: physical px on Windows; on macOS the region is in CG
            // points, which ARE logical units, so the factor is 1 even on Retina.
            var toCapture = OperatingSystem.IsMacOS() ? 1.0 : scaling;
            var size = _options.FixedTraySize.Value;
            var w = (int)Math.Ceiling((size.Width + Tray.Margin.Left + Tray.Margin.Right) * toCapture);
            var h = (int)Math.Ceiling((size.Height + Tray.Margin.Top + Tray.Margin.Bottom) * toCapture);
            var gap = (int)Math.Ceiling(GapLogical * toCapture);

            // no screen means no placement we can defend: refuse rather than guess a position that might
            // land in the region. The helper is called bare, as it is from Reposition.
            if (!TryGetScreenAreas(region, out _, out var workArea))
                return null;

            var rect = TrayPlacement.Outside(region, workArea, w, h, gap, TrayInsets.FromLogical(Tray.Margin, toCapture));
            if (rect == null)
                return null;

            return new PixelPoint(rect.Left, rect.Top);
        }

        /// <summary>The monitor under the region's centre (primary as the fallback), as the full bounds
        /// and the placeable area. False when no monitor can be resolved at all.</summary>
        private bool TryGetScreenAreas(ScreenRect region, out ScreenRect screenBounds, out ScreenRect workArea)
        {
            screenBounds = null;
            workArea = null;

            var screen = DesktopScreens.FromPoint(this, new PixelPoint(region.Center.X, region.Center.Y)) ?? DesktopScreens.Primary(this);
            if (screen == null)
                return false;

            var b = screen.Bounds;
            screenBounds = new ScreenRect(b.X, b.Y, b.Width, b.Height);

            // the placeable area: the monitor minus whatever the shell reserves (dock, menu bar,
            // taskbar). Avalonia reports this in the same space as Bounds, so no conversion.
            var w = screen.WorkingArea;
            workArea = w.Width > 0 && w.Height > 0
                ? new ScreenRect(w.X, w.Y, w.Width, w.Height).Intersect(screenBounds)
                : screenBounds;
            if (workArea.IsEmpty())
                workArea = screenBounds;

            return true;
        }

        /// <summary>The one place the strip's axis is written, so everything that hangs off it follows
        /// both the automatic placement cascade and a click on the rotate button.</summary>
        private void SetOrientation(Orientation orientation)
        {
            // the tray pushes the value into every orientable item; the templates restyle themselves.
            Tray.Orientation = orientation;
            AimToolTips();

            // a blip that is already up was aimed at the old axis, and the cascade's rotation is posted:
            // an owner that says something and then leaves the strip to stand itself up would otherwise be
            // left with the message lying across the top of a vertical strip. Re-aim and re-open it — the
            // timer is deliberately untouched, because the six seconds belong to the message rather than
            // to the rotation.
            if (_blip?.IsOpen == true)
            {
                AimBlip();
                _blip.IsOpen = false;
                _blip.IsOpen = true;
            }
        }

        /// <summary>
        /// Anchors every tooltip on the strip to its control, on the strip's free side.
        /// </summary>
        /// <remarks>
        /// Avalonia's default is <see cref="PlacementMode.Pointer"/> with a 20px vertical offset,
        /// which parks the tip's own top-level window just below the cursor. Nudging the mouse
        /// down before clicking — exactly what you do to grab the grip — then puts that popup
        /// under the pointer, so the press lands on the popup rather than on the control.
        /// ToolTipService closes the tip from its raw-input hook on the same button-down, which
        /// tears the PopupRoot down mid-dispatch (Avalonia logs "PlatformImpl is null, couldn't
        /// handle input"), and the click is simply lost: the first drag does nothing and the
        /// second — with no tip open yet — works. Anchoring to the control keeps the tip clear of
        /// both the cursor and the neighbouring tiles, which is why the side follows the
        /// rotation rather than being fixed. The small gap is the spec's, not the cursor-clearing
        /// default, and it is the tip theme's margin rather than an offset here: an offset is applied
        /// in the same direction after the positioner flips a tip that has no room below the strip
        /// to above it, which put the flipped tip 7 px over the strip. Both offsets stay 0.
        /// Every control is aimed whether or not it has a tip yet: a tip assigned after this ran
        /// (an owner filling in a toggle's two tips, say) would otherwise keep the Pointer default and
        /// bring the swallowed first click back. Four idempotent attached-property writes per control
        /// is cheap next to re-walking the tree whenever a tip changes.
        /// </remarks>
        private void AimToolTips()
        {
            var horizontal = Tray.Orientation == Orientation.Horizontal;
            var placement = horizontal ? PlacementMode.Bottom : PlacementMode.Right;

            foreach (var control in Tray.GetVisualDescendants().OfType<Control>())
            {
                ToolTip.SetPlacement(control, placement);
                ToolTip.SetVerticalOffset(control, 0);
                ToolTip.SetHorizontalOffset(control, 0);

                // per control, not once on the tray: ToolTip.ShowDelay does not inherit.
                ToolTip.SetShowDelay(control, TrayTokens.ToolTipShowDelayMs);
            }
        }
    }
}

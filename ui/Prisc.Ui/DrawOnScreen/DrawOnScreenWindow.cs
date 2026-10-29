using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Prisc.Config;
using Prisc.Drawing;
using Prisc.Interop;
using Prisc.Localization;
using Prisc.UI.Helpers;

namespace Prisc.UI.DrawOnScreen
{
    /// <summary>
    /// One screen's draw-on-screen canvas: a borderless, transparent, topmost window covering exactly
    /// one monitor, hosting a <see cref="DrawingCanvas"/> in overlay mode. There is one per monitor
    /// (an Avalonia window has a single RenderScaling, and macOS windows cannot span displays), and
    /// <see cref="DrawOnScreenSession"/> owns the set.
    /// <para>
    /// The window has two input states. Drawing: it takes the mouse like any window, and the canvas
    /// shows the active tool's cursor. Click-through (<see cref="SetClickThrough"/>): the OS routes
    /// the mouse to whatever is beneath, so the ink stays up while the user works. On Windows that
    /// is WS_EX_TRANSPARENT toggled live through <see cref="ToggleableExStyles"/> — never
    /// <see cref="WindowNativeExtensions.AddExStyles"/>, which is one-way, and never a WM_NCHITTEST
    /// hook, whose HTTRANSPARENT only forwards within one thread; on macOS it is
    /// setIgnoresMouseEvents:. On Windows the window is never activated by a click (WS_EX_NOACTIVATE),
    /// so drawing leaves the keyboard with the toolbar and its hotkeys; the one exception, the text
    /// tool, is the session's business. Unlike the recording chrome it is NOT excluded from screen capture: the ink is
    /// meant to appear in screenshots, recordings and shares.
    /// </para>
    /// <para>
    /// A frozen session (Draw on Instant Screenshot) hands its window the monitor's picture, shown
    /// under the canvas rather than added to it as a graphic: the eraser, the select tool, Clear all
    /// and the undo history only ever see the ink. The mouse wheel zooms the picture alone
    /// (<see cref="ZoomBackdropAt"/>), with nearest-neighbour sampling so its pixels stay sharp; the
    /// ink stays where it was drawn on the screen.
    /// </para>
    /// </summary>
    internal sealed class DrawOnScreenWindow : Window
    {
        // the ink's hide/show fade (the session drives Canvas.Opacity)
        private static readonly TimeSpan HideFade = TimeSpan.FromMilliseconds(150);

        // the backdrop's zoom range, and one wheel notch's step within it: the capture overlay's
        // magnifier (prisc_capture's ZOOM_STEP and ZOOM_MAX), so both zoom alike
        private const double MaxBackdropZoom = 256;
        private const double WheelZoomStep = 2;

        private readonly ToggleableExStyles _styles;
        private readonly Bitmap _backdrop;
        private readonly Image _backdropImage;

        // the backdrop's zoom, and where its top-left corner sits as a fraction of the window
        // (always in [1 - zoom, 0], so the picture covers the whole screen)
        private double _zoom = 1;
        private Point _pan;

        /// <param name="backdrop">The monitor's frozen picture, owned (and disposed) by the window from
        /// here on; null to draw over the live desktop.</param>
        public DrawOnScreenWindow(DesktopScreen screen, Func<ToolType, SavedToolSettings> resolver, Bitmap backdrop = null)
        {
            Screen = screen ?? throw new ArgumentNullException(nameof(screen));

            Title = Loc.T("Draw_WindowTitle");
            WindowDecorations = WindowDecorations.None;
            Background = Brushes.Transparent;
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            CanResize = false;
            SizeToContent = SizeToContent.Manual;

            // the ink is what the user wants recorded, so no capture setting hides it
            CaptureExclusion.SetRole(this, CaptureRole.Content);

            // the resolver and the mode go in before the tool: picking the tool binds its settings,
            // and those must come from the session's own table, never the editor's persisted ones
            Canvas = new DrawingCanvas
            {
                IsOverlayMode = true,
                ToolSettingsResolver = resolver,
                HandleColor = AppStyles.AccentColor,
                Tool = ToolType.Brush,
                Transitions = new Transitions
                {
                    new DoubleTransition { Property = OpacityProperty, Duration = HideFade },
                },
            };
            // the keystroke rows ride above the ink as a sibling, outside the canvas: hiding the ink
            // fades the canvas alone, and the keys are never a graphic the user could erase
            Keystrokes = new KeystrokeOverlay();
            var root = new Panel
            {
                Background = Brushes.Transparent,
                Children = { Canvas, Keystrokes },
            };

            // the bitmap is a 96 DPI grab of the monitor's real pixels (ScreenPixels.Grab), so
            // stretching it to fill the window maps it 1:1
            _backdrop = backdrop;
            if (backdrop != null)
            {
                _backdropImage = new Image
                {
                    Source = backdrop,
                    Stretch = Stretch.Fill,
                    RenderTransformOrigin = RelativePoint.TopLeft,
                };
                RenderOptions.SetBitmapInterpolationMode(_backdropImage, BitmapInterpolationMode.None);
                _backdropImage.PropertyChanged += (s, e) =>
                {
                    if (e.Property == BoundsProperty)
                        UpdateBackdropTransform();
                };
                root.Children.Insert(0, _backdropImage);

                // ahead of the canvas, which pins itself 1:1 in overlay mode anyway
                AddHandler(PointerWheelChangedEvent, OnPreviewPointerWheel, RoutingStrategies.Tunnel);
            }
            Content = root;

            // pre-show geometry from the monitor's own scaling as a best guess; Opened re-applies it
            // from the window's actual RenderScaling, the only value that is trustworthy
            SeedGeometry();

            // registered before Show() so the always-on bits are in from the first frame. Only
            // TRANSPARENT ever toggles; LAYERED needs the OpaqueLayered follow-up in Opened.
            _styles = new ToggleableExStyles(this,
                WindowNativeExtensions.WS_EX_LAYERED | WindowNativeExtensions.WS_EX_TOOLWINDOW | WindowNativeExtensions.WS_EX_NOACTIVATE,
                WindowNativeExtensions.WS_EX_TRANSPARENT,
                initiallyOn: false);

            // keyboard defence: no key reaches the canvas, whose own shortcuts are the image editor's.
            // Only the text tool's TextBox may see keys; every other key is the session's hotkeys'
            // (HotkeyDown/HotkeyUp), for while this window has the keyboard.
            AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
            AddHandler(KeyUpEvent, OnPreviewKeyUp, RoutingStrategies.Tunnel);

            // the right button belongs to the session (it drops to click-through); the canvas's own
            // overlay-mode right-press return is only a backstop under this
            AddHandler(PointerPressedEvent, OnPreviewPointerPressed, RoutingStrategies.Tunnel);

            Opened += OnOpened;
            ScalingChanged += OnScalingChanged;
        }

        public DrawingCanvas Canvas { get; }

        /// <summary>The keystroke rows over this screen (blank unless the session points it at its
        /// keys).</summary>
        public KeystrokeOverlay Keystrokes { get; }

        /// <summary>The monitor this window covers (capture-space bounds, see <see cref="DesktopScreens"/>).</summary>
        public DesktopScreen Screen { get; private set; }

        /// <summary>Whether the mouse currently goes through this window to the desktop.</summary>
        public bool ClickThrough { get; private set; }

        /// <summary>A right press anywhere on the window. Marked handled before the canvas sees it.</summary>
        public event EventHandler RightClicked;

        /// <summary>A key edge outside the text tool's edit box, while this window has the keyboard (the
        /// text tool's edit, or on macOS any click, gives it the keyboard). Marked handled before
        /// anything else sees it.</summary>
        public event EventHandler<KeyEventArgs> HotkeyDown, HotkeyUp;

        /// <summary>
        /// Switches the window between taking the mouse and letting it through. Turning
        /// click-through on first ends whatever the canvas is in the middle of (an open text edit is
        /// committed, a half-drawn shape or a live eraser marquee is aborted); overlay mode keeps the
        /// tool, so coming back resumes it with its cursor.
        /// </summary>
        public void SetClickThrough(bool on)
        {
            if (on == ClickThrough)
                return;

            ClickThrough = on;

            if (on)
            {
                Canvas.CommitTextEdit();
                Canvas.CancelCurrentOperation();
            }

            // the styles callback's desire first, so a style re-application keeps the new state,
            // then the live switch on the native window (WS_EX_TRANSPARENT / setIgnoresMouseEvents:)
            _styles.IsOn = on;
            WindowNativeExtensions.SetClickThrough(this, on);

            // belt and braces under the native styles, and it clears any stale hover outline
            Canvas.IsHitTestVisible = !on;

            // a tool picked while the canvas was ignoring the mouse (the brush, the default) never
            // changed Tool, so nothing re-applied its cursor; show it before the first press
            if (!on)
                Canvas.RefreshToolCursor();
        }

        /// <summary>The monitor changed under this window (a scaling or bounds update at hotplug):
        /// re-covers it.</summary>
        public void SetScreen(DesktopScreen screen)
        {
            Screen = screen ?? throw new ArgumentNullException(nameof(screen));
            ApplyGeometry();
            Canvas.UpdateForScalingChange();
        }

        /// <summary>
        /// Zooms the backdrop to <paramref name="zoom"/> (clamped to 1..<see cref="MaxBackdropZoom"/>),
        /// keeping the picture's pixel under <paramref name="screenPoint"/> (in
        /// <see cref="DesktopScreens"/> units, like <see cref="CursorPosition"/>) where it is. A no-op
        /// without a backdrop. Usable before the window is shown.
        /// </summary>
        public void ZoomBackdropAt(double zoom, PixelPoint screenPoint)
        {
            var bounds = Screen.Bounds;
            if (bounds.Width <= 0 || bounds.Height <= 0)
                return;

            ZoomBackdrop(zoom, new Point(
                (double)(screenPoint.X - bounds.X) / bounds.Width,
                (double)(screenPoint.Y - bounds.Y) / bounds.Height));
        }

        /// <param name="anchor">The fixed point, as a fraction of the window.</param>
        private void ZoomBackdrop(double zoom, Point anchor)
        {
            if (_backdropImage == null)
                return;

            zoom = Math.Clamp(zoom, 1, MaxBackdropZoom);
            anchor = new Point(Math.Clamp(anchor.X, 0, 1), Math.Clamp(anchor.Y, 0, 1));

            // the picture's point under the anchor (as a fraction of the picture) stays under it
            var u = (anchor - _pan) / _zoom;
            var pan = anchor - u * zoom;

            _zoom = zoom;
            _pan = new Point(Math.Clamp(pan.X, 1 - zoom, 0), Math.Clamp(pan.Y, 1 - zoom, 0));
            UpdateBackdropTransform();
        }

        private void UpdateBackdropTransform()
        {
            var size = _backdropImage.Bounds.Size;
            _backdropImage.RenderTransform = new MatrixTransform(
                Matrix.CreateScale(_zoom, _zoom) * Matrix.CreateTranslation(_pan.X * size.Width, _pan.Y * size.Height));
        }

        private void OnPreviewPointerWheel(object sender, PointerWheelEventArgs e)
        {
            e.Handled = true;

            var size = _backdropImage.Bounds.Size;
            if (size.Width <= 0 || size.Height <= 0)
                return;

            // a wheel notch (a delta of 1) is one step; the fractions touchpads and macOS mice send
            // zoom smoothly in proportion, so a notch's worth of them is one step too
            var factor = Math.Pow(WheelZoomStep, e.Delta.Y);
            var p = e.GetPosition(this);
            ZoomBackdrop(_zoom * factor, new Point(p.X / size.Width, p.Y / size.Height));
        }

        /// <summary>
        /// Only a programmatic Close() (the session's, or App.CloseAllWindows at exit) may close a
        /// canvas: Alt+F4 on one monitor's window would otherwise tear a hole in the session while
        /// the toolbar and the other screens carry on.
        /// </summary>
        protected override void OnClosing(WindowClosingEventArgs e)
        {
            if (!e.IsProgrammatic)
                e.Cancel = true;

            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            _backdrop?.Dispose();
        }

        private void OnOpened(object sender, EventArgs e)
        {
            // order matters: the overlay setup before positioning (AppKit constrains a window below
            // the overlay level away from the menu bar, so a frame set earlier would not stick) with
            // the layered attributes (a layered window without them is never repainted), then the
            // current input state as an explicit choice either way (on macOS a window drawing in its
            // transparent pixels must say NO: AppKit's never-set default lets those clicks through),
            // then the real geometry.
            WindowNativeExtensions.PrepareOverlay(this, OverlayFlags.CoverMenuBar | OverlayFlags.OpaqueLayered);
            WindowNativeExtensions.SetClickThrough(this, ClickThrough);
            ApplyGeometry();
        }

        private void OnScalingChanged(object sender, EventArgs e)
        {
            ApplyGeometry();
            Canvas.UpdateForScalingChange();

            // Avalonia.Win32 raises ScalingChanged inline from WM_DPICHANGED and only then applies
            // Windows' suggested rect (the old rect scaled by newDpi/oldDpi), overwriting the bounds
            // just set. Re-cover the monitor once that handler has returned.
            Dispatcher.UIThread.Post(() =>
            {
                if (IsVisible)
                {
                    ApplyGeometry();
                    Canvas.UpdateForScalingChange();
                }
            }, DispatcherPriority.Send);
        }

        /// <summary>Best-effort placement before the native window exists: the monitor's own
        /// scaling stands in for RenderScaling. CG points are already logical on macOS.</summary>
        private void SeedGeometry()
        {
            var scaling = OperatingSystem.IsMacOS() ? 1.0 : Screen.Scaling;
            Position = Screen.Bounds.Position;
            Width = Screen.Bounds.Width / scaling;
            Height = Screen.Bounds.Height / scaling;
        }

        /// <summary>Covers <see cref="Screen"/> exactly, in one native call (see
        /// <see cref="WindowNativeExtensions.SetPhysicalBounds"/>).</summary>
        private void ApplyGeometry()
        {
            var scaling = OperatingSystem.IsMacOS() ? 1.0 : RenderScaling;
            WindowNativeExtensions.SetPhysicalBounds(this, Screen.Bounds.Position, Screen.Bounds.Width, Screen.Bounds.Height, scaling);

            // the working area in the window's own DIPs: the window's origin is the monitor's
            var work = Screen.WorkingArea;
            Keystrokes.SetWorkArea(new Rect(
                (work.X - Screen.Bounds.X) / scaling, (work.Y - Screen.Bounds.Y) / scaling,
                work.Width / scaling, work.Height / scaling));
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (IsForTextBox(e))
                return;

            e.Handled = true;
            HotkeyDown?.Invoke(this, e);
        }

        private void OnPreviewKeyUp(object sender, KeyEventArgs e)
        {
            if (IsForTextBox(e))
                return;

            e.Handled = true;
            HotkeyUp?.Invoke(this, e);
        }

        // the text tool's in-place TextBox is the one place keys are meant to land
        private static bool IsForTextBox(KeyEventArgs e) =>
            e.Source is Visual source && source.FindAncestorOfType<TextBox>(includeSelf: true) != null;

        private void OnPreviewPointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
                return;

            e.Handled = true;
            RightClicked?.Invoke(this, EventArgs.Empty);
        }
    }
}

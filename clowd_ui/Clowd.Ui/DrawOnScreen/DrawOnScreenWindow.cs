using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Clowd.Config;
using Clowd.Drawing;
using Clowd.Localization;
using Clowd.UI.Helpers;

namespace Clowd.UI.DrawOnScreen
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
    /// setIgnoresMouseEvents:. The window is never activated by a click (WS_EX_NOACTIVATE), so the
    /// user's foreground app keeps the keyboard; the one exception, the text tool, is the session's
    /// business. Unlike the recording chrome it is NOT excluded from screen capture: the ink is
    /// meant to appear in screenshots, recordings and shares.
    /// </para>
    /// </summary>
    internal sealed class DrawOnScreenWindow : Window
    {
        // the ink's hide/show fade (the session drives Canvas.Opacity)
        private static readonly TimeSpan HideFade = TimeSpan.FromMilliseconds(150);

        private readonly ToggleableExStyles _styles;

        public DrawOnScreenWindow(DesktopScreen screen, Func<ToolType, SavedToolSettings> resolver)
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
            Content = new Panel
            {
                Background = Brushes.Transparent,
                Children = { Canvas },
            };

            // pre-show geometry from the monitor's own scaling as a best guess; Opened re-applies it
            // from the window's actual RenderScaling, the only value that is trustworthy
            SeedGeometry();

            // registered before Show() so the always-on bits are in from the first frame. Only
            // TRANSPARENT ever toggles; LAYERED needs the SetLayeredFullyOpaque follow-up in Opened.
            _styles = new ToggleableExStyles(this,
                WindowNativeExtensions.WS_EX_LAYERED | WindowNativeExtensions.WS_EX_TOOLWINDOW | WindowNativeExtensions.WS_EX_NOACTIVATE,
                WindowNativeExtensions.WS_EX_TRANSPARENT,
                initiallyOn: false);

            // keyboard defence: this window has no shortcuts, and a key that reached the canvas
            // while the user thinks they are typing into the app underneath would be a surprise.
            // Only the text tool's TextBox may see keys.
            AddHandler(KeyDownEvent, OnPreviewKey, RoutingStrategies.Tunnel);
            AddHandler(KeyUpEvent, OnPreviewKey, RoutingStrategies.Tunnel);

            // the right button belongs to the session (it drops to click-through); the canvas's own
            // overlay-mode right-press return is only a backstop under this
            AddHandler(PointerPressedEvent, OnPreviewPointerPressed, RoutingStrategies.Tunnel);

            Opened += OnOpened;
            ScalingChanged += OnScalingChanged;
        }

        public DrawingCanvas Canvas { get; }

        /// <summary>The monitor this window covers (capture-space bounds, see <see cref="DesktopScreens"/>).</summary>
        public DesktopScreen Screen { get; private set; }

        /// <summary>Whether the mouse currently goes through this window to the desktop.</summary>
        public bool ClickThrough { get; private set; }

        /// <summary>A right press anywhere on the window. Marked handled before the canvas sees it.</summary>
        public event EventHandler RightClicked;

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

            _styles.Set(on);
            WindowNativeExtensions.SetIgnoresMouseEvents(this, on);

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

        private void OnOpened(object sender, EventArgs e)
        {
            // order matters: the level lift before positioning (AppKit constrains a window below
            // NSStatusWindowLevel away from the menu bar, so a frame set earlier would not stick),
            // the layered attributes right away (a layered window without them is never repainted),
            // an explicit mouse-events state (AppKit's default is a third state the window would
            // otherwise never leave once YES was sent), then the real geometry.
            WindowNativeExtensions.SetCanCoverMenuBar(this);
            WindowNativeExtensions.SetLayeredFullyOpaque(this);
            WindowNativeExtensions.SetIgnoresMouseEvents(this, ClickThrough);
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
        }

        private static void OnPreviewKey(object sender, KeyEventArgs e)
        {
            // the text tool's in-place TextBox is the one place keys are meant to land
            if (e.Source is Visual source && source.FindAncestorOfType<TextBox>(includeSelf: true) != null)
                return;

            e.Handled = true;
        }

        private void OnPreviewPointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
                return;

            e.Handled = true;
            RightClicked?.Invoke(this, EventArgs.Empty);
        }
    }
}

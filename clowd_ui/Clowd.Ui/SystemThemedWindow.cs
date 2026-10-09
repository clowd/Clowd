using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Clowd.Config;
using Clowd.UI.Helpers;

namespace Clowd.UI
{
    /// <summary>
    /// Window base class for all Clowd shell windows. Replaces the WPF SystemThemedWindow /
    /// CustomUiWindow style combination: app icon, default sizing, dynamic theme background
    /// and a system-backdrop transparency hint. Dark titlebar / WM hooks are not ported —
    /// Avalonia follows the system theme via SemiTheme + RequestedThemeVariant=Default.
    /// </summary>
    public class SystemThemedWindow : Window
    {
        public SystemThemedWindow() : this(applyDefaultSize: true)
        { }

        /// <param name="applyDefaultSize">Pass false from windows that size themselves
        /// (e.g. SizeToContent dialogs) — the fixed defaults would defeat auto-sizing.</param>
        protected SystemThemedWindow(bool applyDefaultSize)
        {
            Icon = AppStyles.AppIcon;

            // Defaults previously applied by the WPF "CustomUiWindow" style (decision table #70).
            if (applyDefaultSize)
            {
                Width = 1100;
                Height = 600;
                MinWidth = 460;
                MinHeight = 100;
            }

            FontSize = 14; // Semi's base size; keeps generated pages in step with the Body class

            // A window that does not extend still has to resolve the gutter keys, so register them
            // first and let the platform branch below raise them. Collapsed, the spacers bound to
            // them take no space and every layout is the one it was before any of this existed.
            Resources["MacTitleBarGutterHorz"] = 0d;
            Resources["MacTitleBarGutterVert"] = 0d;

            // The outer test is the user's intent, the inner one is what this platform can honour.
            // They are separate because the intent is platform-neutral and the means are not: if
            // Windows ever grows a branch here it wants its own hints and its own gutters, since
            // its caption buttons sit on the trailing edge rather than the leading one.
            //
            // Read once, here, so a window's look is fixed for its lifetime: the hint is a
            // construction-time decision for the backend, and the drag handler below is subscribed
            // once. Toggling the setting therefore governs windows opened after it, which is what
            // its description promises.
            if (ExtendIntoTitleBar)
            {
                if (OperatingSystem.IsMacOS())
                {
                    // Runs the window content up under a transparent titlebar (NSWindow gains
                    // FullSizeContentView), which is what a Tahoe-era mac window looks like; the
                    // traffic lights stay where AppKit puts them and float over the content. -1
                    // keeps the system titlebar height.
                    ExtendClientAreaToDecorationsHint = true;
                    ExtendClientAreaTitleBarHeightHint = -1;

                    // Room a window's own content yields to the traffic lights, in whichever axis
                    // it gets out of their way.
                    //
                    // Horz: for a window whose top row is a toolbar and so shares the strip with
                    // them. Clears the buttons (three 14pt on 20pt centres, the first centred
                    // 15.8pt in, so the zoom button's right edge lands at ~63pt) and then keeps
                    // going, the surplus being what leaves bare strip to drag the window by even
                    // when the bar is packed.
                    //
                    // Vert: for a window that steps its top-left content down past them instead,
                    // leaving the strip to the buttons alone.
                    Resources["MacTitleBarGutterHorz"] = 105d;
                    Resources["MacTitleBarGutterVert"] = 28d;
                }

                // No Windows branch yet, and the row is hidden there (GeneralSettingsPage), so the
                // setting cannot be turned on without one. Building it means more than flipping the
                // hint: extending the client area on Win32 hands Avalonia the drag region,
                // double-click-to-maximize and the Win11 snap layouts flyout.
            }

            ActualThemeVariantChanged += (_, _) => UpdateBackdrop();
            UpdateBackdrop();

            // macOS Cmd+W (issue #73). Registered on the base so every shell window gets it;
            // the recording/scroll overlays are deliberately not shell windows and keep their
            // own (Escape-driven) cancel semantics.
            MacWindowShortcuts.AddCloseShortcut(this);
        }

        /// <summary>
        /// Makes the empty space of <paramref name="region"/> drag the window, and a double-click
        /// there maximize or restore it — what the title bar itself does.
        ///
        /// Every platform, not just the extended-client-area one it was written for: on macOS this
        /// bar IS the title bar and has to drag, and on Windows it sits right under a real one and
        /// reads as part of the same strip, so a drag that dies on the seam between them is a
        /// surprise either way.
        ///
        /// The region needs a non-null Background (Transparent is enough) to be hit-testable at
        /// all; only presses that report the region itself as their source count, since every
        /// control in such a bar reports itself, while a background-less panel is not hit-testable
        /// and so its padding falls through and correctly reads as empty.
        /// </summary>
        protected void EnableTitleBarDrag(Control region)
        {
            if (region == null)
                return;

            region.PointerPressed += (_, e) =>
            {
                if (!ReferenceEquals(e.Source, region))
                    return;

                if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                    return;

                // A double-click never starts a drag: BeginMoveDrag would swallow the second
                // press, and the gesture people expect from a title bar is maximize/restore.
                if (e.ClickCount == 2)
                {
                    if (CanResize)
                        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

                    e.Handled = true;
                    return;
                }

                BeginMoveDrag(e);
            };
        }

        /// <summary>The traffic lights' own diameter, which no API changes: AppKit draws the circle
        /// at its own size whatever frame the button carries, so centring is a matter of where that
        /// circle goes, not how big it is. 14pt is the macOS 26 chrome, which is what ships (CI
        /// stamps the apphost's linked-SDK field; see the workflow). A local build without that
        /// stamp gets the legacy 12pt circle in a 14x16 frame and so centres about a point low.</summary>
        private const double MacTrafficLightDiameter = 14;

        /// <summary>How far the close button sits from the TOOL BAR's own left edge, rather than
        /// from the window's. The two chromes start their bar in different places (modern floats it
        /// 4pt in, compact runs it flush to the edge), and the lights belong with the bar: 16pt into
        /// the window in modern, 12 in compact, which is where each one's first control is.</summary>
        private const double MacTrafficLightGutter = 12;

        /// <summary>Bare strip kept to the right of the lights, for dragging the window by when the
        /// bar is packed. Spans the cluster (14pt plus two 23pt steps) and then some.</summary>
        private const double MacTrafficLightClearance = 96;

        /// <summary>
        /// macOS: insets the traffic lights and centres them vertically in <paramref name="strip"/>,
        /// which is this window's own top bar. For a window whose first row is a tool bar taller
        /// than the 32pt title bar, the lights otherwise sit high and left of its first control
        /// instead of in line with it.
        /// </summary>
        /// <remarks>
        /// Call alongside <see cref="EnableTitleBarDrag"/>, with the same bar. The placement is
        /// re-asserted rather than set once, because AppKit resets the three buttons to (9, 9) on
        /// every title bar relayout: a resize, a zoom, a minimise and restore, and both fullscreen
        /// transitions. Each of those reaches Avalonia as one of the events subscribed below, so no
        /// NSNotification observer is needed; the posted second pass covers the relayout that AppKit
        /// performs after the event rather than before it.
        /// <para>
        /// The bar's own height and offset are read from its layout rather than assumed, so the two
        /// editor chromes (compact's 30pt rows, modern's taller floating bars) each centre in what
        /// they actually drew, and a live chrome switch re-centres with them.
        /// </para>
        /// </remarks>
        protected void CenterMacTrafficLightsIn(Control strip)
        {
            if (!OperatingSystem.IsMacOS() || !ExtendIntoTitleBar || strip == null)
                return;

            void Apply()
            {
                var row = FirstRowHeight(strip);
                if (row <= 0)
                    return;

                // Where the bar actually is, margins included, so the lights follow the bar rather
                // than the window edge and each chrome gets its own placement for free. Its top,
                // not its middle, so a bar that has wrapped does not drag them down with it.
                var barOrigin = strip.TranslatePoint(default, this) ?? default;
                var left = barOrigin.X + MacTrafficLightGutter;

                // Fullscreen is AppKit's: it takes the three buttons into the title bar that slides
                // down from the top of the SCREEN, and draws nothing in this window's own bar. A
                // spacer held there would be clearing something that is never painted, so the bar
                // reclaims the width and its first control starts at the edge. Positioning is
                // skipped for the same reason: those buttons are not ours to place while they
                // belong to the overlay.
                var fullScreen = WindowState == WindowState.FullScreen;

                // The spacer that clears them moves with them. Written only on a real change: the
                // resource drives a Border's width, so an unconditional write would relayout the
                // bar, which calls straight back in here.
                var gutter = fullScreen ? 0d : left + MacTrafficLightClearance;
                if (Resources["MacTitleBarGutterHorz"] is not double current || Math.Abs(current - gutter) > 0.5)
                    Resources["MacTitleBarGutterHorz"] = gutter;

                if (fullScreen)
                    return;

                WindowNativeExtensions.SetTrafficLightPosition(
                    this, left, barOrigin.Y + (row - MacTrafficLightDiameter) / 2);
            }

            void Reapply()
            {
                Apply();
                Dispatcher.UIThread.Post(Apply, DispatcherPriority.Background);
            }

            Opened += (_, _) => Reapply();
            Resized += (_, _) => Reapply();

            // A window that becomes key re-draws its title bar, and a state change (minimise,
            // zoom, fullscreen) rebuilds it outright.
            Activated += (_, _) => Reapply();
            PropertyChanged += (_, e) =>
            {
                if (e.Property == WindowStateProperty)
                    Reapply();
            };

            // The bar's own geometry moving (a chrome switch, a row wrapping) is a reposition too,
            // and one that no window-level event reports.
            strip.PropertyChanged += (_, e) =>
            {
                if (e.Property == BoundsProperty)
                    Apply();
            };
        }

        /// <summary>
        /// The height of the bar's FIRST row, which is the one the traffic lights belong beside. A
        /// tool bar that runs out of width wraps into a second row and doubles its own height; the
        /// lights must not follow it down, since the row they sit in has not moved.
        /// </summary>
        /// <remarks>Both editors' bars state an ItemHeight, which IS the row height they lay out
        /// on. The fallbacks are for a bar that does not: the tallest child still sitting at the
        /// panel's top edge, and failing that the panel itself (correct whenever it has not
        /// wrapped).</remarks>
        private static double FirstRowHeight(Control strip)
        {
            if (strip is Clowd.UI.Controls.DockAndWrapPanel panel
                && !Double.IsNaN(panel.ItemHeight) && panel.ItemHeight > 0)
                return panel.ItemHeight;

            if (strip is Panel children)
            {
                double row = 0;
                foreach (var child in children.Children)
                {
                    if (child.Bounds.Y < 1 && child.Bounds.Height > row)
                        row = child.Bounds.Height;
                }

                if (row > 0)
                    return row;
            }

            return strip.Bounds.Height;
        }

        /// <summary>
        /// Whether the user wants windows to run their content under a transparent title bar.
        /// Platform-neutral: this is the intent, not the means, so a platform that cannot honour it
        /// checks for itself. Null-safe on purpose: this is read from a base window constructor,
        /// where a missing settings file must not be able to take every window in the app down.
        /// </summary>
        internal static bool ExtendIntoTitleBar => SettingsRoot.Current?.General?.ExtendIntoTitleBar ?? true;

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == ActualTransparencyLevelProperty)
                UpdateBackground();
        }

        /// <summary>
        /// Whether this window wants Mica in the LIGHT theme as well as the dark one. Off by
        /// default: over a light backdrop the composited surface costs text its subpixel
        /// antialiasing, which the shell's dense pages (settings rows, the recent list) cannot
        /// afford. A window whose content is chrome rather than prose — the editors, the colour
        /// picker — can, and says so by overriding this. Read from the base constructor, so an
        /// override must return a constant rather than read a field of its own.
        /// </summary>
        protected virtual bool AllowMicaInLightTheme => false;

        /// <summary>
        /// Mica (Win11) in the dark theme, and in the light theme for windows that opt in via
        /// <see cref="AllowMicaInLightTheme"/>. There is no acrylic fallback: compositors that do
        /// not grant Mica get the opaque brush.
        /// </summary>
        private void UpdateBackdrop()
        {
            // a window's own ActualThemeVariant is not settled until it is attached; the app's is
            var variant = ActualThemeVariant ?? Application.Current?.ActualThemeVariant;
            var mica = variant == ThemeVariant.Dark || AllowMicaInLightTheme;

            TransparencyLevelHint = mica
                ? new[] { WindowTransparencyLevel.Mica, WindowTransparencyLevel.None }
                : new[] { WindowTransparencyLevel.None };

            UpdateBackground();
        }

        /// <summary>How much of the theme background is washed over Mica in the light theme. The
        /// light backdrop is far busier than the dark one — it picks up the wallpaper rather than
        /// merely darkening it — so the windows that opt in take it as a tint rather than neat.
        /// The dark theme keeps Mica unveiled, which is what it has always looked like.</summary>
        private const double LightMicaVeilOpacity = 0.65;

        private void UpdateBackground()
        {
            // Mica is subtle enough in the dark theme to sit directly behind the content, so it
            // gets no background of its own; in the light theme it takes a veil of the theme
            // colour. Anything else paints the opaque theme brush (Light #FAFAFA / Dark #202020
            // theme dictionaries in Assets/AppResources.axaml).
            if (ActualTransparencyLevel == WindowTransparencyLevel.Mica)
            {
                Background = ActualThemeVariant == ThemeVariant.Light
                    ? VeilBrush() ?? Brushes.Transparent
                    : Brushes.Transparent;
                return;
            }

            if (this.TryFindResource("ApplicationBackgroundBrush", ActualThemeVariant, out var value) && value is IBrush brush)
                Background = brush;
        }

        private IBrush VeilBrush()
        {
            if (this.TryFindResource("ApplicationBackgroundColor", ActualThemeVariant, out var value) && value is Color color)
                return new SolidColorBrush(color, LightMicaVeilOpacity);

            return null;
        }
    }
}

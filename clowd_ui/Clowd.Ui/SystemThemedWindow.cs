using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
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

                    _clientAreaExtended = true;
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
        /// Makes the empty space of <paramref name="region"/> drag the window, for the bar a
        /// window runs under the extended client area. No-op off macOS, where the window keeps a
        /// real title bar that already drags itself. The region needs a non-null Background
        /// (Transparent is enough) to be hit-testable at all; only presses that report the region
        /// itself as their source count, since every control in such a bar reports itself, while
        /// a background-less panel is not hit-testable and so its padding falls through and
        /// correctly reads as empty.
        /// </summary>
        protected void EnableTitleBarDrag(Control region)
        {
            if (!_clientAreaExtended || region == null)
                return;

            region.PointerPressed += (_, e) =>
            {
                if (!ReferenceEquals(e.Source, region))
                    return;

                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                    BeginMoveDrag(e);
            };
        }

        /// <summary>
        /// Whether the user wants windows to run their content under a transparent title bar.
        /// Platform-neutral: this is the intent, not the means, so a platform that cannot honour it
        /// checks for itself. Null-safe on purpose: this is read from a base window constructor,
        /// where a missing settings file must not be able to take every window in the app down.
        /// </summary>
        internal static bool ExtendIntoTitleBar => SettingsRoot.Current?.General?.ExtendIntoTitleBar ?? true;

        /// <summary>
        /// Whether this window actually got an extended client area, as opposed to merely being
        /// asked for one. Fixed at construction, and the thing to test before assuming the window
        /// has no title bar of its own.
        /// </summary>
        private readonly bool _clientAreaExtended;

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == ActualTransparencyLevelProperty)
                UpdateBackground();
        }

        /// <summary>
        /// Mica (Win11) in the dark theme only. In the light theme the composited backdrop makes
        /// the text look smeared and low-contrast (ClearType has no opaque surface to blend
        /// against), so light windows are plain opaque windows with no effect at all. There is
        /// no acrylic fallback either: compositors that do not grant Mica get the opaque brush.
        /// </summary>
        private void UpdateBackdrop()
        {
            // a window's own ActualThemeVariant is not settled until it is attached; the app's is
            var variant = ActualThemeVariant ?? Application.Current?.ActualThemeVariant;
            var dark = variant == ThemeVariant.Dark;

            TransparencyLevelHint = dark
                ? new[] { WindowTransparencyLevel.Mica, WindowTransparencyLevel.None }
                : new[] { WindowTransparencyLevel.None };

            UpdateBackground();
        }

        private void UpdateBackground()
        {
            // Mica is subtle enough to sit directly behind the content, so it gets no background
            // of its own. Anything else paints the opaque theme brush (Light #FAFAFA / Dark
            // #202020 theme dictionaries in Assets/AppResources.axaml).
            if (ActualTransparencyLevel == WindowTransparencyLevel.Mica)
            {
                Background = Brushes.Transparent;
                return;
            }

            if (this.TryFindResource("ApplicationBackgroundBrush", ActualThemeVariant, out var value) && value is IBrush brush)
                Background = brush;
        }
    }
}

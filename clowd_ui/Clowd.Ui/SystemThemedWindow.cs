using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
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

            ActualThemeVariantChanged += (_, _) => UpdateBackdrop();
            UpdateBackdrop();

            // macOS Cmd+W (issue #73). Registered on the base so every shell window gets it;
            // the recording/scroll overlays are deliberately not shell windows and keep their
            // own (Escape-driven) cancel semantics.
            MacWindowShortcuts.AddCloseShortcut(this);
        }

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

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
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

            // Decision table #45: prefer Mica (Win11), fall back to acrylic blur-behind
            // (Win10 / macOS vibrancy). The background brush is chosen per granted level in
            // UpdateBackgroundForTransparency; compositors that grant neither get the opaque brush.
            TransparencyLevelHint = new[]
            {
                WindowTransparencyLevel.Mica,
                WindowTransparencyLevel.AcrylicBlur,
                WindowTransparencyLevel.None,
            };

            ActualThemeVariantChanged += (_, _) => UpdateBackgroundForTransparency();
            UpdateBackgroundForTransparency();

            // macOS Cmd+W (issue #73). Registered on the base so every shell window gets it;
            // the recording/scroll overlays are deliberately not shell windows and keep their
            // own (Escape-driven) cancel semantics.
            MacWindowShortcuts.AddCloseShortcut(this);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == ActualTransparencyLevelProperty)
                UpdateBackgroundForTransparency();
        }

        private void UpdateBackgroundForTransparency()
        {
            // Mica is subtle enough to sit directly behind the content; acrylic blur is much
            // busier, so it gets an 80% wash (Light #FAFAFA / Dark #202020 theme dictionaries
            // in Assets/AppResources.axaml). Anything else falls back to the opaque brush.
            if (ActualTransparencyLevel == WindowTransparencyLevel.Mica)
            {
                Background = Brushes.Transparent;
                return;
            }

            var key = ActualTransparencyLevel == WindowTransparencyLevel.AcrylicBlur
                ? "ApplicationBackgroundAcrylicBrush"
                : "ApplicationBackgroundBrush";
            if (this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush)
                Background = brush;
        }
    }
}

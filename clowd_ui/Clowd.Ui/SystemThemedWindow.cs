using System;
using Avalonia;
using Avalonia.Controls;

namespace Clowd.UI
{
    /// <summary>
    /// Window base class for all Clowd shell windows. Replaces the WPF SystemThemedWindow /
    /// CustomUiWindow style combination: app icon, default sizing, dynamic theme background
    /// and (on Windows) a Mica transparency hint. Dark titlebar / WM hooks are not ported —
    /// Avalonia follows the system theme via FluentTheme + RequestedThemeVariant=Default.
    /// </summary>
    public class SystemThemedWindow : Window
    {
        public SystemThemedWindow()
        {
            Icon = AppStyles.AppIcon;

            // Defaults previously applied by the WPF "CustomUiWindow" style (decision table #70).
            Width = 1100;
            Height = 600;
            MinWidth = 460;
            MinHeight = 100;
            FontSize = 13;

            // Light #FAFAFA / Dark #202020 (Assets/AppResources.axaml theme dictionaries).
            this.Bind(BackgroundProperty, this.GetResourceObservable("ApplicationBackgroundBrush"));

            // Decision table #45: Mica hint on Windows only (falls back to acrylic, then opaque).
            if (OperatingSystem.IsWindows())
            {
                TransparencyLevelHint = new[]
                {
                    WindowTransparencyLevel.Mica,
                    WindowTransparencyLevel.AcrylicBlur,
                    WindowTransparencyLevel.None,
                };
            }
        }
    }
}

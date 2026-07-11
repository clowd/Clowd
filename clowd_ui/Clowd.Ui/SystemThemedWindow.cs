using System;
using Avalonia;
using Avalonia.Controls;

namespace Clowd.UI
{
    /// <summary>
    /// Window base class for all Clowd shell windows. Replaces the WPF SystemThemedWindow /
    /// CustomUiWindow style combination: app icon, default sizing, dynamic theme background
    /// and (on Windows) a Mica transparency hint. Dark titlebar / WM hooks are not ported —
    /// Avalonia follows the system theme via SemiTheme + RequestedThemeVariant=Default.
    /// </summary>
    public class SystemThemedWindow : Window
    {
        private IDisposable _backgroundBinding;

        public SystemThemedWindow()
        {
            Icon = AppStyles.AppIcon;

            // Defaults previously applied by the WPF "CustomUiWindow" style (decision table #70).
            Width = 1100;
            Height = 600;
            MinWidth = 460;
            MinHeight = 100;
            FontSize = 14; // Semi's base size; keeps generated pages in step with the Body class

            // Decision table #45: Mica hint on Windows only. The opaque background would paint
            // over the backdrop, so the translucent variant is used only while the compositor
            // actually grants Mica (Win10 / remoting fall back to the opaque brush).
            if (OperatingSystem.IsWindows())
            {
                TransparencyLevelHint = new[]
                {
                    WindowTransparencyLevel.Mica,
                    WindowTransparencyLevel.None,
                };
            }

            UpdateBackgroundForTransparency();
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == ActualTransparencyLevelProperty)
                UpdateBackgroundForTransparency();
        }

        private void UpdateBackgroundForTransparency()
        {
            // Light #FAFAFA / Dark #202020 (Assets/AppResources.axaml theme dictionaries).
            var key = ActualTransparencyLevel == WindowTransparencyLevel.Mica
                ? "ApplicationBackgroundMicaBrush"
                : "ApplicationBackgroundBrush";
            _backgroundBinding?.Dispose();
            _backgroundBinding = this.Bind(BackgroundProperty, this.GetResourceObservable(key));
        }
    }
}

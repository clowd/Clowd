using System;
using System.Windows;
using System.Windows.Interop;
using Clowd.PlatformUtil.Windows;
using ModernWpf;

namespace Clowd.UI
{
    public class SystemThemedWindow : InteropWindow
    {
        private bool? _isDark;
        public SystemThemedWindow()
        {
            ThemeManager.SetIsThemeAware(this, true);
            ThemeManager.Current.ActualApplicationThemeChanged += ThemeChanged;
            this.SetResourceReference(Window.BackgroundProperty, "SystemControlPageBackgroundAltHighBrush");
            this.SetResourceReference(Window.ForegroundProperty, "SystemControlPageTextBaseHighBrush");
            this.SourceInitialized += SystemThemedWindow_SourceInitialized;
        }

        private void ThemeChanged(ThemeManager sender, object args)
        {
            var hWnd = new WindowInteropHelper(this).Handle;
            UpdateDarkModeState(hWnd);
        }

        private void SystemThemedWindow_SourceInitialized(object sender, EventArgs e)
        {
            var hWnd = new WindowInteropHelper(this).Handle;
            UpdateDarkModeState(hWnd);
        }

        protected override IntPtr WndProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == (int)Vanara.PInvoke.User32.WindowMessage.WM_SETTINGCHANGE)
                UpdateDarkModeState(hWnd);
            return base.WndProc(hWnd, msg, wParam, lParam, ref handled);
        }

        protected virtual void UpdateDarkModeState(IntPtr handle)
        {
            Icon = AppStyles.AppIconWpf;
            bool shouldBeDark = AppStyles.IsDarkTheme;
            if (shouldBeDark && _isDark != true)
            {
                DarkMode.UseImmersiveDarkMode(handle, true);
                _isDark = true;
            }
            else if (!shouldBeDark && _isDark != false)
            {
                DarkMode.UseImmersiveDarkMode(handle, false);
                _isDark = false;
            }
        }
    }
}

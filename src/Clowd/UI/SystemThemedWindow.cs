using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using Clowd.PlatformUtil.Windows;

namespace Clowd.UI
{
    public class SystemThemedWindow : InteropWindow
    {
        private bool? _isDark;
        public SystemThemedWindow()
        {
            ModernWpf.ThemeManager.SetIsThemeAware(this, true);
            this.SetResourceReference(Window.BackgroundProperty, "SystemControlPageBackgroundAltHighBrush");
            this.SetResourceReference(Window.ForegroundProperty, "SystemControlPageTextBaseHighBrush");
            this.SourceInitialized += SystemThemedWindow_SourceInitialized;
        }

        private void SystemThemedWindow_SourceInitialized(object sender, EventArgs e)
        {
            var hWnd = new WindowInteropHelper(this).Handle;
            UpdateDarkModeState(hWnd);
        }

        protected override IntPtr WndProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == (int)WindowMessage.WM_SETTINGCHANGE)
                UpdateDarkModeState(hWnd);

            return base.WndProc(hWnd, msg, wParam, lParam, ref handled);
        }

        protected virtual void UpdateDarkModeState(IntPtr handle)
        {
            var systemIsDark = DarkMode.IsDarkModeEnabled();
            if (systemIsDark && _isDark != true)
            {
                DarkMode.UseImmersiveDarkMode(handle, true);
                _isDark = true;
            }
            else if (!systemIsDark && _isDark != false)
            {
                DarkMode.UseImmersiveDarkMode(handle, false);
                _isDark = false;
            }
        }
    }
}

using Clowd.PlatformUtil.Windows;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Clowd.Dialogs
{
    public class ThemedWindow : Window
    {
        public static readonly ResourceDictionary ThemeBase = new ResourceDictionary()
        {
            Source = new Uri("/Clowd.Dialogs;component/Themes/ThemeBase.xaml", UriKind.RelativeOrAbsolute)
        };

        public static readonly ResourceDictionary ThemeDark = new ResourceDictionary()
        {
            Source = new Uri("/Clowd.Dialogs;component/Themes/ThemeDark.xaml", UriKind.RelativeOrAbsolute)
        };

        private bool? _isDark;

        public ThemedWindow()
        {
            Resources.MergedDictionaries.Add(ThemeBase);
            this.Style = (Style)ThemeBase["DefaultWindowStyle"];
        }

        protected virtual IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == (int)WindowMessage.WM_SETTINGCHANGE)
                UpdateDarkModeState(hwnd);
            return IntPtr.Zero;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            var handle = new WindowInteropHelper(this).EnsureHandle();
            UpdateDarkModeState(handle);
            HwndSource source = HwndSource.FromHwnd(handle);
            source.AddHook(new HwndSourceHook(WndProc));
            base.OnSourceInitialized(e);
        }

        protected virtual void UpdateDarkModeState(IntPtr handle)
        {
            var systemIsDark = DarkMode.IsDarkModeEnabled();
            if (systemIsDark && _isDark != true)
            {
                DarkMode.UseImmersiveDarkMode(handle, true);
                Resources.MergedDictionaries.Add(ThemeDark);
                _isDark = true;
            }
            else if (!systemIsDark && _isDark != false)
            {
                DarkMode.UseImmersiveDarkMode(handle, false);
                if (Resources.MergedDictionaries.Contains(ThemeDark))
                    Resources.MergedDictionaries.Remove(ThemeDark);
                _isDark = false;
            }
        }
    }
}

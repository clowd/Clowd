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

            if (msg == (int)WindowMessage.WM_DWMCOLORIZATIONCOLORCHANGED)
                UpdateDwmAccentColor(hwnd);

            return IntPtr.Zero;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            var handle = new WindowInteropHelper(this).EnsureHandle();
            UpdateDarkModeState(handle);
            UpdateDwmAccentColor(handle);
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

        protected virtual void UpdateDwmAccentColor(IntPtr handle)
        {
            var systemIsDark = DarkMode.IsDarkModeEnabled();
            var areo = AreoColor.GetColor();

            void SetColor(string name, Color clr)
            {
                var brush = new SolidColorBrush(clr);
                brush.Freeze();
                this.Resources[name] = clr;
                this.Resources[name + "Brush"] = brush;
            }

            var accent = Color.FromRgb(areo.R, areo.G, areo.B);
            var accentDark = BlendColor(accent, Colors.Black, 20);
            var accentLight = BlendColor(accent, Colors.White, 10);
            var accentLightLight = BlendColor(accent, Colors.White, 20);

            if (systemIsDark)
            {
                SetColor("ControlActive", accent);
                SetColor("ControlActiveDark", accentDark);
                SetColor("ControlActiveLight", accentLight);
                SetColor("ControlActiveLightLight", accentLightLight);
            }
            else
            {
                SetColor("ControlActive", accent);
                SetColor("ControlActiveDark", accentLight);
                SetColor("ControlActiveLight", accentDark);
                SetColor("ControlActiveLightLight", accentLightLight);
            }
        }

        private static Color BlendColor(Color color1, Color color2, double color2Perc)
        {
            if ((color2Perc < 0) || (100 < color2Perc))
                throw new ArgumentOutOfRangeException("color2Perc");

            return Color.FromRgb(
                BlendColorChannel(color1.R, color2.R, color2Perc),
                BlendColorChannel(color1.G, color2.G, color2Perc),
                BlendColorChannel(color1.B, color2.B, color2Perc));
        }

        private static byte BlendColorChannel(double channel1, double channel2, double channel2Perc)
        {
            var buff = channel1 + (channel2 - channel1) * channel2Perc / 100D;
            return Math.Min((byte)Math.Round(buff), (byte)255);
        }
    }
}

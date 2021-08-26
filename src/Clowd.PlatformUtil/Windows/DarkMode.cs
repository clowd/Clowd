using Microsoft.Win32;
using System;
using System.Runtime.InteropServices;
using static Vanara.PInvoke.User32;

namespace Clowd.PlatformUtil.Windows
{
    public unsafe static class DarkMode
    {
        enum PreferredAppMode
        {
            Default,
            AllowDark,
            ForceDark,
            ForceLight,
            Max
        };

        enum WINDOWCOMPOSITIONATTRIB
        {
            WCA_UNDEFINED = 0,
            WCA_NCRENDERING_ENABLED = 1,
            WCA_NCRENDERING_POLICY = 2,
            WCA_TRANSITIONS_FORCEDISABLED = 3,
            WCA_ALLOW_NCPAINT = 4,
            WCA_CAPTION_BUTTON_BOUNDS = 5,
            WCA_NONCLIENT_RTL_LAYOUT = 6,
            WCA_FORCE_ICONIC_REPRESENTATION = 7,
            WCA_EXTENDED_FRAME_BOUNDS = 8,
            WCA_HAS_ICONIC_BITMAP = 9,
            WCA_THEME_ATTRIBUTES = 10,
            WCA_NCRENDERING_EXILED = 11,
            WCA_NCADORNMENTINFO = 12,
            WCA_EXCLUDED_FROM_LIVEPREVIEW = 13,
            WCA_VIDEO_OVERLAY_ACTIVE = 14,
            WCA_FORCE_ACTIVEWINDOW_APPEARANCE = 15,
            WCA_DISALLOW_PEEK = 16,
            WCA_CLOAK = 17,
            WCA_CLOAKED = 18,
            WCA_ACCENT_POLICY = 19,
            WCA_FREEZE_REPRESENTATION = 20,
            WCA_EVER_UNCLOAKED = 21,
            WCA_VISUAL_OWNER = 22,
            WCA_HOLOGRAPHIC = 23,
            WCA_EXCLUDED_FROM_DDA = 24,
            WCA_PASSIVEUPDATEMODE = 25,
            WCA_USEDARKMODECOLORS = 26,
            WCA_LAST = 27
        };

        struct WINDOWCOMPOSITIONATTRIBDATA
        {
            public WINDOWCOMPOSITIONATTRIB Attrib;
            public void* pvData;
            public nuint cbData;
        };

        [DllImport("ntdll.dll")]
        private static extern void RtlGetNtVersionNumbers(out uint major, out uint minor, out uint build);

        [DllImport("user32.dll")]
        private static extern void SetWindowCompositionAttribute(nint hWnd, ref WINDOWCOMPOSITIONATTRIBDATA data);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        public static bool IsDarkModeEnabled()
        {
            using var personalize = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            object useLight = personalize.GetValue("AppsUseLightTheme");

            if (useLight == null)
                return false;

            if (useLight is not int)
                return false;

            return (int)useLight == 0;
        }

        public static bool UseImmersiveDarkMode(IntPtr handle, bool enabled)
        {
            if (IsWindows10OrGreater(17763))
            {
                var attribute = DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1;
                if (IsWindows10OrGreater(18985))
                    attribute = DWMWA_USE_IMMERSIVE_DARK_MODE;

                int useImmersiveDarkMode = enabled ? 1 : 0;
                return DwmSetWindowAttribute(handle, (int)attribute, ref useImmersiveDarkMode, sizeof(int)) == 0;
            }

            return false;
        }

        private static bool IsWindows10OrGreater(int build = -1)
        {
            return Environment.OSVersion.Version.Major >= 10 && Environment.OSVersion.Version.Build >= build;
        }

        private static void RefreshDarkModeForWindow(nint hWnd)
        {
            SetDarkModeForWindow(hWnd, IsDarkModeEnabled());
        }

        private static void SetDarkModeForWindow(nint hWnd, bool dark)
        {
            RtlGetNtVersionNumbers(out var major, out var minor, out var build);
            build &= ~0xF0000000;

            if (major < 10 || build < 17763) // not supported
                return;

            if (build < 18362)
            {
                SetProp(hWnd, "UseImmersiveDarkModeColors", (IntPtr)(&dark));
            }
            else
            {
                WINDOWCOMPOSITIONATTRIBDATA data = new();
                data.Attrib = WINDOWCOMPOSITIONATTRIB.WCA_USEDARKMODECOLORS;
                data.pvData = &dark;
                data.cbData = sizeof(bool);
                SetWindowCompositionAttribute(hWnd, ref data);
            }

            int dwma = dark ? 1 : 0;
            DwmSetWindowAttribute(hWnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref dwma, sizeof(int));
            DwmSetWindowAttribute(hWnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dwma, sizeof(int));

            SendMessage(hWnd, WindowMessage.WM_THEMECHANGED, IntPtr.Zero, IntPtr.Zero);
        }
    }
}

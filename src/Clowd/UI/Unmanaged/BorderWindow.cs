using System;
using System.Runtime.InteropServices;
using System.Windows.Media;
using Clowd.PlatformUtil;
using Vanara.PInvoke;

namespace Clowd.UI.Unmanaged
{
    public static unsafe class BorderWindow
    {
        [DllImport("Clowd.Win64")]
        private static extern void BorderShow([MarshalAs(UnmanagedType.U1)] byte r, [MarshalAs(UnmanagedType.U1)] byte g, [MarshalAs(UnmanagedType.U1)] byte b, RECT* decoratedArea);

        [DllImport("Clowd.Win64")]
        private static extern void BorderSetOverlayText([MarshalAs(UnmanagedType.LPWStr)] string overlayText);

        [DllImport("Clowd.Win64")]
        private static extern void BorderClose();

        public static void Show(Color accentColor, ScreenRect area)
        {
            var c = new RECT(area.Left, area.Top, area.Right, area.Bottom);
            BorderShow(accentColor.R, accentColor.G, accentColor.B, &c);
        }

        public static void SetText(string txt)
        {
            BorderSetOverlayText(txt);
        }

        public static void Hide()
        {
            BorderClose();
        }
    }
}

using System;
using CsWin32.UI.WindowsAndMessaging;

namespace Clowd.PlatformUtil.Windows
{
    [Flags]
    public enum WindowStyleEx : uint
    {
        Dlgmodalframe = WINDOW_EX_STYLE.WS_EX_DLGMODALFRAME,
        Noparentnotify = WINDOW_EX_STYLE.WS_EX_NOPARENTNOTIFY,
        Topmost = WINDOW_EX_STYLE.WS_EX_TOPMOST,
        Acceptfiles = WINDOW_EX_STYLE.WS_EX_ACCEPTFILES,
        Transparent = WINDOW_EX_STYLE.WS_EX_TRANSPARENT,
        Mdichild = WINDOW_EX_STYLE.WS_EX_MDICHILD,
        Toolwindow = WINDOW_EX_STYLE.WS_EX_TOOLWINDOW,
        Windowedge = WINDOW_EX_STYLE.WS_EX_WINDOWEDGE,
        Clientedge = WINDOW_EX_STYLE.WS_EX_CLIENTEDGE,
        Contexthelp = WINDOW_EX_STYLE.WS_EX_CONTEXTHELP,
        Right = WINDOW_EX_STYLE.WS_EX_RIGHT,
        Left = WINDOW_EX_STYLE.WS_EX_LEFT,
        Rtlreading = WINDOW_EX_STYLE.WS_EX_RTLREADING,
        Ltrreading = WINDOW_EX_STYLE.WS_EX_LTRREADING,
        Leftscrollbar = WINDOW_EX_STYLE.WS_EX_LEFTSCROLLBAR,
        Rightscrollbar = WINDOW_EX_STYLE.WS_EX_RIGHTSCROLLBAR,
        Controlparent = WINDOW_EX_STYLE.WS_EX_CONTROLPARENT,
        Staticedge = WINDOW_EX_STYLE.WS_EX_STATICEDGE,
        Appwindow = WINDOW_EX_STYLE.WS_EX_APPWINDOW,
        Overlappedwindow = WINDOW_EX_STYLE.WS_EX_OVERLAPPEDWINDOW,
        Palettewindow = WINDOW_EX_STYLE.WS_EX_PALETTEWINDOW,
        Layered = WINDOW_EX_STYLE.WS_EX_LAYERED,
        Noinheritlayout = WINDOW_EX_STYLE.WS_EX_NOINHERITLAYOUT,
        Noredirectionbitmap = WINDOW_EX_STYLE.WS_EX_NOREDIRECTIONBITMAP,
        Layoutrtl = WINDOW_EX_STYLE.WS_EX_LAYOUTRTL,
        Composited = WINDOW_EX_STYLE.WS_EX_COMPOSITED,
        Noactivate = WINDOW_EX_STYLE.WS_EX_NOACTIVATE,
    }
}

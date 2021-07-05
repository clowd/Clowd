using System;
using CsWin32.UI.WindowsAndMessaging;

namespace Clowd.PlatformUtil.Windows
{
    [Flags]
    public enum WindowStyle : uint
    {
        Overlapped = WINDOW_STYLE.WS_OVERLAPPED,
        Popup = WINDOW_STYLE.WS_POPUP,
        Child = WINDOW_STYLE.WS_CHILD,
        Minimize = WINDOW_STYLE.WS_MINIMIZE,
        Maximize = WINDOW_STYLE.WS_MAXIMIZE,
        HScroll = WINDOW_STYLE.WS_HSCROLL,
        VScroll = WINDOW_STYLE.WS_VSCROLL,
        Visible = WINDOW_STYLE.WS_VISIBLE,
        Disabled = WINDOW_STYLE.WS_DISABLED,
        ClipSiblings = WINDOW_STYLE.WS_CLIPSIBLINGS,
        ClipChildren = WINDOW_STYLE.WS_CLIPCHILDREN,
        Caption = WINDOW_STYLE.WS_CAPTION,
        Border = WINDOW_STYLE.WS_BORDER,
        DlgFrame = WINDOW_STYLE.WS_DLGFRAME,
        SysMenu = WINDOW_STYLE.WS_SYSMENU,
        ThickFrame = WINDOW_STYLE.WS_THICKFRAME,
        Group = WINDOW_STYLE.WS_GROUP,
        Tabstop = WINDOW_STYLE.WS_TABSTOP,
        MinimizeBox = WINDOW_STYLE.WS_MINIMIZEBOX,
        MaximizeBox = WINDOW_STYLE.WS_MAXIMIZEBOX,
        Tiled = WINDOW_STYLE.WS_TILED,
        Iconic = WINDOW_STYLE.WS_ICONIC,
        SizeBox = WINDOW_STYLE.WS_SIZEBOX,
        TiledWindow = WINDOW_STYLE.WS_TILEDWINDOW,
        OverlappedWindow = WINDOW_STYLE.WS_OVERLAPPEDWINDOW,
        PopupWindow = WINDOW_STYLE.WS_POPUPWINDOW,
        ChildWindow = WINDOW_STYLE.WS_CHILDWINDOW,
        ActiveCaption = WINDOW_STYLE.WS_ACTIVECAPTION,
    }
}

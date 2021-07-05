using System;
using CsWin32.UI.WindowsAndMessaging;

namespace Clowd.PlatformUtil.Windows
{
    [Flags]
    public enum ClassStyle : uint
    {
        VRedraw = WNDCLASS_STYLES.CS_VREDRAW,
        HRedraw = WNDCLASS_STYLES.CS_HREDRAW,
        Dblclks = WNDCLASS_STYLES.CS_DBLCLKS,
        OwnDC = WNDCLASS_STYLES.CS_OWNDC,
        ClassDC = WNDCLASS_STYLES.CS_CLASSDC,
        ParentDC = WNDCLASS_STYLES.CS_PARENTDC,
        NoClose = WNDCLASS_STYLES.CS_NOCLOSE,
        SaveBits = WNDCLASS_STYLES.CS_SAVEBITS,
        ByteAlignClient = WNDCLASS_STYLES.CS_BYTEALIGNCLIENT,
        ByteAlignWindow = WNDCLASS_STYLES.CS_BYTEALIGNWINDOW,
        GlobalClass = WNDCLASS_STYLES.CS_GLOBALCLASS,
        Ime = WNDCLASS_STYLES.CS_IME,
        Dropshadow = WNDCLASS_STYLES.CS_DROPSHADOW,
    }
}

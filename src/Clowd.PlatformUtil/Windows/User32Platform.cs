using System;
using System.Collections.Generic;
using System.Text;
using CsWin32.Graphics.Gdi;
using CsWin32.UI.WindowsAndMessaging;
using CsWin32.Foundation;
using static CsWin32.PInvoke;
using static CsWin32.Constants;
using System.ComponentModel;

namespace Clowd.PlatformUtil.Windows
{
    public class User32Platform : Platform
    {
        public override IEnumerable<IScreen> AllScreens => User32Screen.AllScreens;

        public override IScreen VirtualScreen => User32Screen.VirtualScreen;

        public override IScreen PrimaryScreen => User32Screen.PrimaryScreen;

        public override ScreenPoint GetMousePosition()
        {
            if (!GetCursorPos(out POINT pt))
                throw new Win32Exception();

            return pt;
        }

        public override IScreen GetScreenFromPoint(ScreenPoint pt)
        {
            return User32Screen.FromPoint(pt);
        }

        public override IScreen GetScreenFromRect(ScreenRect rect)
        {
            return User32Screen.FromRect(rect);
        }

        public override IWindow GetWindowFromHandle(nint handle)
        {
            return User32Window.FromHandle(handle);
        }

        public override void SetMousePosition(ScreenPoint pt)
        {
            if (!SetCursorPos(pt.X, pt.Y))
                throw new Win32Exception();
        }
    }
}

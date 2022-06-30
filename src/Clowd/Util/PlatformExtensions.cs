using System.Windows;
using System.Windows.Interop;
using Clowd.PlatformUtil;
using Clowd.PlatformUtil.Windows;

namespace Clowd
{
    internal static class PlatformExtensions
    {
        public static IWindow GetPlatformWindow(this Window window)
        {
            var iop = new WindowInteropHelper(window);
            iop.EnsureHandle();
            return Platform.Current.GetWindowFromHandle(iop.Handle);
        }

        public static DpiContext ClientAreaToDpiContext(this Window window)
        {
            WindowInteropHelper helper = new WindowInteropHelper(window);
            PresentationSource source = PresentationSource.FromVisual(window);

            var wplat = User32Window.FromHandle(helper.Handle);

            double dpiX = 96, dpiY = 96;
            if (source != null)
            {
                dpiX *= source.CompositionTarget.TransformToDevice.M11;
                dpiY *= source.CompositionTarget.TransformToDevice.M22;
            }

            var bndWindow = wplat.ClientBounds;
            var bndVirt = Platform.Current.VirtualScreen;

            var xoff = bndVirt.Bounds.Left - bndWindow.Left;
            var yoff = bndVirt.Bounds.Top - bndWindow.Top;

            return new DpiContext((int)dpiX, (int)dpiY, xoff, yoff);
        }
    }
}

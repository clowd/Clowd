using System.Windows;
using System.Windows.Interop;
using Clowd.PlatformUtil;

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
    }
}

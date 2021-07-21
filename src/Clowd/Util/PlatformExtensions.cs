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
            return Platform.Current.GetWindowFromHandle(iop.Handle);
        }
    }
}

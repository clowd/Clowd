using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Clowd.Interop.DwmApi
{
    /// <summary>
    /// Returned by the GetThemeMargins function to define the margins of windows that have visual styles applied.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MARGINS
    {
        /// <summary>
        /// Width of the left border that retains its size.
        /// </summary>
        public int cxLeftWidth;

        /// <summary>
        /// Width of the right border that retains its size.
        /// </summary>
        public int cxRightWidth;

        /// <summary>
        /// Height of the top border that retains its size.
        /// </summary>
        public int cyTopHeight;

        /// <summary>
        /// Height of the bottom border that retains its size.
        /// </summary>
        public int cyBottomHeight;
    }
}

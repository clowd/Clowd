using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Clowd.Interop
{
    /// <summary>
    /// The POINT structure defines the x- and y- coordinates of a point.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        /// <summary>
        /// The x-coordinate of the point.
        /// </summary>
        public int x;

        /// <summary>
        /// The y-coordinate of the point.
        /// </summary>
        public int y;

        /// <summary>
        /// Converts a POINT to a System.Windows.Point.
        /// </summary>
        /// <param name="point">The POINT structure to convert.</param>
        /// <returns>The equivalent System.Windows.Point.</returns>
        public static implicit operator Point(POINT point)
        {
            return new Point(point.x, point.y);
        }
    }
}

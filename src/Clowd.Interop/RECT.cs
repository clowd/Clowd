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
    /// Defines the coordinates of the upper-left and lower-right corners of a rectangle.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        /// <summary>
        /// The x-coordinate of the upper-left corner of the rectangle.
        /// </summary>
        public int left;

        /// <summary>
        /// The y-coordinate of the upper-left corner of the rectangle.
        /// </summary>
        public int top;

        /// <summary>
        /// The x-coordinate of the lower-right corner of the rectangle.
        /// </summary>
        public int right;

        /// <summary>
        /// The y-coordinate of the lower-right corner of the rectangle.
        /// </summary>
        public int bottom;

        /// <summary>
        /// Returns whether the rectangle does not have a zero width or height.
        /// </summary>
        /// <returns>Whether the rectangle does not have a zero width or height.</returns>
        public bool HasSize()
        {
            return right - left > 0 && bottom - top > 0;
        }

        /// <summary>
        /// Converts a RECT structure to an equivalent System.Windows.Rectangle structure. Returns a 0-width rectangle if the calculated width or height is negative.
        /// </summary>
        /// <param name="rect">The RECT to convert.</param>
        /// <returns>The equivalent System.Windows.Rectangle.</returns>
        public static implicit operator Rectangle(RECT rect)
        {
            // return a 0-width rectangle if the width or height is negative
            if (rect.right - rect.left < 0 || rect.bottom - rect.top < 0)
                return new Rectangle(rect.left, rect.top, 0, 0);
            return new Rectangle(rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top);
        }

        /// <summary>
        /// Converts a RECT structure equivalent to the specified System.Windows.Rectangle. Double precision is lost.
        /// </summary>
        /// <param name="rect">The System.Windows.Rectangle to convert.</param>
        /// <returns>The equivalent RECT structure.</returns>
        public static implicit operator RECT(Rectangle rect)
        {
            return new RECT()
            {
                left = (int)rect.Left,
                top = (int)rect.Top,
                right = (int)rect.Right,
                bottom = (int)rect.Bottom
            };
        }

        public static implicit operator System.Windows.Rect(RECT rect)
        {
            // return a 0-width rectangle if the width or height is negative
            if (rect.right - rect.left < 0 || rect.bottom - rect.top < 0)
                return new System.Windows.Rect(rect.left, rect.top, 0, 0);
            return new System.Windows.Rect(rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top);
        }
        public static implicit operator RECT(System.Windows.Rect rect)
        {
            return new RECT()
            {
                left = (int)rect.Left,
                top = (int)rect.Top,
                right = (int)rect.Right,
                bottom = (int)rect.Bottom
            };
        }
    }
}

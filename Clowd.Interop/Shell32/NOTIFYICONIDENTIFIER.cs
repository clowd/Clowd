using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Clowd.Interop.Shell32
{
    /// <summary>
    /// Contains information used by Shell_NotifyIconGetRectangle to identify the icon for which to retrieve the bounding rectangle.
    /// </summary>
    /// <remarks>
    /// The icon can be identified to Shell_NotifyIconGetRectangle through this structure in two ways: 
    ///    guidItem alone (recommended)
    ///    hWnd plus uID
    /// If guidItem is used, hWnd and uID are ignored.
    /// </remarks>
    public struct NOTIFYICONIDENTIFIER
    {
        /// <summary>
        /// Size of this structure, in bytes. 
        /// </summary>
        public uint cbSize;

        /// <summary>
        /// A handle to the parent window used by the notification's callback function. For more information, see the hWnd member of the NOTIFYICONDATA structure.
        /// </summary>
        public IntPtr hWnd;

        /// <summary>
        /// The application-defined identifier of the notification icon. Multiple icons can be associated with a single hWnd, each with their own uID.
        /// </summary>
        public uint uID;

        /// <summary>
        /// A registered GUID that identifies the icon.
        /// </summary>
        public Guid guidItem;
    }
}

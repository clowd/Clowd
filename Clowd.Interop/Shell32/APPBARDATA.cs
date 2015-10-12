using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Clowd.Interop.Shell32
{
    /// <summary>
    /// Contains information about a system appbar message. This structure is used with the SHAppBarMessage function.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct APPBARDATA
    {
        /// <summary>
        /// The size of the structure, in bytes.
        /// </summary>
        public uint cbSize;

        /// <summary>
        /// The handle to the appbar window.
        /// </summary>
        public IntPtr hWnd;

        /// <summary>
        /// An application-defined message identifier. The application uses the specified identifier for notification messages that it sends to the appbar identified by the hWnd member. This member is used when sending the ABM_NEW message.
        /// </summary>
        public uint uCallbackMessage;

        /// <summary>
        /// A value that specifies an edge of the screen. This member is used when sending the ABM_GETAUTOHIDEBAR, ABM_QUERYPOS, ABM_SETAUTOHIDEBAR, and ABM_SETPOS messages. This member can be one of the following values.
        /// </summary>
        public ABEdge uEdge;

        /// <summary>
        /// A RECT structure to contain the bounding rectangle, in screen coordinates, of an appbar or the Windows taskbar. This member is used when sending the ABM_GETTASKBARPOS, ABM_QUERYPOS, and ABM_SETPOS messages.
        /// </summary>
        public RECT rc;

        /// <summary>
        /// A message-dependent value. This member is used with the ABM_SETAUTOHIDEBAR and ABM_SETSTATE messages.
        /// </summary>
        public IntPtr lParam;
    }
}

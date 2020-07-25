using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Clowd.Interop.Shell32
{
    public class SHELL32
    {
        /// <summary>
        /// Gets the screen coordinates of the bounding rectangle of a notification icon.
        /// </summary>
        /// <param name="identifier">Pointer to a NOTIFYICONIDENTIFIER structure that identifies the icon.</param>
        /// <param name="iconLocation">Pointer to a RECT structure that, when this function returns successfully, receives the coordinates of the icon.</param>
        /// <returns>If the method succeeds, it returns S_OK. Otherwise, it returns an HRESULT error code.</returns>
        [DllImport("Shell32", SetLastError = true)]
        public static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT iconLocation);

        /// <summary>
        /// Sends an appbar message to the system.
        /// </summary>
        /// <param name="dwMessage">Appbar message value to send. This parameter can be one of the following values.</param>
        /// <param name="pData">The address of an APPBARDATA structure. The content of the structure on entry and on exit depends on the value set in the dwMessage parameter.</param>
        /// <returns>This function returns a message-dependent value. For more information, see the Windows SDK documentation for the specific appbar message sent. Links to those documents are given in the See Also section.</returns>
        [DllImport("shell32.dll", SetLastError = true)]
        public static extern IntPtr SHAppBarMessage(ABMsg dwMessage, ref APPBARDATA pData);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out string pszPath);
    }

    public static class KnownFolders
    {
        public static Guid Contacts = Guid.Parse("{56784854-C6CB-462B-8169-88E350ACB882}");
        public static Guid Desktop = Guid.Parse("{B4BFCC3A-DB2C-424C-B029-7FE99A87C641}");
        public static Guid Documents = Guid.Parse("{FDD39AD0-238F-46AF-ADB4-6C85480369C7}");
        public static Guid Downloads = Guid.Parse("{374DE290-123F-4565-9164-39C4925E467B}");
        public static Guid Favorites = Guid.Parse("{1777F761-68AD-4D8A-87BD-30B759FA33DD}");
        public static Guid Links = Guid.Parse("{BFB9D5E0-C6A9-404C-B2B2-AE6DB6AF4968}");
        public static Guid Music = Guid.Parse("{4BD8D571-6D19-48D3-BE97-422220080E43}");
        public static Guid Pictures = Guid.Parse("{33E28130-4E1E-4676-835A-98395C3BC3BB}");
        public static Guid SavedGames = Guid.Parse("{4C5C32FF-BB9D-43B0-B5B4-2D72E54EAAA4}");
        public static Guid SavedSearches = Guid.Parse("{7D1D3A04-DEBB-4115-95CF-2F29DA2920DA}");
        public static Guid Videos = Guid.Parse("{18989B1D-99B5-455B-841C-AB7C74E4DDFC}");
    }

    /// <summary>
    /// Appbar message value to send.
    /// </summary>
    public enum ABMsg
    {
        /// <summary>
        /// Registers a new appbar and specifies the message identifier that the system should use to send notification messages to the appbar.
        /// </summary>
        ABM_NEW = 0,

        /// <summary>
        /// Unregisters an appbar, removing the bar from the system's internal list.
        /// </summary>
        ABM_REMOVE = 1,

        /// <summary>
        /// Requests a size and screen position for an appbar.
        /// </summary>
        ABM_QUERYPOS = 2,

        /// <summary>
        /// Sets the size and screen position of an appbar.
        /// </summary>
        ABM_SETPOS = 3,

        /// <summary>
        /// Retrieves the autohide and always-on-top states of the Windows taskbar.
        /// </summary>
        ABM_GETSTATE = 4,

        /// <summary>
        /// Retrieves the bounding rectangle of the Windows taskbar.
        /// </summary>
        ABM_GETTASKBARPOS = 5,

        /// <summary>
        /// Notifies the system to activate or deactivate an appbar. The lParam member of the APPBARDATA pointed to by pData is set to TRUE to activate or FALSE to deactivate.
        /// </summary>
        ABM_ACTIVATE = 6,

        /// <summary>
        /// Retrieves the handle to the autohide appbar associated with a particular edge of the screen.
        /// </summary>
        ABM_GETAUTOHIDEBAR = 7,

        /// <summary>
        /// Registers or unregisters an autohide appbar for an edge of the screen.
        /// </summary>
        ABM_SETAUTOHIDEBAR = 8,

        /// <summary>
        /// Notifies the system when an appbar's position has changed.
        /// </summary>
        ABM_WINDOWPOSCHANGED = 9,

        /// <summary>
        /// Windows XP and later: Sets the state of the appbar's autohide and always-on-top attributes.
        /// </summary>
        ABM_SETSTATE = 10
    }

    /// <summary>
    /// A value that specifies an edge of the screen. This member is used when sending the ABM_GETAUTOHIDEBAR, ABM_QUERYPOS, ABM_SETAUTOHIDEBAR, and ABM_SETPOS messages.
    /// </summary>
    public enum ABEdge
    {
        /// <summary>
        /// Left edge of screen.
        /// </summary>
        ABE_LEFT = 0,

        /// <summary>
        /// Top edge of screen.
        /// </summary>
        ABE_TOP = 1,

        /// <summary>
        /// Right edge of screen.
        /// </summary>
        ABE_RIGHT = 2,

        /// <summary>
        /// Bottom edge of screen.
        /// </summary>
        ABE_BOTTOM = 3
    }

    /// <summary>
    /// Autohide and always-on-top states of the Windows taskbar.
    /// </summary>
    public enum ABState
    {
        /// <summary>
        /// Autohide and always-on-top both off.
        /// </summary>
        ABS_MANUAL = 0,

        /// <summary>
        /// Always-on-top on, autohide off.
        /// </summary>
        ABS_AUTOHIDE = 1,

        /// <summary>
        /// Autohide on, always-on-top off.
        /// </summary>
        ABS_ALWAYSONTOP = 2,

        /// <summary>
        /// Autohide and always-on-top both on.
        /// </summary>
        ABS_AUTOHIDEANDONTOP = ABS_AUTOHIDE | ABS_ALWAYSONTOP
    }
}

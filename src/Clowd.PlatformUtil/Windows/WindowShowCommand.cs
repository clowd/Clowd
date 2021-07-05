using System;
using CsWin32.UI.WindowsAndMessaging;

namespace Clowd.PlatformUtil
{

    /// <summary>
    /// Controls how the window is to be shown.
    /// </summary>
    public enum WindowShowCommand : uint
    {
        /// <summary>
        /// Hides the window and activates another window.
        /// </summary>
        Hide = SHOW_WINDOW_CMD.SW_HIDE,

        /// <summary>
        /// Minimizes a window, even if the thread that owns the window is not responding. This flag should only be used when minimizing windows from a different thread.
        /// </summary>
        ForceMinimize = SHOW_WINDOW_CMD.SW_FORCEMINIMIZE,

        /// <summary>
        /// Maximizes the specified window.
        /// </summary>
        Maximize = SHOW_WINDOW_CMD.SW_MAXIMIZE,

        /// <summary>
        /// Minimizes the specified window and activates the next top-level window in the Z order.
        /// </summary>
        Minimize = SHOW_WINDOW_CMD.SW_MINIMIZE,

        /// <summary>
        /// Activates and displays the window. If the window is minimized or maximized, the system restores it to its original size and position. An application should specify this flag when restoring a minimized window.
        /// </summary>
        Restore = SHOW_WINDOW_CMD.SW_RESTORE,

        /// <summary>
        /// Activates the window and displays it in its current size and position.
        /// </summary>
        Show = SHOW_WINDOW_CMD.SW_SHOW,

        /// <summary>
        /// Sets the show state based on the SW_ value specified in the STARTUPINFO structure passed to the CreateProcess function by the program that started the application.
        /// </summary>
        ShowDefault = SHOW_WINDOW_CMD.SW_SHOWDEFAULT,

        /// <summary>
        /// Activates the window and displays it as a maximized window.
        /// </summary>
        ShowMaximized = SHOW_WINDOW_CMD.SW_SHOWMAXIMIZED,

        /// <summary>
        /// Activates the window and displays it as a minimized window.
        /// </summary>
        ShowMinimized = SHOW_WINDOW_CMD.SW_SHOWMINIMIZED,

        /// <summary>
        /// Displays the window as a minimized window. This value is similar to SW_SHOWMINIMIZED, except the window is not activated.
        /// </summary>
        ShowMinNoActivate = SHOW_WINDOW_CMD.SW_SHOWMINNOACTIVE,

        /// <summary>
        /// Displays the window in its current size and position. This value is similar to SW_SHOW, except that the window is not activated.
        /// </summary>
        ShowNA = SHOW_WINDOW_CMD.SW_SHOWNA,

        /// <summary>
        /// Displays a window in its most recent size and position. This value is similar to SW_SHOWNORMAL, except that the window is not activated.
        /// </summary>
        ShowNoActivate = SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE,

        /// <summary>
        /// Activates and displays a window. If the window is minimized or maximized, the system restores it to its original size and position. An application should specify this flag when displaying the window for the first time.
        /// </summary>
        ShowNormal = SHOW_WINDOW_CMD.SW_SHOWNORMAL,
    }
}

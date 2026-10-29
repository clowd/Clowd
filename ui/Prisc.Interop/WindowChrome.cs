using System;
using System.Diagnostics;

namespace Prisc.Interop
{
    /// <summary>
    /// Native window chrome (<c>prisc_window_*</c>): the open-time setup of overlay windows,
    /// click-through, raising without activation, capture exclusion, the keyboard foreground, atomic
    /// bounds and the macOS traffic lights.
    /// </summary>
    /// <remarks>
    /// Every member takes the raw platform window the toolkit hands out: the HWND on Windows, the
    /// <c>NSWindow*</c> on macOS (Avalonia's <c>IMacOSTopLevelPlatformHandle.NSWindow</c>, not the
    /// view). The window is borrowed for the call and must not have closed; a zero handle is a
    /// caller bug and throws.
    /// <para>The operations are cosmetic and best-effort, as their call sites have always treated
    /// them: each returns whether it took effect. False means the OS has no such thing (another
    /// platform, or the macOS arm deferred this phase) or the OS refused the call, which is written
    /// to the debug output with the library's message. A caller error (a zero handle, an unknown
    /// flag, a macOS call off the main thread) throws <see cref="InteropException"/>.</para>
    /// <para>Threads: on macOS every member except <see cref="TakeForeground"/> must run on the main
    /// (UI) thread; elsewhere any thread, except <see cref="DefaultWindowProc"/>, which runs inside
    /// the window's own procedure.</para>
    /// </remarks>
    public static unsafe class WindowChrome
    {
        /// <summary>
        /// The open-time setup of an overlay window in one call: <see cref="OverlayFlags.CoverMenuBar"/>
        /// (macOS overlay level and collection behaviour; apply before positioning, or AppKit keeps the
        /// frame off the menu bar), <see cref="OverlayFlags.OpaqueLayered"/> (Windows: the mandatory
        /// follow-up to WS_EX_LAYERED, without which the window is never repainted) and
        /// <see cref="OverlayFlags.ClickThrough"/> (mouse events pass through). A flag that does not
        /// apply on this OS is a no-op; without ClickThrough the mouse state is left alone.
        /// </summary>
        public static bool PrepareOverlay(nint handle, OverlayFlags flags)
        {
            InteropLibrary.Require();
            return Applied(NativeMethods.prisc_window_prepare_overlay(handle, (uint)flags), "prisc_window_prepare_overlay");
        }

        /// <summary>
        /// Switches click-through on or off, live. Off is an explicit "take the mouse everywhere" on
        /// macOS, transparent pixels included. On Windows the toggled WS_EX_TRANSPARENT lasts until
        /// Avalonia re-applies the window styles, so the window must also re-assert it from a
        /// retained styles callback.
        /// </summary>
        public static bool SetClickThrough(nint handle, bool on)
        {
            InteropLibrary.Require();
            return Applied(NativeMethods.prisc_window_set_click_through(handle, on ? (byte)1 : (byte)0), "prisc_window_set_click_through");
        }

        /// <summary>Raises the window above every overlay without activating it, moving it or resizing it
        /// (HWND_TOPMOST on Windows, one level above the overlay level plus orderFrontRegardless on
        /// macOS). The raise stands for the life of the window.</summary>
        public static bool RaiseTopmostNoActivate(nint handle)
        {
            InteropLibrary.Require();
            return Applied(NativeMethods.prisc_window_raise_topmost_no_activate(handle), "prisc_window_raise_topmost_no_activate");
        }

        /// <summary>Hides the window from every screen capture API, or shows it again. False on a Windows
        /// build older than 10 2004, which refuses the exclusion (the window stays visible to capture
        /// rather than being blacked out).</summary>
        public static bool SetExcludedFromCapture(nint handle, bool excluded)
        {
            InteropLibrary.Require();
            return Applied(NativeMethods.prisc_window_set_excluded_from_capture(handle, excluded ? (byte)1 : (byte)0),
                           "prisc_window_set_excluded_from_capture");
        }

        /// <summary>
        /// Whether the window has the keyboard right now: the system foreground window on Windows (not
        /// the toolkit's active flag, which a SetForegroundWindow refused by the foreground lock still
        /// sets), the key window of the active app on macOS. False on a platform without either.
        /// </summary>
        public static bool IsForeground(nint handle)
        {
            InteropLibrary.Require();
            byte foreground;
            int status = NativeMethods.prisc_window_is_foreground(handle, &foreground);
            return Applied(status, "prisc_window_is_foreground") && foreground != 0;
        }

        /// <summary>
        /// Brings the window to the front with the keyboard past the Windows foreground lock (which
        /// refuses a process that did not receive the last input, as after a global hotkey) and returns
        /// whether it ended up there. False on macOS this phase: activate through the toolkit there.
        /// Any thread.
        /// </summary>
        public static bool TakeForeground(nint handle)
        {
            InteropLibrary.Require();
            byte inFront;
            int status = NativeMethods.prisc_window_take_foreground(handle, &inFront);
            return Applied(status, "prisc_window_take_foreground") && inFront != 0;
        }

        /// <summary>Moves and resizes the window in one native step, in physical virtual-desktop pixels.
        /// False on macOS this phase, where the caller sets the bounds through the toolkit.</summary>
        public static bool SetBounds(nint handle, int x, int y, int width, int height)
        {
            InteropLibrary.Require();
            var rect = new PriscRect { x = x, y = y, width = width, height = height };
            return Applied(NativeMethods.prisc_window_set_bounds(handle, &rect), "prisc_window_set_bounds");
        }

        /// <summary>
        /// Windows: the default window procedure's answer for a message a procedure hook hands on. Call
        /// only from inside the window's procedure. 0 when the call could not be made (off Windows).
        /// </summary>
        public static nint DefaultWindowProc(nint hwnd, uint message, nuint wParam, nint lParam)
        {
            InteropLibrary.Require();
            nint result;
            int status = NativeMethods.prisc_window_default_proc_windows(hwnd, message, wParam, lParam, &result);
            return Applied(status, "prisc_window_default_proc_windows") ? result : 0;
        }

        /// <summary>
        /// macOS: places the close, minimise and zoom buttons at the given insets (points from the
        /// window's top-left; the top inset may exceed the title bar, which does not clip). AppKit puts
        /// them back on every title bar relayout, so call again after a resize, zoom, minimise and
        /// restore, or fullscreen transition. False off macOS or for a window without the buttons.
        /// </summary>
        public static bool SetTrafficLightPosition(nint nsWindow, double leftInset, double topInset)
        {
            InteropLibrary.Require();
            return Applied(NativeMethods.prisc_window_set_traffic_light_position_macos(nsWindow, leftInset, topInset),
                           "prisc_window_set_traffic_light_position_macos");
        }

        /// <summary>
        /// The policy every member shares: OK is true; nothing-to-do and not-on-this-OS are false; an
        /// OS refusal is false plus one debug line with the library's message (read now, on this
        /// thread); anything else is a caller bug and throws.
        /// </summary>
        private static bool Applied(int status, string operation)
        {
            switch ((InteropStatus)status)
            {
                case InteropStatus.Ok:
                    return true;
                case InteropStatus.None:
                case InteropStatus.Unsupported:
                    return false;
                case InteropStatus.Os:
                    Debug.WriteLine($"{operation}: the OS refused. {Native.LastErrorMessage()}");
                    return false;
                default:
                    Native.Check(status, operation);
                    return false;
            }
        }
    }
}

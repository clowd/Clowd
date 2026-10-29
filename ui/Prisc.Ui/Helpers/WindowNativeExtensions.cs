using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Prisc.Interop;

namespace Prisc.UI.Helpers
{
    /// <summary>
    /// Native window-style helpers for the recording UI windows (BorderWindow, ShareResizeWindow,
    /// FloatingTrayWindow, Draw on Screen, TransparentDialogHost), and the capture exclusion every
    /// Prisc top level is subject to (CaptureExclusion). The native work is
    /// <see cref="WindowChrome"/>'s (prisc_interop); this class maps an Avalonia window to the raw
    /// platform window it takes, and keeps the parts that are Avalonia's own (the Win32 style and
    /// window procedure callbacks). Every member is safe to call on any OS: what does not apply on
    /// one is a no-op there, so callers need no OS checks.
    /// </summary>
    internal static class WindowNativeExtensions
    {
        public const uint WS_EX_TRANSPARENT = 0x00000020;
        public const uint WS_EX_TOOLWINDOW = 0x00000080;
        public const uint WS_EX_LAYERED = 0x00080000;
        public const uint WS_EX_NOACTIVATE = 0x08000000;

        private const uint WM_NCHITTEST = 0x0084;
        private const uint WM_SYSCOMMAND = 0x0112;
        private const long SC_KEYMENU = 0xF100;
        private const int HTTRANSPARENT = -1;

        /// <summary>
        /// The raw platform window the native calls take: the NSWindow on macOS (Avalonia's plain
        /// <see cref="IPlatformHandle.Handle"/> is the view there), the HWND on Windows. 0 before the
        /// native window exists.
        /// </summary>
        internal static nint NativeWindow(TopLevel topLevel) => topLevel?.TryGetPlatformHandle() switch
        {
            IMacOSTopLevelPlatformHandle mac => mac.NSWindow,
            { } handle => handle.Handle,
            null => 0,
        };

        /// <summary>
        /// Injects extra Win32 extended styles into the window at style-application time via
        /// Avalonia's <see cref="Win32Properties.AddWindowStylesCallback"/>. Must be called
        /// before Show() so the styles are in place from the first frame.
        /// </summary>
        /// <remarks>
        /// This is a one-way door, and the shape of the call is why. The callback registered here
        /// is an anonymous lambda that nothing retains, so no equal delegate instance can ever be
        /// handed back to <see cref="Win32Properties.RemoveWindowStylesCallback"/>; and even if one
        /// could, removing a styles callback does not un-apply the bits it already OR'd onto the
        /// HWND. Clearing such a bit by hand is equally futile: the next time Avalonia re-applies
        /// window styles (a resize, a state change, a DPI change) it runs the surviving callbacks
        /// again and silently re-ORs the old mask back on.
        /// <para>
        /// So a style added through this method is permanent for the life of the window. Any window
        /// that needs a <em>togglable</em> extended style must not use this overload at all: it
        /// must register a retained instance-method callback that reads a mutable field, so every
        /// style re-application re-asserts the window's current desire rather than a mask captured
        /// once at construction (<see cref="ToggleableExStyles"/>).
        /// </para>
        /// </remarks>
        public static void AddExStyles(Window window, uint exStyles)
        {
            if (!OperatingSystem.IsWindows())
                return;

            Win32Properties.AddWindowStylesCallback(window, (style, exStyle) => (style, exStyle | exStyles));
        }

        /// <summary>
        /// The open-time setup of an overlay window, in one native call. Call from the window's
        /// Opened handler (the native window must exist), before (re)positioning it:
        /// <see cref="OverlayFlags.CoverMenuBar"/> lifts it to the macOS overlay level, below which
        /// AppKit keeps the frame off the menu bar (issue #56), so a position set earlier would not
        /// stick. <see cref="OverlayFlags.OpaqueLayered"/> is the mandatory follow-up to a
        /// WS_EX_LAYERED style (a layered window without it is never repainted, and Avalonia's
        /// swapchain does not make the call). <see cref="OverlayFlags.ClickThrough"/> lets the mouse
        /// through (on Windows the window keeps WS_EX_TRANSPARENT in its styles callback as well, or
        /// Avalonia's next style re-application drops it); <see cref="OverlayFlags.TakesMouse"/> is the
        /// explicit opposite, for a window that draws over its transparent pixels.
        /// </summary>
        public static void PrepareOverlay(Window window, OverlayFlags flags)
        {
            var handle = NativeWindow(window);
            if (handle != 0)
                WindowChrome.PrepareOverlay(handle, flags);
        }

        /// <summary>
        /// Switches click-through on or off, live: WS_EX_TRANSPARENT on Windows (the window's
        /// <see cref="ToggleableExStyles"/> must be told too, so a style re-application keeps it),
        /// setIgnoresMouseEvents: on macOS. Leaving click-through sends NO explicitly there: AppKit's
        /// default (ignore only transparent pixels) is a third state the window would otherwise never
        /// return to once YES was sent. Call once the native window exists.
        /// </summary>
        public static void SetClickThrough(Window window, bool on)
        {
            var handle = NativeWindow(window);
            if (handle != 0)
                WindowChrome.SetClickThrough(handle, on);
        }

        /// <summary>
        /// Belt-and-braces click-through in addition to WS_EX_TRANSPARENT: answer WM_NCHITTEST
        /// with HTTRANSPARENT so hit-testing falls through to whatever is underneath (exactly
        /// what the WPF-era native BorderWindow did). Register before Show().
        /// </summary>
        public static void AddHitTestTransparentHook(Window window)
        {
            if (!OperatingSystem.IsWindows())
                return;

            Win32Properties.AddWndProcHookCallback(window, HitTestTransparentHook);
        }

        private static IntPtr HitTestTransparentHook(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_NCHITTEST)
            {
                handled = true;
                return new IntPtr(HTTRANSPARENT);
            }

            return IntPtr.Zero;
        }

        /// <summary>
        /// Restores Alt+Space (the window's system menu) on Windows, issue #108. Avalonia's WndProc
        /// swallows every <c>SC_KEYMENU</c> whose lParam has no high word, to stop a bare Alt or
        /// F10 from entering the native menu loop, but Alt+Space arrives as <c>SC_KEYMENU</c> with
        /// lParam = ' ', so it is swallowed too (AvaloniaUI/Avalonia#14545). Hooks run before
        /// Avalonia's own handler, so this one hands just that case to the default window procedure
        /// and leaves the bare-Alt suppression alone. Idempotent; App installs it on every window as
        /// it opens.
        /// </summary>
        public static void AddSystemMenuKeyHook(Window window)
        {
            if (!OperatingSystem.IsWindows())
                return;

            // WindowOpened fires on every Show; keep one registration per window
            Win32Properties.RemoveWndProcHookCallback(window, SystemMenuKeyHook);
            Win32Properties.AddWndProcHookCallback(window, SystemMenuKeyHook);
        }

        private static IntPtr SystemMenuKeyHook(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_SYSCOMMAND && ((long)wParam & 0xFFF0) == SC_KEYMENU && (long)lParam == ' ')
            {
                handled = true;
                return WindowChrome.DefaultWindowProc(hWnd, msg, (nuint)(nint)wParam, lParam);
            }

            return IntPtr.Zero;
        }

        /// <summary>
        /// Re-asserts a topmost window above its topmost peers WITHOUT activating it or moving it.
        /// Used when a second topmost window is shown over an existing one (the share-region resize
        /// overlay over the floating toolbar): the tile that ends resize mode must not end up
        /// underneath it, because there is no keyboard escape from that mode.
        /// </summary>
        /// <remarks>
        /// On Windows, among topmost peers the later Show() wins. A SetWindowPos is deterministic
        /// where a Topmost=false/true bounce goes through Avalonia's window-style path and can
        /// re-apply styles as a side effect.
        /// <para>
        /// On macOS the sibling is activatable and already sits at the overlay level (it was
        /// prepared with <see cref="OverlayFlags.CoverMenuBar"/>), so front-ordering within one level
        /// would not hold: a window that becomes key orders itself to the front of its own level, so
        /// a same-level toolbar would sink again on the first click into the overlay. This window is
        /// therefore lifted one level above it and front-ordered with orderFrontRegardless, which
        /// raises without making the app active or this window key. As with HWND_TOPMOST on Windows,
        /// the raise then stands for the life of the window.
        /// </para>
        /// </remarks>
        public static void RaiseTopmostNoActivate(Window window)
        {
            var handle = NativeWindow(window);
            if (handle != 0)
                WindowChrome.RaiseTopmostNoActivate(handle);
        }

        /// <summary>
        /// Moves AND resizes an undecorated window in one step. Avalonia has no such call: its
        /// <c>Position</c> setter is one SetWindowPos and its <c>Width</c>/<c>Height</c> setters
        /// become a second one only after the next layout pass, so a window whose left edge is
        /// dragged first slides whole (right edge overshooting by the delta) and then snaps back to
        /// size a frame later: the jitter the share-region resize overlay showed. Here the native
        /// rect goes down atomically first, and the Avalonia properties are then set to the SAME
        /// values so its own pass finds nothing to do.
        /// </summary>
        /// <param name="position">Top-left in physical (capture-space) pixels.</param>
        /// <param name="physicalWidth">Window width in physical pixels; the window must have no
        /// system decorations, so this is also the client width.</param>
        /// <param name="physicalHeight">As above.</param>
        /// <param name="scaling">The window's RenderScaling, which the logical size is derived from.
        /// On macOS the caller passes 1: the region there is CG points, which are already logical.</param>
        /// <remarks>
        /// The physical size actually applied is <c>(int)(logical × scaling)</c>, computed exactly the
        /// way Avalonia.Win32's <c>WindowImpl.Resize</c> computes it from the logical size this
        /// method then assigns, so the two agree bit-for-bit and Avalonia early-returns on its own
        /// resize instead of shrinking the window a pixel on the next frame. That truncation can
        /// leave the window one physical pixel short of the requested size at fractional scalings;
        /// it is the same pixel it was short by before this helper existed, which is why both
        /// callers carry a pixel of slack.
        /// <para>On macOS the native step answers "not this phase" and this is exactly the old
        /// two-step: Position, then Width and Height. NSWindow has an atomic setFrame:display:, but it
        /// takes a flipped-origin NSRect that Avalonia's macOS backend translates internally, so it
        /// is left to Avalonia until the native side takes it over (no change at this call site).</para>
        /// </remarks>
        public static void SetPhysicalBounds(Window window, PixelPoint position, int physicalWidth, int physicalHeight, double scaling)
        {
            var logicalWidth = physicalWidth / scaling;
            var logicalHeight = physicalHeight / scaling;

            var handle = NativeWindow(window);
            if (handle != 0)
            {
                // Avalonia.Win32 WindowImpl.Resize: (int)(value.Width * RenderScaling). Match it.
                WindowChrome.SetBounds(handle, position.X, position.Y, (int)(logicalWidth * scaling), (int)(logicalHeight * scaling));
            }

            // Same values into Avalonia so its state agrees with the native window. Where the native
            // step ran both are no-ops against the rect just applied; elsewhere they are the whole
            // operation.
            window.Position = position;
            window.Width = logicalWidth;
            window.Height = logicalHeight;
        }

        /// <summary>
        /// Hides the top level from screen capture, or shows it again: while hidden, DXGI desktop
        /// duplication, Windows Graphics Capture (monitor and window), GDI BitBlt of the screen,
        /// PrintScreen and the Snipping Tool all render whatever is behind it instead, and on macOS
        /// the CGWindowList capture path and ScreenCaptureKit skip it. Which windows are hidden is the
        /// user's choice (<see cref="CaptureExclusion"/> decides and is the only caller). The native
        /// handle must exist, so this runs from Opened or later.
        /// </summary>
        /// <remarks>
        /// Windows: <c>SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)</c>, honoured from Windows 10
        /// 2004 (build 19041) by DWM for every capture API, layered windows included, and
        /// <c>WDA_NONE</c> to undo it. Older builds reject the exclude value; that is swallowed,
        /// because the alternative (WDA_MONITOR) would black the window out rather than remove it.
        /// The affinity is a property of the HWND and survives Avalonia's style re-applications,
        /// unlike an extended style.
        /// <para>macOS: <c>NSWindow.sharingType</c>, <c>NSWindowSharingNone</c> to hide and AppKit's
        /// default <c>NSWindowSharingReadOnly</c> to show.</para>
        /// </remarks>
        public static void SetExcludedFromScreenCapture(TopLevel topLevel, bool excluded)
        {
            var handle = NativeWindow(topLevel);
            if (handle != 0)
                WindowChrome.SetExcludedFromCapture(handle, excluded);
        }

        /// <summary>
        /// macOS: places the traffic lights at an explicit inset from the window's top-left,
        /// instead of the (9, 9) AppKit gives a bare window. Call once the NSWindow exists, and
        /// again after anything that relays the title bar out (see the remarks). No-op elsewhere.
        /// </summary>
        /// <param name="leftInset">Left edge of the close button, in points from the window's left.</param>
        /// <param name="topInset">Top edge of all three buttons, in points from the window's top.
        /// Free to exceed the title bar's own 32pt height: NSTitlebarView does not clip, so a
        /// button can sit below the bar, over the window's content (verified on macOS 26).</param>
        /// <remarks>
        /// This has to be re-applied, which is the whole cost of the approach: AppKit resets all
        /// three frames to (9, 9) on every title bar relayout, which is a resize, a zoom, a
        /// minimise and restore, and both fullscreen transitions. The alternative that maintains
        /// itself is an empty unified NSToolbar, but that one buys its placement by growing the
        /// title bar to 52pt, which drags the window's own top row down with it.
        /// </remarks>
        public static void SetTrafficLightPosition(Window window, double leftInset, double topInset)
        {
            var handle = NativeWindow(window);
            if (handle != 0)
                WindowChrome.SetTrafficLightPosition(handle, leftInset, topInset);
        }

        /// <summary>Whether <paramref name="window"/> has the keyboard: the system's foreground window on
        /// Windows, the key window of the active app on macOS. Not the same as
        /// <see cref="WindowBase.IsActive"/> on Windows: a SetForegroundWindow refused by the
        /// foreground lock still makes the window its thread's active one, so Avalonia reports it
        /// active while every key goes to the app in front. False before the window has a handle.</summary>
        public static bool IsForeground(Window window)
        {
            var handle = NativeWindow(window);
            return handle != 0 && WindowChrome.IsForeground(handle);
        }

        /// <summary>
        /// Windows: brings <paramref name="window"/> to the foreground past the foreground lock, which
        /// refuses SetForegroundWindow to a process that did not receive the last input, and a
        /// global hotkey swallowed by the low-level keyboard hook was never delivered to us as input
        /// (<see cref="WindowChrome.TakeForeground"/> has the Alt-key unlock). Returns whether the
        /// window ended up in front. False on macOS, where callers activate through Avalonia, and
        /// before the window has a handle.
        /// </summary>
        public static bool ForceForeground(Window window)
        {
            var handle = NativeWindow(window);
            return handle != 0 && WindowChrome.TakeForeground(handle);
        }

        /// <summary>
        /// Windows: calls <paramref name="changed"/> whenever the system's foreground window changes,
        /// to any window of any process, until disposed. Call on the UI thread, and dispose there: the
        /// hook is out of context, so it is delivered through the calling thread's message loop, and
        /// <paramref name="changed"/> runs synchronously from it. This is the event for
        /// <see cref="IsForeground"/>; <see cref="WindowBase.IsActive"/> is not, as the two drift
        /// apart (a refused SetForegroundWindow sets one and not the other), and a foreground lost
        /// while they disagree raises no IsActive change at all. Null off Windows, or when Windows
        /// refuses the hook.
        /// </summary>
        public static IDisposable WatchForeground(Action changed)
        {
            try
            {
                return ForegroundWatch.Start(changed);
            }
            catch (InteropException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Win32 extended styles for a window where some bits stay on for its whole life and others
    /// are switched at runtime (the draw-on-screen canvases: layered/toolwindow/noactivate always,
    /// WS_EX_TRANSPARENT only while clicks go through to the desktop). Construct before Show().
    /// No-op off Windows.
    /// </summary>
    /// <remarks>
    /// <see cref="WindowNativeExtensions.AddExStyles"/> cannot be used for this: it is one-way. Its
    /// lambda captures the mask once and nothing retains it, so every later Avalonia style
    /// re-application (a resize, a state or DPI change) silently re-ORs a bit that was cleared by
    /// hand. Here ONE instance method is registered and kept alive by this object, and it reads
    /// <see cref="IsOn"/> each time, so a re-application re-asserts the current desire instead of
    /// the construction-time one: the toggled bits are forced on or masked out, never left to
    /// whatever was there before. This object only keeps that desire; the live switch of the
    /// window's current style is <see cref="WindowNativeExtensions.SetClickThrough"/>.
    /// </remarks>
    public sealed class ToggleableExStyles
    {
        private readonly uint _alwaysOn;
        private readonly uint _toggled;

        // the registered callback, held so the delegate Avalonia keeps is the one we created
        private readonly Win32Properties.CustomWindowStylesCallback _callback;

        public ToggleableExStyles(Window window, uint alwaysOn, uint toggled, bool initiallyOn)
        {
            _alwaysOn = alwaysOn;
            _toggled = toggled;
            IsOn = initiallyOn;
            _callback = Apply;

            if (OperatingSystem.IsWindows())
                Win32Properties.AddWindowStylesCallback(window, _callback);
        }

        /// <summary>Whether the toggled bits are wanted on at the next style re-application.</summary>
        public bool IsOn { get; set; }

        private (uint style, uint exStyle) Apply(uint style, uint exStyle) =>
            (style, ((exStyle | _alwaysOn) & ~_toggled) | (IsOn ? _toggled : 0));
    }
}

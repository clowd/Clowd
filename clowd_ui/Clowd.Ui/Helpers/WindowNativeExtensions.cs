using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace Clowd.UI.Helpers
{
    /// <summary>
    /// Native window-style helpers for the recording UI windows (BorderWindow,
    /// ShareResizeWindow and FloatingTrayWindow, design §4.2). Every member is safe to call on any OS —
    /// each one is a no-op off its own platform, so callers need no cfg guards.
    /// </summary>
    internal static class WindowNativeExtensions
    {
        public const uint WS_EX_TRANSPARENT = 0x00000020;
        public const uint WS_EX_TOOLWINDOW = 0x00000080;
        public const uint WS_EX_LAYERED = 0x00080000;
        public const uint WS_EX_NOACTIVATE = 0x08000000;

        private const uint WM_NCHITTEST = 0x0084;
        private const int HTTRANSPARENT = -1;
        private const uint LWA_ALPHA = 0x00000002;

        // SetWindowDisplayAffinity values. WDA_EXCLUDEFROMCAPTURE (Windows 10 2004+) removes the
        // window from every capture path; the older WDA_MONITOR only paints it black, which is
        // worse than nothing for a frame, so it is deliberately not used as a fallback.
        private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

        // NSWindowSharingNone
        private const nuint NSWindowSharingNone = 0;

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOOWNERZORDER = 0x0200;

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
        /// HWND. Clearing such a bit by hand with SetWindowLongPtr is equally futile: the next time
        /// Avalonia re-applies window styles (a resize, a state change, a DPI change) it runs the
        /// surviving callbacks again and silently re-ORs the old mask back on.
        /// <para>
        /// So a style added through this method is permanent for the life of the window. Any window
        /// that needs a <em>togglable</em> extended style must not use this overload at all — it
        /// must register a retained instance-method callback that reads a mutable field, so every
        /// style re-application re-asserts the window's current desire rather than a mask captured
        /// once at construction.
        /// </para>
        /// </remarks>
        public static void AddExStyles(Window window, uint exStyles)
        {
            if (!OperatingSystem.IsWindows())
                return;

            Win32Properties.AddWindowStylesCallback(window, (style, exStyle) => (style, exStyle | exStyles));
        }

        /// <summary>
        /// Mandatory follow-up to WS_EX_LAYERED: a window that gains the layered style without a
        /// subsequent SetLayeredWindowAttributes/UpdateLayeredWindow call is never repainted, and
        /// Avalonia's swapchain does not make one. Call from the window's Opened handler.
        /// </summary>
        public static void SetLayeredFullyOpaque(Window window)
        {
            if (!OperatingSystem.IsWindows())
                return;

            var handle = window.TryGetPlatformHandle();
            if (handle != null && handle.Handle != IntPtr.Zero)
                SetLayeredWindowAttributes(handle.Handle, 0, 255, LWA_ALPHA);
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
        /// Re-asserts a topmost window above its topmost peers WITHOUT activating it or moving it.
        /// Used when a second topmost window is shown over an existing one (the share-region resize
        /// overlay over the floating toolbar): the tile that ends resize mode must not end up
        /// underneath it — there is no keyboard escape from that mode. No-op off Windows and macOS.
        /// </summary>
        /// <remarks>
        /// On Windows, among topmost peers the later Show() wins. A SetWindowPos is deterministic
        /// where a Topmost=false/true bounce goes through Avalonia's window-style path and can
        /// re-apply styles as a side effect.
        /// <para>
        /// On macOS the sibling is activatable and already sits at NSStatusWindowLevel (it calls
        /// <see cref="SetCanCoverMenuBar"/>), so front-ordering within one level would not hold: a
        /// window that becomes key orders itself to the front of its own level, so a same-level
        /// toolbar would sink again on the first click into the overlay. This window is therefore
        /// lifted one level above it and front-ordered with orderFrontRegardless, which raises
        /// without making the app active or this window key. As with HWND_TOPMOST on Windows, the
        /// raise then stands for the life of the window.
        /// </para>
        /// </remarks>
        public static void RaiseTopmostNoActivate(Window window)
        {
            var handle = window?.TryGetPlatformHandle();
            if (handle == null)
                return;

            if (OperatingSystem.IsWindows())
            {
                if (handle.Handle != IntPtr.Zero)
                    SetWindowPos(handle.Handle, HWND_TOPMOST, 0, 0, 0, 0,
                                 SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            else if (OperatingSystem.IsMacOS())
            {
                if (handle is IMacOSTopLevelPlatformHandle mac && mac.NSWindow != IntPtr.Zero)
                {
                    objc_msgSend(mac.NSWindow, sel_registerName("setLevel:"), AboveOverlayWindowLevel);
                    objc_msgSend(mac.NSWindow, sel_registerName("orderFrontRegardless"));
                }
            }
        }

        /// <summary>
        /// Moves AND resizes an undecorated window in one step. Avalonia has no such call: its
        /// <c>Position</c> setter is one SetWindowPos and its <c>Width</c>/<c>Height</c> setters
        /// become a second one only after the next layout pass, so a window whose left edge is
        /// dragged first slides whole (right edge overshooting by the delta) and then snaps back to
        /// size a frame later — the jitter the share-region resize overlay showed. Here the native
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
        /// <para>Off Windows this is exactly the old two-step: Position, then Width and Height, so
        /// the drag there is no worse than it was. NSWindow has an atomic setFrame:display:, but it
        /// takes a flipped-origin NSRect that Avalonia's macOS backend translates internally and
        /// this code cannot be tested here (design §4.2), so it is left to Avalonia.</para>
        /// </remarks>
        public static void SetPhysicalBounds(Window window, PixelPoint position, int physicalWidth, int physicalHeight, double scaling)
        {
            var logicalWidth = physicalWidth / scaling;
            var logicalHeight = physicalHeight / scaling;

            if (OperatingSystem.IsWindows())
            {
                var handle = window?.TryGetPlatformHandle();
                if (handle != null && handle.Handle != IntPtr.Zero)
                {
                    // Avalonia.Win32 WindowImpl.Resize: (int)(value.Width * RenderScaling). Match it.
                    var cx = (int)(logicalWidth * scaling);
                    var cy = (int)(logicalHeight * scaling);
                    SetWindowPos(handle.Handle, IntPtr.Zero, position.X, position.Y, cx, cy,
                                 SWP_NOACTIVATE | SWP_NOZORDER | SWP_NOOWNERZORDER);
                }
            }

            // Same values into Avalonia so its state agrees with the HWND. On Windows both are
            // no-ops against the rect just applied; elsewhere they are the whole operation.
            window.Position = position;
            window.Width = logicalWidth;
            window.Height = logicalHeight;
        }

        /// <summary>
        /// Hides the window from screen capture: DXGI desktop duplication, Windows Graphics Capture
        /// (monitor and window), GDI BitBlt of the screen, PrintScreen and the Snipping Tool all
        /// render whatever is behind it instead. For a window drawn over the region being recorded
        /// or shared this is the backstop under the geometry: no accent pixel, wash, handle or
        /// indicator can reach the recording or the meeting even if a DPI rounding puts it inside
        /// the region. Call from the window's Opened handler — the native handle must exist.
        /// </summary>
        /// <remarks>
        /// Windows: <c>SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)</c>, honoured from Windows 10
        /// 2004 (build 19041) by DWM for every capture API, layered windows included. Older builds
        /// reject the value and the call returns false; that is swallowed, because the window's own
        /// geometry keeps it out of the recording on its own and the alternative (WDA_MONITOR) would
        /// black the window out rather than remove it. The affinity is a property of the HWND and
        /// survives Avalonia's style re-applications, unlike an extended style.
        /// <para>macOS: <c>NSWindow.sharingType = NSWindowSharingNone</c>, the per-window opt-out
        /// that the CGWindowList capture path and ScreenCaptureKit honour. Untested here —
        /// compile-guarded per design §4.2.</para>
        /// </remarks>
        public static void ExcludeFromScreenCapture(Window window)
        {
            var handle = window?.TryGetPlatformHandle();
            if (handle == null)
                return;

            if (OperatingSystem.IsWindows())
            {
                if (handle.Handle != IntPtr.Zero)
                    SetWindowDisplayAffinity(handle.Handle, WDA_EXCLUDEFROMCAPTURE);
            }
            else if (OperatingSystem.IsMacOS())
            {
                if (handle is IMacOSTopLevelPlatformHandle mac && mac.NSWindow != IntPtr.Zero)
                    objc_msgSend(mac.NSWindow, sel_registerName("setSharingType:"), NSWindowSharingNone);
            }
        }

        /// <summary>
        /// macOS click-through: NSWindow setIgnoresMouseEvents:YES via objc_msgSend on the
        /// window's <see cref="IMacOSTopLevelPlatformHandle"/>. Call from the window's Opened
        /// handler (the NSWindow must exist). Untested here — compile-guarded per design §4.2.
        /// </summary>
        public static void SetIgnoresMouseEvents(Window window)
        {
            if (!OperatingSystem.IsMacOS())
                return;

            if (window.TryGetPlatformHandle() is IMacOSTopLevelPlatformHandle mac && mac.NSWindow != IntPtr.Zero)
                objc_msgSend(mac.NSWindow, sel_registerName("setIgnoresMouseEvents:"), true);
        }

        // NSStatusWindowLevel — one above NSMainMenuWindowLevel (24), same level the Rust
        // capturer overlay uses.
        private const nint NSStatusWindowLevel = 25;

        // One above that, for a window that must stay clickable over an overlay window which can
        // itself become key (RaiseTopmostNoActivate).
        private const nint AboveOverlayWindowLevel = NSStatusWindowLevel + 1;

        // CanJoinAllSpaces | Stationary | IgnoresCycle | FullScreenAuxiliary
        private const nuint OverlayCollectionBehavior = (1 << 0) | (1 << 4) | (1 << 6) | (1 << 8);

        /// <summary>
        /// macOS: lets an overlay window cover the menu bar and fullscreen apps, matching the
        /// capturer overlay. AppKit constrains the frame of any window below
        /// NSMainMenuWindowLevel so it can never overlap the menu bar (issue #56) — raising to
        /// NSStatusWindowLevel lifts that constraint. Call from Opened, before (re)positioning.
        /// </summary>
        public static void SetCanCoverMenuBar(Window window)
        {
            if (!OperatingSystem.IsMacOS())
                return;

            if (window.TryGetPlatformHandle() is IMacOSTopLevelPlatformHandle mac && mac.NSWindow != IntPtr.Zero)
            {
                objc_msgSend(mac.NSWindow, sel_registerName("setLevel:"), NSStatusWindowLevel);
                objc_msgSend(mac.NSWindow, sel_registerName("setCollectionBehavior:"), OverlayCollectionBehavior);
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte bAlpha, uint dwFlags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
                                                int X, int Y, int cx, int cy, uint uFlags);

        private const string LibObjC = "/usr/lib/libobjc.A.dylib";

        [DllImport(LibObjC)]
        private static extern IntPtr sel_registerName([MarshalAs(UnmanagedType.LPStr)] string name);

        [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
        private static extern void objc_msgSend(IntPtr receiver, IntPtr selector);

        // ObjC BOOL is a signed char — marshal as I1, not the 4-byte Win32 BOOL default.
        [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
        private static extern void objc_msgSend(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.I1)] bool arg1);

        [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
        private static extern void objc_msgSend(IntPtr receiver, IntPtr selector, nint arg1);

        [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
        private static extern void objc_msgSend(IntPtr receiver, IntPtr selector, nuint arg1);
    }
}

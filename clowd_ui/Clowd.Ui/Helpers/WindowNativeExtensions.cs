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
        private const uint SWP_FRAMECHANGED = 0x0020;

        private const int GWL_EXSTYLE = -20;

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
        public static void SetIgnoresMouseEvents(Window window) => SetIgnoresMouseEvents(window, true);

        /// <summary>
        /// macOS: switches click-through on or off (NSWindow setIgnoresMouseEvents:). Leaving
        /// click-through must send NO explicitly — AppKit's default (ignore only transparent
        /// pixels) is a third state the window would otherwise never return to once YES was sent.
        /// No-op elsewhere. Untested here — compile-guarded per design §4.2.
        /// </summary>
        public static void SetIgnoresMouseEvents(Window window, bool ignore)
        {
            if (!OperatingSystem.IsMacOS())
                return;

            if (window.TryGetPlatformHandle() is IMacOSTopLevelPlatformHandle mac && mac.NSWindow != IntPtr.Zero)
                objc_msgSend(mac.NSWindow, sel_registerName("setIgnoresMouseEvents:"), ignore);
        }

        /// <summary>
        /// macOS: places the traffic lights at an explicit inset from the window's top-left,
        /// instead of the (9, 9) AppKit gives a bare window. Call once the NSWindow exists, and
        /// again after anything that relays the title bar out (see the remarks).
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
            if (!OperatingSystem.IsMacOS())
                return;

            if (window?.TryGetPlatformHandle() is not IMacOSTopLevelPlatformHandle mac || mac.NSWindow == IntPtr.Zero)
                return;

            var standardWindowButton = sel_registerName("standardWindowButton:");
            var setFrameOrigin = sel_registerName("setFrameOrigin:");

            // Read all three frames before moving any of them. AppKit's own spacing and button
            // size are then preserved exactly: the cluster is translated as a unit rather than
            // laid out again from constants, which matters because neither is fixed. Under a
            // title bar shorter than 32pt AppKit draws the small variant, where the circles are
            // 12pt on 20pt centres in 14x16 frames instead of 14pt on 23pt centres.
            var buttons = new IntPtr[3];
            var frames = new NSRect[3];
            var titleBar = IntPtr.Zero;

            for (var i = 0; i < 3; i++)
            {
                buttons[i] = objc_msgSend_IntPtr(mac.NSWindow, standardWindowButton, (nuint)i);
                if (buttons[i] == IntPtr.Zero)
                    return;

                frames[i] = ViewFrame(buttons[i]);

                if (titleBar == IntPtr.Zero)
                    titleBar = objc_msgSend_IntPtr(buttons[i], sel_registerName("superview"));
            }

            if (titleBar == IntPtr.Zero)
                return;

            // The three share one superview (NSTitlebarView), whose height converts a top inset
            // into the bottom-up y AppKit wants: the view is not flipped, so y counts up from the
            // bar's bottom edge and a negative value simply puts a button below the bar.
            var titleBarHeight = ViewFrame(titleBar).Height;
            if (titleBarHeight <= 0)
                return;

            // One shift for all three, taken from the close button. Idempotent: re-running against
            // frames this method already placed computes a zero shift and the same y.
            var shift = leftInset - frames[0].X;

            for (var i = 0; i < 3; i++)
            {
                var origin = new NSPoint
                {
                    X = frames[i].X + shift,
                    Y = titleBarHeight - frames[i].Height - topInset,
                };

                objc_msgSend(buttons[i], setFrameOrigin, origin);
            }
        }

        /// <summary>A view's frame. The struct is 32 bytes, which the two ABIs return differently:
        /// arm64 hands back a hidden pointer through plain objc_msgSend, where x86_64 needs the
        /// _stret entry point (the same split the NSPoint helper above sits on the other side of,
        /// two doubles being small enough for registers everywhere).</summary>
        private static NSRect ViewFrame(IntPtr view)
        {
            var frame = sel_registerName("frame");

            if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
            {
                objc_msgSend_stret(out var rect, view, frame);
                return rect;
            }

            return objc_msgSend_NSRect(view, frame);
        }

        /// <summary>
        /// Windows: sets or clears <paramref name="mask"/> in the live window's extended style and
        /// makes the change take effect now (SWP_FRAMECHANGED). On its own this lasts only until
        /// Avalonia next re-applies window styles; a togglable style must also be re-asserted by a
        /// retained styles callback, which is what <see cref="ToggleableExStyles"/> pairs it with.
        /// No-op off Windows or before the window has a handle.
        /// </summary>
        public static void SetExStyleBits(Window window, uint mask, bool on)
        {
            if (!OperatingSystem.IsWindows())
                return;

            var handle = window?.TryGetPlatformHandle();
            if (handle == null || handle.Handle == IntPtr.Zero)
                return;

            var ex = (ulong)GetWindowLongPtrW(handle.Handle, GWL_EXSTYLE).ToInt64();
            var next = on ? ex | mask : ex & ~(ulong)mask;
            if (next == ex)
                return;

            SetWindowLongPtrW(handle.Handle, GWL_EXSTYLE, new IntPtr((long)next));
            SetWindowPos(handle.Handle, IntPtr.Zero, 0, 0, 0, 0,
                         SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }

        /// <summary>The current foreground window (Windows), or <see cref="IntPtr.Zero"/> elsewhere.
        /// Remembered before a NOACTIVATE window takes focus so it can be handed back.</summary>
        public static IntPtr GetForegroundWindowHandle() =>
            OperatingSystem.IsWindows() ? GetForegroundWindow() : IntPtr.Zero;

        /// <summary>Hands the foreground back to <paramref name="hwnd"/> if that window still
        /// exists. No-op off Windows or for a null handle.</summary>
        public static void TryRestoreForeground(IntPtr hwnd)
        {
            if (!OperatingSystem.IsWindows() || hwnd == IntPtr.Zero)
                return;

            if (IsWindow(hwnd))
                SetForegroundWindow(hwnd);
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

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr hWnd);

        private const string LibObjC = "/usr/lib/libobjc.A.dylib";

        [DllImport(LibObjC)]
        internal static extern IntPtr sel_registerName([MarshalAs(UnmanagedType.LPStr)] string name);

        [DllImport(LibObjC)]
        internal static extern IntPtr objc_getClass([MarshalAs(UnmanagedType.LPStr)] string name);

        // NSPoint-returning message (NSEvent +mouseLocation). Two doubles come back in registers on
        // arm64 and x86_64 alike, so plain objc_msgSend is correct; objc_msgSend_stret is for
        // larger structs only.
        [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
        internal static extern NSPoint objc_msgSend_NSPoint(IntPtr receiver, IntPtr selector);

        [StructLayout(LayoutKind.Sequential)]
        internal struct NSPoint
        {
            public double X;
            public double Y;
        }

        [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
        private static extern void objc_msgSend(IntPtr receiver, IntPtr selector);

        // Object-returning message (+alloc, -init). nint being IntPtr, the argument overloads
        // below already carry an object parameter; only the return type needs its own entry.
        [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
        private static extern IntPtr objc_msgSend_IntPtr(IntPtr receiver, IntPtr selector);

        // ObjC BOOL is a signed char — marshal as I1, not the 4-byte Win32 BOOL default.
        [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
        private static extern void objc_msgSend(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.I1)] bool arg1);

        [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
        private static extern void objc_msgSend(IntPtr receiver, IntPtr selector, nint arg1);

        [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
        private static extern void objc_msgSend(IntPtr receiver, IntPtr selector, nuint arg1);

        [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
        private static extern void objc_msgSend(IntPtr receiver, IntPtr selector, NSPoint arg1);

        [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
        private static extern IntPtr objc_msgSend_IntPtr(IntPtr receiver, IntPtr selector, nuint arg1);

        // NSRect-returning message (-[NSView frame]); see ViewFrame for the ABI split.
        [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
        private static extern NSRect objc_msgSend_NSRect(IntPtr receiver, IntPtr selector);

        [DllImport(LibObjC, EntryPoint = "objc_msgSend_stret")]
        private static extern void objc_msgSend_stret(out NSRect result, IntPtr receiver, IntPtr selector);

        [StructLayout(LayoutKind.Sequential)]
        internal struct NSRect
        {
            public double X;
            public double Y;
            public double Width;
            public double Height;
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
    /// whatever was there before.
    /// </remarks>
    public sealed class ToggleableExStyles
    {
        private readonly Window _window;
        private readonly uint _alwaysOn;
        private readonly uint _toggled;

        // the registered callback, held so the delegate Avalonia keeps is the one we created
        private readonly Win32Properties.CustomWindowStylesCallback _callback;

        public ToggleableExStyles(Window window, uint alwaysOn, uint toggled, bool initiallyOn)
        {
            _window = window;
            _alwaysOn = alwaysOn;
            _toggled = toggled;
            IsOn = initiallyOn;
            _callback = Apply;

            if (OperatingSystem.IsWindows())
                Win32Properties.AddWindowStylesCallback(window, _callback);
        }

        /// <summary>Whether the toggled bits are currently wanted on.</summary>
        public bool IsOn { get; private set; }

        /// <summary>Switches the toggled bits on or off, live.</summary>
        public void Set(bool on)
        {
            IsOn = on;
            WindowNativeExtensions.SetExStyleBits(_window, _toggled, on);
        }

        private (uint style, uint exStyle) Apply(uint style, uint exStyle) =>
            (style, ((exStyle | _alwaysOn) & ~_toggled) | (IsOn ? _toggled : 0));
    }
}

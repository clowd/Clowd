using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;

namespace Clowd.UI.Helpers
{
    /// <summary>
    /// Native window-style helpers for the recording UI windows (BorderWindow and
    /// FloatingToolbarWindow, design §4.2). Every member is safe to call on any OS —
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

        /// <summary>
        /// Injects extra Win32 extended styles into the window at style-application time via
        /// Avalonia's <see cref="Win32Properties.AddWindowStylesCallback"/>. Must be called
        /// before Show() so the styles are in place from the first frame.
        /// </summary>
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

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte bAlpha, uint dwFlags);

        private const string LibObjC = "/usr/lib/libobjc.A.dylib";

        [DllImport(LibObjC)]
        private static extern IntPtr sel_registerName([MarshalAs(UnmanagedType.LPStr)] string name);

        // ObjC BOOL is a signed char — marshal as I1, not the 4-byte Win32 BOOL default.
        [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
        private static extern void objc_msgSend(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.I1)] bool arg1);
    }
}

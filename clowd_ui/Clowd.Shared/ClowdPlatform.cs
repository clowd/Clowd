using System;
using System.Runtime.Versioning;

namespace Clowd
{
    /// <summary>
    /// What this desktop can do, as the one place every Linux decision keys off. On Windows and
    /// macOS every Supports* flag is true and every Linux-only flag false, so nothing there changes.
    /// Linux has two capture worlds: an X11 session runs the clowd_capture overlay much as Windows
    /// does; a Wayland session gives no process the screen, so screenshots go through
    /// clowd_capture_wayland (the desktop's own screenshot UI via xdg-desktop-portal) and
    /// recordings through obs-express's own screen-share picker. The session type decides, not
    /// whether an X display exists: Avalonia and clowd_capture both still run under XWayland there.
    /// The rule is the one clowd_capture_wayland and obs-express use, so all three always agree.
    /// </summary>
    public static class ClowdPlatform
    {
        [SupportedOSPlatformGuard("linux")]
        public static bool IsLinux => OperatingSystem.IsLinux();

        /// <summary>A Linux Wayland session (read once from the environment at startup).</summary>
        [SupportedOSPlatformGuard("linux")]
        public static bool IsWayland { get; } = OperatingSystem.IsLinux()
            && IsWaylandSession(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"),
                                Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

        /// <summary>A Linux session that is not Wayland (X11).</summary>
        [SupportedOSPlatformGuard("linux")]
        public static bool IsX11 => OperatingSystem.IsLinux() && !IsWayland;

        /// <summary>Pure, testable detection: XDG_SESSION_TYPE equal to "wayland" (ignoring case), or
        /// WAYLAND_DISPLAY non-empty. Either one means Wayland; Wayland wins over DISPLAY.</summary>
        public static bool IsWaylandSession(string xdgSessionType, string waylandDisplay)
            => String.Equals(xdgSessionType, "wayland", StringComparison.OrdinalIgnoreCase)
            || !String.IsNullOrEmpty(waylandDisplay);

        /// <summary>The clowd_capture overlay can run (false on Wayland: screenshots use the portal binary).</summary>
        public static bool SupportsCaptureOverlay => !IsWayland;

        /// <summary>SharpHook/libuiohook global hotkeys work (false on Wayland). Also gates the Hotkeys page.</summary>
        public static bool SupportsGlobalHotkeys => !IsWayland;

        /// <summary>The standby (--standby) capturer can run. clowd_capture exits 4 for --standby on Linux,
        /// so the supervisor must never be constructed there.</summary>
        public static bool SupportsWarmCapturer => !IsLinux;

        /// <summary>The overlay offers SCROLL and clowd_scroll_driver exists (not built for Linux).</summary>
        public static bool SupportsScrollCapture => !IsLinux;

        /// <summary>Share Region works (clowd_share_region is not built or shipped on Linux).</summary>
        public static bool SupportsShareRegion => !IsLinux;

        /// <summary>The overlay offers reverse image search (forced off on Linux).</summary>
        public static bool SupportsImageSearch => !IsLinux;

        /// <summary>The overlay's lifted-text (OCR) strip offers COPY and SEARCH. Both are compiled
        /// out on Linux, leaving UPLOAD as the strip's only action there.</summary>
        public static bool SupportsOcrCopy => !IsLinux;

        /// <summary>The overlay can preselect the active window (--capture-mode window becomes region on Linux).</summary>
        public static bool SupportsActiveWindowCapture => !IsLinux;

        /// <summary>The overlay can pick a recording region (--video). Forced off on all of Linux in the
        /// Rust first pass; when it is off, a recording starts obs-express with no region and it
        /// chooses the source itself. Flip to <c>!IsWayland</c> once clowd_capture supports --video on X11.</summary>
        public static bool OverlayPicksRecordingRegion => !IsLinux;

        /// <summary>obs-express opens the system screen-share picker while it starts (Wayland);
        /// --region/--monitor are rejected there (exit 2).</summary>
        public static bool RecorderShowsSourcePicker => IsWayland;

        /// <summary>obs-express accepts --input-capture / --window-capture (rejected on Linux, exit 2).</summary>
        public static bool SupportsRecordingSidecars => !IsLinux;

        /// <summary>obs-express accepts the click tracker ("tracker": true is rejected on Linux).</summary>
        public static bool SupportsClickTracker => !IsLinux;
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Prisc.Interop;

namespace Prisc.UI.Helpers
{
    /// <summary>A monitor, in the same shapes Avalonia's <see cref="Screens"/> exposes: physical
    /// pixel rectangles on Windows, CG points (logical units) on macOS.</summary>
    internal sealed record DesktopScreen
    {
        public PixelRect Bounds { get; init; }
        public PixelRect WorkingArea { get; init; }
        public double Scaling { get; init; } = 1.0;
        public bool IsPrimary { get; init; }

        /// <summary>The panel's current refresh rate in Hz, or 0 when the platform would not say
        /// (a failed query, or a display mode that reports none, as some built-in and variable-rate
        /// panels do). Callers treat 0 as "unknown", never as a rate.</summary>
        public double RefreshRate { get; init; }
    }

    /// <summary>
    /// Monitor lookup for window-placement math. Use this instead of <see cref="Window.Screens"/>.
    ///
    /// Avalonia caches its screen list process-wide and, on Windows, invalidates it only from a
    /// window procedure: each live window's WM_DISPLAYCHANGE, or the hidden message window's
    /// WM_SETTINGCHANGE(SPI_SETWORKAREA); the message window ignores WM_DISPLAYCHANGE itself.
    /// Prisc runs tray-only for long stretches with no Avalonia window alive, so a display change
    /// in that state is never observed and the cache goes stale for the rest of the process. In
    /// the worst case the cache holds the 1024x768 fallback monitor Windows reports while the
    /// real display is still handshaking at logon, and every subsequent placement clamps into the
    /// top-left corner of the desktop (the share-region toolbar bug of 2026-09-11).
    ///
    /// On Windows every call here therefore asks the OS directly through
    /// <see cref="NativeScreens"/> (the screen list the capture overlay and the share-region
    /// prompt read too), always current. On macOS the Avalonia cache is trusted for bounds, work
    /// area and scaling: libAvaloniaNative refreshes it from applicationDidChangeScreenParameters:,
    /// an app-level notification that needs no window, so the Windows failure mode does not exist
    /// there, and only AppKit knows the work area. Avalonia has no refresh rate, so that one value
    /// comes from <see cref="NativeScreens"/>' CoreGraphics list, matched by position.
    /// </summary>
    internal static class DesktopScreens
    {
        /// <summary>The monitor containing the point, or null if the point is on none: the same
        /// contract as Screens.ScreenFromPoint. The point is in physical px on Windows / CG points
        /// on macOS, matching capture-space regions.</summary>
        public static DesktopScreen FromPoint(WindowBase window, PixelPoint point)
        {
            if (!OperatingSystem.IsWindows())
                return FromAvalonia(window?.Screens?.ScreenFromPoint(point));

            return Ask(() => NativeScreens.AtPoint(point.X, point.Y), nameof(FromPoint));
        }

        /// <summary>The monitor with the largest intersection with the rect, or null if it touches
        /// none: the same contract as Screens.ScreenFromBounds.</summary>
        public static DesktopScreen FromRect(WindowBase window, PixelRect rect)
        {
            if (!OperatingSystem.IsWindows())
                return FromAvalonia(window?.Screens?.ScreenFromBounds(rect));

            return Ask(() => NativeScreens.AtRect(rect.X, rect.Y, rect.Width, rect.Height), nameof(FromRect));
        }

        /// <summary>The primary monitor, or null if none can be enumerated.</summary>
        public static DesktopScreen Primary(WindowBase window)
        {
            if (!OperatingSystem.IsWindows())
                return FromAvalonia(window?.Screens?.Primary);

            return Ask(NativeScreens.Primary, nameof(Primary));
        }

        /// <summary>Every monitor, freshly enumerated on Windows. Empty rather than null when
        /// enumeration fails.</summary>
        public static IReadOnlyList<DesktopScreen> All(WindowBase window)
        {
            if (!OperatingSystem.IsWindows())
            {
                var screens = window?.Screens?.All;
                if (screens == null)
                    return Array.Empty<DesktopScreen>();
                var native = NativeList();
                var mapped = new List<DesktopScreen>(screens.Count);
                foreach (var s in screens)
                {
                    var m = FromAvalonia(s, native);
                    if (m != null)
                        mapped.Add(m);
                }
                return mapped;
            }

            var list = NativeList();
            var result = new List<DesktopScreen>(list.Length);
            foreach (var screen in list)
                result.Add(FromNative(screen));
            return result;
        }

        /// <summary>One native lookup; a failed OS call reads as "no monitor" (and is reported),
        /// exactly as the old direct user32 calls did.</summary>
        private static DesktopScreen Ask(Func<ScreenInfo?> lookup, string what)
        {
            try
            {
                return lookup() is { } screen ? FromNative(screen) : null;
            }
            catch (InteropException ex)
            {
                Report(ex, what);
                return null;
            }
        }

        /// <summary>The native list, or empty when the OS enumeration failed (reported).</summary>
        private static ScreenInfo[] NativeList()
        {
            try
            {
                return NativeScreens.List();
            }
            catch (InteropException ex)
            {
                Report(ex, nameof(All));
                return Array.Empty<ScreenInfo>();
            }
        }

        private static void Report(InteropException ex, string what)
        {
            Debug.WriteLine($"DesktopScreens.{what}: {ex.Message}");
            SentryConfig.CaptureHandled(ex, "screens." + what);
        }

        private static DesktopScreen FromAvalonia(Avalonia.Platform.Screen screen, ScreenInfo[] native = null)
        {
            if (screen == null)
                return null;
            native ??= NativeList();
            return new DesktopScreen
            {
                Bounds = screen.Bounds,
                WorkingArea = screen.WorkingArea,
                Scaling = screen.Scaling,
                IsPrimary = screen.IsPrimary,
                RefreshRate = RefreshRateAt(native, screen.Bounds),
            };
        }

        /// <summary>
        /// The refresh rate of the native screen under the centre of <paramref name="bounds"/> (CG
        /// points on macOS, the space both Avalonia's screen bounds and the CoreGraphics list are
        /// in). 0 when no native screen is there or it reports none.
        /// </summary>
        private static double RefreshRateAt(ScreenInfo[] native, PixelRect bounds)
        {
            double x = bounds.X + bounds.Width / 2.0, y = bounds.Y + bounds.Height / 2.0;
            foreach (var screen in native)
            {
                if (screen.Contains(x, y))
                    return screen.RefreshHz;
            }
            return 0;
        }

        private static DesktopScreen FromNative(ScreenInfo s) => new()
        {
            Bounds = new PixelRect(s.BoundsX, s.BoundsY, s.BoundsWidth, s.BoundsHeight),
            WorkingArea = new PixelRect(s.WorkX, s.WorkY, s.WorkWidth, s.WorkHeight),
            Scaling = s.Scale,
            IsPrimary = s.IsPrimary,
            RefreshRate = s.RefreshHz,
        };
    }
}

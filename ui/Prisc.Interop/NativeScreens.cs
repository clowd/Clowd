using System;

namespace Prisc.Interop
{
    /// <summary>One screen as <c>prisc_screens_*</c> describes it, in the platform's capture space:
    /// physical virtual-desktop pixels on Windows, CoreGraphics global points (top-left origin) on
    /// macOS. Carries no Avalonia type, so this assembly stays free of a UI dependency; callers map
    /// it to the rectangle type they already use.</summary>
    /// <param name="WorkAreaKnown">True on Windows (<c>rcWork</c>). False on macOS, where
    /// CoreGraphics has no work area and the work rectangle equals the bounds.</param>
    /// <param name="Scale">Effective DPI / 96 on Windows; backing pixels per point on macOS.</param>
    /// <param name="RefreshHz">The current refresh rate, 0 when unknown.</param>
    /// <param name="DisplayId">HMONITOR on Windows, CGDirectDisplayID on macOS.</param>
    public readonly record struct ScreenInfo(
        int BoundsX,
        int BoundsY,
        int BoundsWidth,
        int BoundsHeight,
        int WorkX,
        int WorkY,
        int WorkWidth,
        int WorkHeight,
        bool WorkAreaKnown,
        double Scale,
        double RefreshHz,
        ulong DisplayId,
        bool IsPrimary);

    /// <summary>
    /// The screens, asked of the OS on every call and never cached: the list Prisc.Ui, the capture
    /// overlay and the share-region prompt all read (<c>prisc_rust_core::desktop::screens</c>).
    /// Any thread on every OS. On a platform without a screen list the list is empty and every
    /// lookup answers null.
    /// </summary>
    public static unsafe class NativeScreens
    {
        /// <summary>Larger than any real desktop, so the common case is one call and no allocation.</summary>
        private const int StackCapacity = 16;

        /// <summary>
        /// Every screen in the OS's enumeration order. Throws <see cref="InteropException"/> when
        /// the OS enumeration fails; an empty array is an honest "no screens".
        /// </summary>
        /// <remarks>Sized and filled in one call when the desktop has up to 16 screens. A display
        /// plugged in between a sizing call and the fill is caught by the count, which is always
        /// the full one, and the fill is retried with room to spare.</remarks>
        public static ScreenInfo[] List()
        {
            InteropLibrary.Require();
            PriscScreen* stack = stackalloc PriscScreen[StackCapacity];
            uint count = 0;
            Native.Check(NativeMethods.prisc_screens_list(stack, StackCapacity, &count), "prisc_screens_list");
            if (count <= StackCapacity)
                return Copy(stack, (int)count);

            for (int attempt = 0; attempt < 3; attempt++)
            {
                var heap = new PriscScreen[count + 4];
                fixed (PriscScreen* buffer = heap)
                {
                    uint capacity = (uint)heap.Length;
                    Native.Check(NativeMethods.prisc_screens_list(buffer, capacity, &count), "prisc_screens_list");
                    if (count <= capacity)
                        return Copy(buffer, (int)count);
                }
            }

            throw new InteropException(InteropStatus.Os, 0, "prisc_screens_list", "the display topology kept changing while it was read");
        }

        /// <summary>The screen containing the point, or what <paramref name="fallback"/> names when
        /// none does; null when that is nothing.</summary>
        public static ScreenInfo? AtPoint(int x, int y, ScreenFallback fallback = ScreenFallback.None)
        {
            InteropLibrary.Require();
            PriscScreen screen;
            var status = Native.Check(NativeMethods.prisc_screens_at_point(x, y, (int)fallback, &screen), "prisc_screens_at_point");
            return status == InteropStatus.Ok ? Map(screen) : null;
        }

        /// <summary>The screen with the largest overlap with the rectangle, or what
        /// <paramref name="fallback"/> names when it overlaps none; null when that is nothing.</summary>
        public static ScreenInfo? AtRect(int x, int y, int width, int height, ScreenFallback fallback = ScreenFallback.None)
        {
            InteropLibrary.Require();
            var rect = new PriscRect { x = x, y = y, width = width, height = height };
            PriscScreen screen;
            var status = Native.Check(NativeMethods.prisc_screens_at_rect(&rect, (int)fallback, &screen), "prisc_screens_at_rect");
            return status == InteropStatus.Ok ? Map(screen) : null;
        }

        /// <summary>The refresh rate of the screen containing each point, all read from one screen
        /// list (the <see cref="AtPoint"/> rule, no fallback): 0 for a point on no screen or a
        /// screen that reports none. Throws <see cref="InteropException"/> when the OS enumeration
        /// fails.</summary>
        public static double[] RefreshRatesAt(ReadOnlySpan<(int X, int Y)> points)
        {
            InteropLibrary.Require();
            var result = new double[points.Length];
            if (points.Length == 0)
                return result;
            var native = new PriscPoint[points.Length];
            for (int i = 0; i < points.Length; i++)
                native[i] = new PriscPoint { x = points[i].X, y = points[i].Y };
            fixed (PriscPoint* p = native)
            fixed (double* hz = result)
                Native.Check(NativeMethods.prisc_screens_refresh_at(p, (uint)native.Length, hz), "prisc_screens_refresh_at");
            return result;
        }

        /// <summary>The primary screen; null on a desktop without one.</summary>
        public static ScreenInfo? Primary()
        {
            InteropLibrary.Require();
            PriscScreen screen;
            var status = Native.Check(NativeMethods.prisc_screens_primary(&screen), "prisc_screens_primary");
            return status == InteropStatus.Ok ? Map(screen) : null;
        }

        private static ScreenInfo[] Copy(PriscScreen* screens, int count)
        {
            var result = new ScreenInfo[count];
            for (int i = 0; i < count; i++)
                result[i] = Map(screens[i]);
            return result;
        }

        internal static ScreenInfo Map(in PriscScreen s) => new(
            s.bounds.x, s.bounds.y, s.bounds.width, s.bounds.height,
            s.work_area.x, s.work_area.y, s.work_area.width, s.work_area.height,
            s.work_area_known != 0,
            s.scale,
            s.refresh_hz,
            s.display_id,
            s.is_primary != 0);
    }
}

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Clowd.Util
{
    /// <summary>
    /// The live system arrow cursor, rasterised into an Avalonia bitmap — what the OS is drawing
    /// right now, theme and cursor scheme included, rather than a picture of what it usually draws.
    /// The inspector's <c>native</c> cursor-style tile shows it, so the tile is honest about the one
    /// style whose artwork lives outside this repo.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Windows only; every other platform gets null and the caller falls back to drawn artwork.
    /// (Not <c>Clowd.Drawing.CursorResources</c>'s job — that parses the <c>.cur</c> files this app
    /// ships. Here the whole point is the cursor the <i>user</i> has.)
    /// </para>
    /// <para>
    /// <c>LoadImageW</c> with an explicit size picks the best-fitting frame out of the cursor file
    /// rather than stretching one, which is why the size is asked for up front instead of scaling
    /// afterwards. <c>DrawIconEx</c> then composites onto a zeroed 32-bit top-down DIB: a modern
    /// alpha cursor lands there premultiplied and finished. A classic AND/XOR mask cursor — still
    /// what the high-contrast and classic schemes hand out — carries no alpha at all and would end
    /// up an invisible rectangle, so when the first pass produces no alpha the mask is drawn on its
    /// own (<c>DI_MASK</c>: white where the cursor is transparent) and read back as the alpha
    /// channel.
    /// </para>
    /// </remarks>
    public static class SystemCursorImage
    {
        /// <summary>The nominal cursor size in DIP, the size a Windows cursor is authored at.
        /// Multiply by the render scaling to pick the frame to load.</summary>
        public const int BaseSizePx = 32;

        /// <summary>Whether this platform can produce a system cursor bitmap at all.</summary>
        public static bool IsSupported => OperatingSystem.IsWindows();

        /// <summary>
        /// The arrow cursor at <paramref name="pixelSize"/> physical pixels square, or null when the
        /// platform cannot supply one (and when it simply failed — a picker tile is not worth
        /// throwing over). Cached per size, failures included, so a caller may ask on every render.
        /// </summary>
        public static Bitmap Arrow(int pixelSize)
        {
            if (!IsSupported || pixelSize <= 0)
                return null;

            // clamped to the range of frames a cursor file actually carries: below 16 the result is
            // unreadable, above 256 nothing is authored and Windows would stretch.
            pixelSize = Math.Clamp(pixelSize, 16, 256);

            lock (CacheSync)
            {
                if (Cache.TryGetValue(pixelSize, out var cached))
                    return cached;

                Bitmap bitmap = null;
                try
                {
                    bitmap = Win32.LoadArrow(pixelSize);
                }
                catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
                {
                    // a Windows without user32/gdi32 is not a thing, but a caller must never see
                    // this throw, so the failure is cached like any other
                }

                Cache[pixelSize] = bitmap;
                return bitmap;
            }
        }

        private static readonly object CacheSync = new object();
        private static readonly Dictionary<int, Bitmap> Cache = new Dictionary<int, Bitmap>();

        private static class Win32
        {
            public static Bitmap LoadArrow(int size)
            {
                // LR_SHARED is mandatory for a system cursor and means the handle is the system's:
                // it must not be destroyed, and asking twice costs nothing.
                var cursor = LoadImageW(IntPtr.Zero, IDC_ARROW, IMAGE_CURSOR, size, size, LR_SHARED);
                if (cursor == IntPtr.Zero)
                    cursor = LoadCursorW(IntPtr.Zero, IDC_ARROW); // no sized frame: take the default
                if (cursor == IntPtr.Zero)
                    return null;

                var screenDc = GetDC(IntPtr.Zero);
                if (screenDc == IntPtr.Zero)
                    return null;

                IntPtr memDc = IntPtr.Zero, dib = IntPtr.Zero, previous = IntPtr.Zero;
                try
                {
                    memDc = CreateCompatibleDC(screenDc);
                    if (memDc == IntPtr.Zero)
                        return null;

                    var header = new BITMAPINFOHEADER
                    {
                        biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                        biWidth = size,
                        biHeight = -size, // negative = top-down, the order Avalonia wants
                        biPlanes = 1,
                        biBitCount = 32,
                        biCompression = BI_RGB,
                    };

                    dib = CreateDIBSection(memDc, ref header, DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
                    if (dib == IntPtr.Zero || bits == IntPtr.Zero)
                        return null;
                    previous = SelectObject(memDc, dib);

                    int stride = size * 4;
                    var pixels = new byte[stride * size];

                    if (!DrawIconEx(memDc, 0, 0, cursor, size, size, 0, IntPtr.Zero, DI_NORMAL))
                        return null;
                    GdiFlush();
                    Marshal.Copy(bits, pixels, 0, pixels.Length);

                    if (!HasAlpha(pixels) && !TryReadMaskAlpha(memDc, cursor, size, bits, pixels))
                        return null;

                    return ToTrimmedBitmap(pixels, size, stride);
                }
                finally
                {
                    if (previous != IntPtr.Zero)
                        SelectObject(memDc, previous);
                    if (dib != IntPtr.Zero)
                        DeleteObject(dib);
                    if (memDc != IntPtr.Zero)
                        DeleteDC(memDc);
                    ReleaseDC(IntPtr.Zero, screenDc);
                }
            }

            /// <summary>Whether the colour pass produced any coverage — false for a classic
            /// AND/XOR cursor, whose pixels carry no alpha byte.</summary>
            private static bool HasAlpha(byte[] pixels)
            {
                for (int i = 3; i < pixels.Length; i += 4)
                {
                    if (pixels[i] != 0)
                        return true;
                }
                return false;
            }

            /// <summary>
            /// Rebuilds the alpha channel of a mask cursor from its AND mask, which
            /// <c>DI_MASK</c> paints white where the cursor is transparent. The colour bytes in
            /// <paramref name="pixels"/> are the ones the earlier <c>DI_NORMAL</c> pass left; the
            /// mask decides which of them survive. False when nothing at all is opaque, which means
            /// the draw produced no cursor and the caller should keep its own artwork.
            /// </summary>
            private static bool TryReadMaskAlpha(IntPtr dc, IntPtr cursor, int size, IntPtr bits, byte[] pixels)
            {
                var mask = new byte[pixels.Length];
                Array.Clear(mask);
                Marshal.Copy(mask, 0, bits, mask.Length);

                if (!DrawIconEx(dc, 0, 0, cursor, size, size, 0, IntPtr.Zero, DI_MASK))
                    return false;
                GdiFlush();
                Marshal.Copy(bits, mask, 0, mask.Length);

                bool any = false;
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    bool opaque = mask[i] < 128; // black in the mask = the cursor's own ink
                    if (opaque)
                    {
                        pixels[i + 3] = 0xFF;
                        any = true;
                    }
                    else
                    {
                        // an inverting pixel (mask set, colour set) has nowhere to go in a static
                        // image: drop it entirely rather than leave a coloured halo
                        pixels[i] = 0;
                        pixels[i + 1] = 0;
                        pixels[i + 2] = 0;
                        pixels[i + 3] = 0;
                    }
                }
                return any;
            }

            /// <summary>
            /// Builds the bitmap with the fully transparent border trimmed off. A cursor frame is a
            /// square canvas whose arrow occupies one corner, so a tile that fits the whole canvas
            /// shows a small arrow floating in blank space — trimming to the ink lets the caller's
            /// fit-to-bounds actually fill the tile. Null when nothing is opaque (the caller keeps
            /// its own artwork), though the alpha passes should have caught that already.
            /// </summary>
            private static Bitmap ToTrimmedBitmap(byte[] pixels, int size, int stride)
            {
                int minX = size, minY = size, maxX = -1, maxY = -1;
                for (int y = 0; y < size; y++)
                {
                    int row = y * stride;
                    for (int x = 0; x < size; x++)
                    {
                        if (pixels[row + x * 4 + 3] == 0)
                            continue;
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }

                if (maxX < 0)
                    return null;

                int width = maxX - minX + 1;
                int height = maxY - minY + 1;
                int cropStride = width * 4;
                var cropped = new byte[cropStride * height];
                for (int y = 0; y < height; y++)
                {
                    Buffer.BlockCopy(pixels, (minY + y) * stride + minX * 4,
                        cropped, y * cropStride, cropStride);
                }

                var handle = GCHandle.Alloc(cropped, GCHandleType.Pinned);
                try
                {
                    // the ctor copies, so the pin only has to outlive the call
                    return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul,
                        handle.AddrOfPinnedObject(), new PixelSize(width, height),
                        new Vector(96, 96), cropStride);
                }
                finally
                {
                    handle.Free();
                }
            }

            private static readonly IntPtr IDC_ARROW = new IntPtr(32512);
            private const uint IMAGE_CURSOR = 2;
            private const uint LR_SHARED = 0x8000;
            private const uint DI_MASK = 0x0001;
            private const uint DI_NORMAL = 0x0003;
            private const uint BI_RGB = 0;
            private const uint DIB_RGB_COLORS = 0;

            [StructLayout(LayoutKind.Sequential)]
            private struct BITMAPINFOHEADER
            {
                public uint biSize;
                public int biWidth;
                public int biHeight;
                public ushort biPlanes;
                public ushort biBitCount;
                public uint biCompression;
                public uint biSizeImage;
                public int biXPelsPerMeter;
                public int biYPelsPerMeter;
                public uint biClrUsed;
                public uint biClrImportant;
            }

            [DllImport("user32.dll", SetLastError = true)]
            private static extern IntPtr LoadImageW(IntPtr hInst, IntPtr name, uint type, int cx, int cy, uint fuLoad);

            [DllImport("user32.dll", SetLastError = true)]
            private static extern IntPtr LoadCursorW(IntPtr hInstance, IntPtr lpCursorName);

            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool DrawIconEx(IntPtr hdc, int xLeft, int yTop, IntPtr hIcon,
                int cxWidth, int cyWidth, uint istepIfAniCur, IntPtr hbrFlickerFreeDraw, uint diFlags);

            [DllImport("user32.dll")]
            private static extern IntPtr GetDC(IntPtr hWnd);

            [DllImport("user32.dll")]
            private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

            [DllImport("gdi32.dll")]
            private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

            [DllImport("gdi32.dll")]
            private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER pbmi,
                uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

            [DllImport("gdi32.dll")]
            private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

            [DllImport("gdi32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool DeleteObject(IntPtr ho);

            [DllImport("gdi32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool DeleteDC(IntPtr hdc);

            [DllImport("gdi32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool GdiFlush();
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Media.Imaging;

namespace Clowd.Drawing
{
    // Avalonia port of the WPF CursorResources (MIGRATION.md §3 #18). The Win32 message window
    // (Vanara WM_DPICHANGED listener) is replaced by TopLevel.ScalingChanged subscriptions, and the
    // WPF Cursor(path, scaleWithDpi) loader is replaced by a runtime .cur parser:
    //   ICONDIR header (u16 reserved, u16 type=2, u16 count), then 16-byte ICONDIRENTRY records —
    //   width byte at offset 0 (0 means 256), hotspot words at entry offsets 4/6, data size at 8,
    //   data offset at 12. Each frame's image data is a PNG; the selected frame is decoded into an
    //   Avalonia Bitmap and paired with that frame's hotspot.
    // Frame selection (see GetDesiredFrameSize):
    //   - Windows: the frame whose size best matches 32 * RenderScaling — Win32 HCURSORs are sized
    //     in physical pixels, so the high-res frame displays at 32 logical px and stays sharp.
    //   - macOS: always the 32px frame. Avalonia's macOS backend re-encodes the bitmap to PNG via
    //     Skia (which writes no pHYs/DPI metadata) and builds the NSCursor with
    //     [NSImage initWithData:], so the image is treated as 72 DPI and an N px frame displays at
    //     N *logical* px regardless of the bitmap's DPI. Picking 32 * RenderScaling there made the
    //     cursors render double size on Retina; per the MIGRATION.md §6 pre-decided fallback we use
    //     the 32px frame and let the OS scale it (slight blur accepted).
    // Cursors are cached keyed on (file, scaling bucket) and the cache is flushed when any attached
    // TopLevel raises ScalingChanged.
    internal partial class CursorResources : EmbeddedResource
    {
        private const string RSX_NS = "Clowd.Drawing.Cursors";
        private const int BASE_CURSOR_SIZE = 32;

        private static readonly object _lock = new object();
        private static readonly Dictionary<(string FileName, int ScalingBucket), Cursor> _cache = new();
        private static readonly HashSet<TopLevel> _attached = new();
        private static double _lastScaling = 1.0;

        private CursorResources() : base(typeof(CursorResources).Assembly, RSX_NS) { }

        /// <summary>
        /// Subscribes to the TopLevel's ScalingChanged event so the cursor cache is flushed when the
        /// DPI changes (replaces the WPF WM_DPICHANGED listener window). Idempotent; safe to call with null.
        /// </summary>
        public static void Attach(TopLevel topLevel)
        {
            if (topLevel == null)
                return;

            lock (_lock)
            {
                if (!_attached.Add(topLevel))
                    return;
                _lastScaling = topLevel.RenderScaling;
            }

            topLevel.ScalingChanged += TopLevelOnScalingChanged;
            if (topLevel is Window window)
                window.Closed += (_, _) => Detach(window);
        }

        public static void Detach(TopLevel topLevel)
        {
            if (topLevel == null)
                return;

            lock (_lock)
            {
                if (!_attached.Remove(topLevel))
                    return;
            }

            topLevel.ScalingChanged -= TopLevelOnScalingChanged;
        }

        private static void TopLevelOnScalingChanged(object sender, EventArgs e)
        {
            lock (_lock)
            {
                if (sender is TopLevel topLevel)
                    _lastScaling = topLevel.RenderScaling;
                _cache.Clear();
            }
        }

        public static Cursor GetCursor(string fileName)
        {
            var scaling = GetCurrentScaling();
            var key = (fileName, ScalingBucket: (int)Math.Round(scaling * 100));

            lock (_lock)
            {
                if (_cache.TryGetValue(key, out var cached))
                    return cached;
            }

            var loaded = LoadCursor(fileName, scaling);

            lock (_lock)
            {
                // another caller may have raced us; prefer the existing instance
                if (_cache.TryGetValue(key, out var cached))
                    return cached;
                _cache[key] = loaded;
            }

            return loaded;
        }

        private static double GetCurrentScaling()
        {
            // prefer the active window's scaling; auto-attach any windows we have not yet seen so the
            // cache is flushed on their ScalingChanged even if nobody called Attach explicitly.
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                Window best = null;
                foreach (var w in desktop.Windows)
                {
                    Attach(w);
                    if (best == null || w.IsActive)
                        best = w;
                }

                if (best != null)
                {
                    lock (_lock)
                    {
                        _lastScaling = best.RenderScaling;
                        return _lastScaling;
                    }
                }
            }

            lock (_lock)
            {
                return _lastScaling;
            }
        }

        /// <summary>
        /// The frame size (in pixels) that <see cref="GetCursor"/> selects for the given render scaling.
        /// macOS always uses the 32px frame: Avalonia's macOS cursor backend ignores bitmap DPI (the
        /// bitmap is re-encoded to PNG without DPI metadata and NSImage assumes 72 DPI), so an N px
        /// frame displays at N logical px — a 64px frame would render double size on Retina.
        /// Windows HCURSORs are sized in physical pixels, so the best frame for 32 logical px is
        /// 32 * scaling.
        /// </summary>
        internal static int GetDesiredFrameSize(double scaling)
        {
            return GetDesiredFrameSize(scaling, OperatingSystem.IsMacOS());
        }

        internal static int GetDesiredFrameSize(double scaling, bool isMacOS)
        {
            return isMacOS ? BASE_CURSOR_SIZE : (int)Math.Round(BASE_CURSOR_SIZE * scaling);
        }

        /// <summary>
        /// Diagnostics/test hook: parses the embedded .cur and reports the frame GetCursor would pick
        /// for the given scaling — its pixel size, the logical (DIP) size the OS will display it at,
        /// and its hotspot (in frame pixels).
        /// </summary>
        internal static (int FramePixelSize, double LogicalSize, int HotspotX, int HotspotY) MeasureFrame(string fileName, double scaling)
        {
            var data = ReadCursorResource(fileName);
            var chosen = SelectFrameEntry(data, fileName, GetDesiredFrameSize(scaling));

            int size = data[chosen];
            if (size == 0)
                size = 256;

            // macOS displays the frame at its pixel size (72 DPI assumption, see GetDesiredFrameSize);
            // Windows displays it at pixel size / scaling.
            var logical = OperatingSystem.IsMacOS() ? size : size / scaling;
            return (size, logical, ReadUInt16(data, chosen + 4), ReadUInt16(data, chosen + 6));
        }

        private static byte[] ReadCursorResource(string fileName)
        {
            byte[] data;
            using (var stream = GetStream(RSX_NS, fileName, typeof(CursorResources).Assembly))
            using (var ms = new MemoryStream())
            {
                stream.CopyTo(ms);
                data = ms.ToArray();
            }

            if (data.Length < 6)
                throw new InvalidDataException($"Cursor resource \"{fileName}\" is truncated.");

            var type = ReadUInt16(data, 2);
            var count = ReadUInt16(data, 4);
            if (type != 2 || count == 0)
                throw new InvalidDataException($"Cursor resource \"{fileName}\" is not a valid .cur file.");

            return data;
        }

        /// <summary>Returns the byte offset of the chosen ICONDIRENTRY: the smallest frame >= desired
        /// size, or the largest available if none is large enough.</summary>
        private static int SelectFrameEntry(byte[] data, string fileName, int desired)
        {
            var count = ReadUInt16(data, 4);

            int bestIdx = -1, bestSize = int.MaxValue;
            int largestIdx = -1, largestSize = 0;
            for (int i = 0; i < count; i++)
            {
                var entry = 6 + (i * 16);
                if (entry + 16 > data.Length)
                    throw new InvalidDataException($"Cursor resource \"{fileName}\" is truncated.");

                int size = data[entry]; // width byte; 0 means 256
                if (size == 0)
                    size = 256;

                if (size >= desired && size < bestSize)
                {
                    bestSize = size;
                    bestIdx = i;
                }

                if (size > largestSize)
                {
                    largestSize = size;
                    largestIdx = i;
                }
            }

            return 6 + ((bestIdx >= 0 ? bestIdx : largestIdx) * 16);
        }

        private static Cursor LoadCursor(string fileName, double scaling)
        {
            var data = ReadCursorResource(fileName);
            var chosen = SelectFrameEntry(data, fileName, GetDesiredFrameSize(scaling));
            var hotspotX = ReadUInt16(data, chosen + 4);
            var hotspotY = ReadUInt16(data, chosen + 6);
            var byteCount = (int)ReadUInt32(data, chosen + 8);
            var byteOffset = (int)ReadUInt32(data, chosen + 12);

            if (byteCount <= 0 || byteOffset <= 0 || (long)byteOffset + byteCount > data.Length)
                throw new InvalidDataException($"Cursor resource \"{fileName}\" has an invalid frame entry.");

            Bitmap bitmap;
            using (var png = new MemoryStream(data, byteOffset, byteCount, false))
                bitmap = new Bitmap(png);

            return new Cursor(bitmap, new PixelPoint(hotspotX, hotspotY));
        }

        private static ushort ReadUInt16(byte[] data, int offset)
        {
            return (ushort)(data[offset] | (data[offset + 1] << 8));
        }

        private static uint ReadUInt32(byte[] data, int offset)
        {
            return (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));
        }
    }
}

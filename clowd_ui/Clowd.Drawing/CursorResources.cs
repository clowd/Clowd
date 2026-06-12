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
    //   data offset at 12. Each frame's image data is a PNG; the frame whose size best matches
    //   32 * RenderScaling is decoded into an Avalonia Bitmap and paired with that frame's hotspot.
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

        private static Cursor LoadCursor(string fileName, double scaling)
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

            var desired = (int)Math.Round(BASE_CURSOR_SIZE * scaling);

            // pick the smallest frame >= desired size; if none is large enough, the largest available.
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

            var chosen = 6 + ((bestIdx >= 0 ? bestIdx : largestIdx) * 16);
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

using System;
using System.Collections.Generic;
using SkiaSharp;

namespace Clowd.VideoSDK.Composition
{
    /// <summary>
    /// Per-composer cache of the latest decoded frame per (sourceId, streamIndex), as
    /// <see cref="SKImage"/>s ready for <c>FrameComposer</c> to draw. On the GPU backend
    /// <see cref="Upload"/> copies the CPU BGRA buffer into a context-bound texture; on CPU it
    /// snapshots into a raster image. Either way the pooled buffer is recycled immediately, and
    /// the previous entry for the same stream is evicted — the compositor only ever needs the
    /// latest frame of each stream.
    ///
    /// Context affinity: textures are bound to the factory's <c>GRContext</c>, so this cache is
    /// per-composer, never global, and ALL calls (including <see cref="Dispose"/>) must happen on
    /// the owning <see cref="ComposerThread"/>. Cached images are owned by the cache: they are
    /// disposed on eviction, so callers draw them immediately rather than holding references.
    /// </summary>
    public sealed class FrameTextureCache : IDisposable
    {
        private readonly ISurfaceFactory _factory;
        private readonly Dictionary<(Guid SourceId, int StreamIndex), Entry> _entries
            = new Dictionary<(Guid, int), Entry>();
        // person mattes ride beside their stream's frames under the same key: same upload path,
        // same ownership, their own slot — a matte frame must never evict the picture it masks.
        private readonly Dictionary<(Guid SourceId, int StreamIndex), Entry> _masks
            = new Dictionary<(Guid, int), Entry>();
        private bool _disposed;

        private readonly struct Entry
        {
            public Entry(SKImage image, long ptsTicks)
            {
                Image = image;
                PtsTicks = ptsTicks;
            }

            public SKImage Image { get; }
            public long PtsTicks { get; }
        }

        public FrameTextureCache(ISurfaceFactory factory)
        {
            ArgumentNullException.ThrowIfNull(factory);
            _factory = factory;
        }

        /// <summary>Number of streams currently cached (frame entries; mattes not counted).</summary>
        public int Count => _entries.Count;

        /// <summary>
        /// Uploads a decoded BGRA frame and caches it for its stream, evicting (and disposing)
        /// any previous frame of the same stream. Takes ownership of <paramref name="buffer"/>:
        /// it is returned to its pool before this method returns, success or failure.
        /// </summary>
        /// <returns>The cached image — owned by the cache, valid until the next Upload for the
        /// same stream (or <see cref="Dispose"/>).</returns>
        public SKImage Upload(Guid sourceId, int streamIndex, long ptsTicks,
            FrameBuffer buffer, int width, int height, int rowBytes)
            => Upload(_entries, sourceId, streamIndex, ptsTicks, buffer, width, height, rowBytes);

        /// <summary>Same contract as <see cref="Upload"/> for the stream's person-matte frame
        /// (gray in luma, decoded to BGRA like every frame): its own slot, its own eviction.</summary>
        public SKImage UploadMask(Guid sourceId, int streamIndex, long ptsTicks,
            FrameBuffer buffer, int width, int height, int rowBytes)
            => Upload(_masks, sourceId, streamIndex, ptsTicks, buffer, width, height, rowBytes);

        private SKImage Upload(Dictionary<(Guid, int), Entry> entries, Guid sourceId, int streamIndex,
            long ptsTicks, FrameBuffer buffer, int width, int height, int rowBytes)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);

                var info = SurfacePixels.Bgra(width, height);
                SKImage image;
                if (_factory.Context != null)
                {
                    // Zero-copy wrap of the CPU buffer, then an immediate copy into a
                    // context-bound GPU texture; the wrapper never outlives the buffer.
                    using var raster = SKImage.FromPixels(info, buffer.Address, rowBytes);
                    if (raster == null)
                        throw new InvalidOperationException($"Invalid frame data ({width}x{height}, rowBytes {rowBytes}).");
                    image = raster.ToTextureImage(_factory.Context)
                        // Texture allocation can fail under VRAM pressure — degrade to raster
                        // rather than dropping the frame.
                        ?? SKImage.FromPixelCopy(info, buffer.Address, rowBytes);
                }
                else
                {
                    image = SKImage.FromPixelCopy(info, buffer.Address, rowBytes);
                }

                if (image == null)
                    throw new InvalidOperationException($"Failed to upload frame ({width}x{height}, rowBytes {rowBytes}).");

                var key = (sourceId, streamIndex);
                if (entries.TryGetValue(key, out var previous))
                    previous.Image.Dispose();
                entries[key] = new Entry(image, ptsTicks);
                return image;
            }
            finally
            {
                buffer.Return();
            }
        }

        /// <summary>Gets the latest cached frame for a stream, if any.</summary>
        public bool TryGet(Guid sourceId, int streamIndex, out SKImage image, out long ptsTicks)
            => TryGet(_entries, sourceId, streamIndex, out image, out ptsTicks);

        /// <summary>Gets the latest cached matte frame for a stream, if any.</summary>
        public bool TryGetMask(Guid sourceId, int streamIndex, out SKImage image, out long ptsTicks)
            => TryGet(_masks, sourceId, streamIndex, out image, out ptsTicks);

        private static bool TryGet(Dictionary<(Guid, int), Entry> entries, Guid sourceId,
            int streamIndex, out SKImage image, out long ptsTicks)
        {
            if (entries.TryGetValue((sourceId, streamIndex), out var entry))
            {
                image = entry.Image;
                ptsTicks = entry.PtsTicks;
                return true;
            }

            image = null;
            ptsTicks = 0;
            return false;
        }

        /// <summary>Removes and disposes the cached frame (and any matte) for a stream (e.g.
        /// when its item leaves the timeline).</summary>
        public void Evict(Guid sourceId, int streamIndex)
        {
            var key = (sourceId, streamIndex);
            if (_entries.TryGetValue(key, out var entry))
            {
                entry.Image.Dispose();
                _entries.Remove(key);
            }

            if (_masks.TryGetValue(key, out var mask))
            {
                mask.Image.Dispose();
                _masks.Remove(key);
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var entry in _entries.Values)
                entry.Image.Dispose();
            _entries.Clear();
            foreach (var entry in _masks.Values)
                entry.Image.Dispose();
            _masks.Clear();
        }
    }
}

using System;
using System.Collections.Generic;
using Clowd.VideoSDK.Model;

namespace Clowd.VideoSDK.Composition
{
    /// <summary>
    /// The render path's <see cref="IFrameSource"/>: monotonic, no seeking. Each requested
    /// (sourceId, streamIndex) lazily opens its own <see cref="SyncStreamDecoder"/> and a
    /// <see cref="SequentialFrameCursor{T}"/> holding current + next; requests decode forward
    /// while <c>next.pts &lt;= t</c>, so lookups follow the latest-PTS-at-or-before-t contract
    /// (see <see cref="IFrameSource"/>) for CFR and VFR alike. Requested times must be
    /// non-decreasing per stream — a regression throws.
    ///
    /// Frames decode to pooled CPU BGRA buffers; when the covering frame changes it is uploaded
    /// through the <see cref="FrameTextureCache"/> (which recycles the buffer and evicts the
    /// stream's previous image), so the returned <see cref="FrameRef.Image"/> is context-bound
    /// on GPU backends. Everything here must therefore run on the composer's thread — the same
    /// single thread that owns the cache's factory. The cache and pool are borrowed, not owned;
    /// disposing the source frees its decoders and any undelivered buffers but leaves cached
    /// images to the cache.
    /// </summary>
    public sealed class SequentialFrameSource : IFrameSource, IDisposable
    {
        private readonly Project _project;
        private readonly FrameTextureCache _cache;
        private readonly FrameBufferPool _pool;
        private readonly bool _ownsPool;
        private readonly Dictionary<(Guid SourceId, int StreamIndex), StreamState> _streams
            = new Dictionary<(Guid, int), StreamState>();
        private bool _disposed;

        private sealed class DecodedFrame
        {
            public FrameBuffer Buffer;
            public int Width;
            public int Height;
            public int RowBytes;
        }

        private sealed class StreamState : IDisposable
        {
            public SyncStreamDecoder Decoder;
            public SequentialFrameCursor<DecodedFrame> Cursor;

            public void Dispose()
            {
                Cursor?.Dispose();
                Decoder?.Dispose();
            }
        }

        /// <param name="project">Resolves <c>SourceId</c> to file paths. Not mutated.</param>
        /// <param name="cache">The composer's texture cache; delivered frames live in it.</param>
        /// <param name="pool">Staging buffer pool; a private pool is created (and owned) when null.</param>
        public SequentialFrameSource(Project project, FrameTextureCache cache, FrameBufferPool pool = null)
        {
            ArgumentNullException.ThrowIfNull(project);
            ArgumentNullException.ThrowIfNull(cache);
            _project = project;
            _cache = cache;
            _ownsPool = pool == null;
            _pool = pool ?? new FrameBufferPool();
        }

        public bool TryGetFrame(Guid sourceId, int streamIndex, long sourceTimeTicks, out FrameRef frame)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            frame = default;

            var key = (sourceId, streamIndex);
            if (!_streams.TryGetValue(key, out var state))
            {
                state = OpenStream(sourceId, streamIndex);
                _streams[key] = state;
            }

            if (state.Cursor == null)
                return false; // stream previously failed to open — stays dark

            if (!state.Cursor.TryAdvance(sourceTimeTicks, out long ptsTicks, out var decoded))
                return false; // no decodable frames in the stream

            SkiaSharp.SKImage image;
            if (decoded != null)
            {
                // the covering frame changed: upload it (the cache takes buffer ownership and
                // evicts this stream's previous image).
                image = _cache.Upload(sourceId, streamIndex, ptsTicks,
                    decoded.Buffer, decoded.Width, decoded.Height, decoded.RowBytes);
            }
            else if (!_cache.TryGet(sourceId, streamIndex, out image, out _))
            {
                return false; // delivered frame was evicted externally
            }

            frame = new FrameRef(image, ptsTicks);
            return true;
        }

        private StreamState OpenStream(Guid sourceId, int streamIndex)
        {
            Source source = null;
            if (_project.Sources != null)
            {
                foreach (var s in _project.Sources)
                {
                    if (s.Id == sourceId)
                    {
                        source = s;
                        break;
                    }
                }
            }

            if (source == null)
                throw new ArgumentException($"Project has no source {sourceId}.", nameof(sourceId));

            var state = new StreamState();
            state.Decoder = new SyncStreamDecoder(source.Path, streamIndex, _pool);
            var decoder = state.Decoder;
            state.Cursor = new SequentialFrameCursor<DecodedFrame>(
                (out long pts, out DecodedFrame f) =>
                {
                    if (!decoder.DecodeNext(out pts, out var buffer, out int w, out int h, out int rowBytes))
                    {
                        f = null;
                        return false;
                    }

                    f = new DecodedFrame { Buffer = buffer, Width = w, Height = h, RowBytes = rowBytes };
                    return true;
                },
                f => f.Buffer.Return());
            return state;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            foreach (var state in _streams.Values)
                state.Dispose();
            _streams.Clear();

            if (_ownsPool)
                _pool.Dispose();
        }
    }
}

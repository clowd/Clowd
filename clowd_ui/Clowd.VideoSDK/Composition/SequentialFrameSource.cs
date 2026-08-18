using System;
using System.Collections.Generic;
using Clowd.VideoSDK.Ai;
using Clowd.VideoSDK.Model;

namespace Clowd.VideoSDK.Composition
{
    /// <summary>
    /// The render path's <see cref="IFrameSource"/>: forward decode, no playback machinery. Each
    /// requested (sourceId, streamIndex) lazily opens its own <see cref="SyncStreamDecoder"/> and a
    /// <see cref="SequentialFrameCursor{T}"/> holding current + next; requests decode forward
    /// while <c>next.pts &lt;= t</c>, so lookups follow the latest-PTS-at-or-before-t contract
    /// (see <see cref="IFrameSource"/>) for CFR and VFR alike.
    ///
    /// <para>
    /// Requests per stream are expected to run forward — that is what a timeline whose items read
    /// their source in order produces, and it decodes each stream exactly once. A request that goes
    /// backwards is legal but costly: the timeline reads that stream out of source order (a clip
    /// moved behind an earlier one, split halves swapped, the same span used twice), so the decoder
    /// container-seeks to the keyframe at or before the wanted time and the cursor restarts there
    /// (<see cref="RepositionCount"/> counts these). Frames still come out identical to a forward
    /// decode; only the decode work is repeated.
    /// </para>
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
        private readonly string _sidecarCacheDir;
        private readonly HashSet<(Guid SourceId, int StreamIndex)> _matteStreams;
        private readonly Dictionary<(Guid SourceId, int StreamIndex), StreamState> _streams
            = new Dictionary<(Guid, int), StreamState>();
        private int _repositions;
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
            public SyncStreamDecoder MaskDecoder;
            public SequentialFrameCursor<DecodedFrame> MaskCursor;
            public long LastRequestTicks = long.MinValue;

            public void Dispose()
            {
                Cursor?.Dispose();
                Decoder?.Dispose();
                MaskCursor?.Dispose();
                MaskDecoder?.Dispose();
            }
        }

        /// <param name="project">Resolves <c>SourceId</c> to file paths. Not mutated.</param>
        /// <param name="cache">The composer's texture cache; delivered frames live in it.</param>
        /// <param name="pool">Staging buffer pool; a private pool is created (and owned) when null.</param>
        /// <param name="sidecarCacheDir">Directory holding the project's AI sidecars (see
        /// <see cref="AiSidecars"/>): streams the project wants a person matte for decode their
        /// valid matte sidecar in step and deliver it as <see cref="FrameRef.Mask"/>. Null (or a
        /// missing/stale sidecar) delivers maskless frames — the effect degrades in the
        /// composer.</param>
        public SequentialFrameSource(Project project, FrameTextureCache cache, FrameBufferPool pool = null,
            string sidecarCacheDir = null)
        {
            ArgumentNullException.ThrowIfNull(project);
            ArgumentNullException.ThrowIfNull(cache);
            _project = project;
            _cache = cache;
            _ownsPool = pool == null;
            _pool = pool ?? new FrameBufferPool();
            _sidecarCacheDir = sidecarCacheDir;
            _matteStreams = MatteGenerator.CollectMatteStreams(project);
        }

        /// <summary>Container seeks performed so far because a stream was read out of source order
        /// (test/diagnostic; a project whose items run forward never repositions).</summary>
        internal int RepositionCount => _repositions;

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

            if (sourceTimeTicks < state.LastRequestTicks)
            {
                // the timeline reads this stream out of source order: replay it from the keyframe
                // at or before the wanted time rather than failing the render. The matte replays
                // in step — its timeline is the stream's timeline.
                state.Decoder.Seek(sourceTimeTicks);
                state.Cursor.Rewind();
                state.MaskDecoder?.Seek(sourceTimeTicks);
                state.MaskCursor?.Rewind();
                _repositions++;
            }

            state.LastRequestTicks = sourceTimeTicks;

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

            // the matte advances by the SAME requested instant, so the pairing is pure
            // latest-PTS-at-or-before per stream — resolution- and rate-independent.
            SkiaSharp.SKImage mask = null;
            if (state.MaskCursor != null
                && state.MaskCursor.TryAdvance(sourceTimeTicks, out long maskPts, out var decodedMask))
            {
                if (decodedMask != null)
                {
                    mask = _cache.UploadMask(sourceId, streamIndex, maskPts,
                        decodedMask.Buffer, decodedMask.Width, decodedMask.Height, decodedMask.RowBytes);
                }
                else
                {
                    _cache.TryGetMask(sourceId, streamIndex, out mask, out _);
                }
            }

            frame = new FrameRef(image, ptsTicks, mask);
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
            state.Cursor = CreateCursor(state.Decoder);

            // the stream's person matte, when the project wants one and a valid sidecar exists —
            // opened beside the stream so both cursors advance in step. Anything wrong with the
            // sidecar simply means maskless frames; the render never fails on an optimization.
            var mattePath = AiSidecars.MattePath(_sidecarCacheDir, sourceId, streamIndex);
            if (mattePath != null && _matteStreams.Contains((sourceId, streamIndex))
                && AiSidecars.IsValid(mattePath, source.Path))
            {
                try
                {
                    state.MaskDecoder = new SyncStreamDecoder(mattePath, 0, _pool);
                    state.MaskCursor = CreateCursor(state.MaskDecoder);
                }
                catch
                {
                    state.MaskDecoder?.Dispose();
                    state.MaskDecoder = null;
                    state.MaskCursor = null;
                }
            }

            return state;
        }

        private static SequentialFrameCursor<DecodedFrame> CreateCursor(SyncStreamDecoder decoder)
            => new SequentialFrameCursor<DecodedFrame>(
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

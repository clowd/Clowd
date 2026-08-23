using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Clowd.VideoSDK.Composition;
using SkiaSharp;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    public class FrameTextureCacheTests
    {
        private static readonly Guid SourceA = Guid.NewGuid();
        private static readonly Guid SourceB = Guid.NewGuid();

        /// <summary>Fills a rented buffer with a solid BGRA color via native copy.</summary>
        private static FrameBuffer RentSolid(FrameBufferPool pool, int w, int h, byte b, byte g, byte r, byte a)
        {
            int size = w * h * 4;
            var buffer = pool.Rent(size);
            var pattern = new byte[size];
            for (int i = 0; i < size; i += 4)
            {
                pattern[i] = b;
                pattern[i + 1] = g;
                pattern[i + 2] = r;
                pattern[i + 3] = a;
            }

            Marshal.Copy(pattern, 0, buffer.Address, size);
            return buffer;
        }

        private static (byte B, byte G, byte R, byte A) ReadImagePixel(SKImage image, int x, int y)
        {
            var info = new SKImageInfo(1, 1, SKColorType.Bgra8888, SKAlphaType.Premul);
            var native = Marshal.AllocHGlobal(4);
            try
            {
                Assert.True(image.ReadPixels(info, native, 4, x, y));
                var px = new byte[4];
                Marshal.Copy(native, px, 0, 4);
                return (px[0], px[1], px[2], px[3]);
            }
            finally
            {
                Marshal.FreeHGlobal(native);
            }
        }

        [Fact]
        public void Upload_produces_image_with_frame_pixels_and_recycles_buffer()
        {
            using var factory = new CpuSurfaceFactory();
            using var pool = new FrameBufferPool();
            using var cache = new FrameTextureCache(factory);

            var buffer = RentSolid(pool, 8, 8, 10, 20, 30, 255);
            Assert.Equal(0, pool.PooledCount);

            var image = cache.Upload(SourceA, 0, ptsTicks: 1000, buffer, 8, 8, 8 * 4);

            Assert.NotNull(image);
            Assert.Equal(((byte)10, (byte)20, (byte)30, (byte)255), ReadImagePixel(image, 3, 3));
            Assert.Equal(1, pool.PooledCount); // buffer went straight back to the pool
        }

        [Fact]
        public void Upload_evicts_prior_entry_for_same_stream()
        {
            using var factory = new CpuSurfaceFactory();
            using var pool = new FrameBufferPool();
            using var cache = new FrameTextureCache(factory);

            var first = cache.Upload(SourceA, 0, 100, RentSolid(pool, 4, 4, 255, 0, 0, 255), 4, 4, 16);
            var second = cache.Upload(SourceA, 0, 200, RentSolid(pool, 4, 4, 0, 255, 0, 255), 4, 4, 16);

            Assert.Equal(1, cache.Count); // same key: replaced, not accumulated
            Assert.True(cache.TryGet(SourceA, 0, out var image, out var pts));
            Assert.Same(second, image);
            Assert.NotSame(first, image);
            Assert.Equal(200, pts);

            // Only one native buffer was ever allocated — the first upload recycled it and the
            // second upload rented the same one back.
            Assert.Equal(1, pool.TotalAllocated);
            Assert.Equal(1, pool.PooledCount);
        }

        [Fact]
        public void Streams_are_cached_independently()
        {
            using var factory = new CpuSurfaceFactory();
            using var pool = new FrameBufferPool();
            using var cache = new FrameTextureCache(factory);

            cache.Upload(SourceA, 0, 100, RentSolid(pool, 4, 4, 1, 1, 1, 255), 4, 4, 16);
            cache.Upload(SourceA, 1, 150, RentSolid(pool, 4, 4, 2, 2, 2, 255), 4, 4, 16);
            cache.Upload(SourceB, 0, 175, RentSolid(pool, 4, 4, 3, 3, 3, 255), 4, 4, 16);

            Assert.Equal(3, cache.Count);
            Assert.True(cache.TryGet(SourceA, 0, out _, out var ptsA0));
            Assert.True(cache.TryGet(SourceA, 1, out _, out var ptsA1));
            Assert.True(cache.TryGet(SourceB, 0, out _, out var ptsB0));
            Assert.Equal(100, ptsA0);
            Assert.Equal(150, ptsA1);
            Assert.Equal(175, ptsB0);

            Assert.False(cache.TryGet(Guid.NewGuid(), 0, out _, out _));

            cache.Evict(SourceA, 1);
            Assert.Equal(2, cache.Count);
            Assert.False(cache.TryGet(SourceA, 1, out _, out _));
        }

        [Fact]
        public void Upload_recycles_buffer_even_when_it_fails()
        {
            using var factory = new CpuSurfaceFactory();
            using var pool = new FrameBufferPool();
            var cache = new FrameTextureCache(factory);
            cache.Dispose();

            var buffer = pool.Rent(64);
            Assert.Throws<ObjectDisposedException>(() => cache.Upload(SourceA, 0, 0, buffer, 4, 4, 16));
            Assert.Equal(1, pool.PooledCount);
        }

        [Fact]
        public void Pool_reuses_by_capacity_and_frees_on_dispose()
        {
            var pool = new FrameBufferPool();
            var big = pool.Rent(1024);
            var small = pool.Rent(64);
            Assert.Equal(2, pool.TotalAllocated);
            big.Return();
            small.Return();
            Assert.Equal(2, pool.PooledCount);

            // A request that fits the big buffer reuses it rather than allocating.
            var reused = pool.Rent(512);
            Assert.Equal(big.Address, reused.Address);
            Assert.Equal(2, pool.TotalAllocated);

            // Double-return is a harmless no-op.
            small.Return();
            Assert.Equal(1, pool.PooledCount);

            pool.Dispose();
            reused.Return(); // late return after dispose: freed, not pooled
            Assert.Equal(0, pool.PooledCount);
            Assert.Throws<ObjectDisposedException>(() => pool.Rent(16));
        }

        [Fact]
        public void Sink_latest_frame_semantics()
        {
            using var sink = new PooledFrameSink();

            Assert.False(sink.TryAcquireLatest(out _));

            var t1 = sink.BeginFrame(4, 4);
            Assert.NotEqual(IntPtr.Zero, t1.Address);
            Assert.Equal(16, t1.RowBytes);
            sink.CompleteFrame(in t1, TimeSpan.FromMilliseconds(10));

            var t2 = sink.BeginFrame(4, 4);
            sink.CompleteFrame(in t2, TimeSpan.FromMilliseconds(20));

            // Unconsumed frame 1 was superseded and its buffer recycled — only the latest is
            // acquirable, exactly once.
            Assert.True(sink.TryAcquireLatest(out var frame));
            Assert.Equal(TimeSpan.FromMilliseconds(20), frame.Pts);
            Assert.Equal(4, frame.Width);
            Assert.Equal(4, frame.Height);
            Assert.False(sink.TryAcquireLatest(out _));

            frame.Return();
            Assert.Equal(sink.Pool.TotalAllocated, sink.Pool.PooledCount);
        }

        [Fact]
        public void Sink_acquired_frame_buffer_is_not_reused_while_held()
        {
            using var sink = new PooledFrameSink();

            var t1 = sink.BeginFrame(4, 4);
            sink.CompleteFrame(in t1, TimeSpan.Zero);
            Assert.True(sink.TryAcquireLatest(out var held));

            // While the consumer holds frame 1, new frames must get a different buffer.
            var t2 = sink.BeginFrame(4, 4);
            Assert.NotEqual(held.Buffer.Address, t2.Address);
            sink.CompleteFrame(in t2, TimeSpan.FromMilliseconds(1));

            held.Return();
        }

        [Fact]
        public void Sink_upload_consumes_acquired_frame()
        {
            using var factory = new CpuSurfaceFactory();
            using var sink = new PooledFrameSink();
            using var cache = new FrameTextureCache(factory);

            var target = sink.BeginFrame(4, 4);
            sink.CompleteFrame(in target, TimeSpan.FromTicks(12345));
            Assert.True(sink.TryAcquireLatest(out var frame));

            var image = cache.Upload(SourceA, 0, frame.Pts.Ticks, frame.Buffer,
                frame.Width, frame.Height, frame.RowBytes);
            Assert.NotNull(image);
            Assert.True(cache.TryGet(SourceA, 0, out _, out var pts));
            Assert.Equal(12345, pts);
            // Upload recycled the frame's buffer back into the sink's pool.
            Assert.Equal(sink.Pool.TotalAllocated, sink.Pool.PooledCount);
        }

        [Fact]
        public async Task Sink_concurrent_writers_and_consumer_do_not_lose_or_leak_buffers()
        {
            using var sink = new PooledFrameSink();
            const int writers = 4;
            const int framesPerWriter = 250;
            long completed = 0;

            var writerTasks = new Task[writers];
            for (int wi = 0; wi < writers; wi++)
            {
                writerTasks[wi] = Task.Run(() =>
                {
                    for (int i = 0; i < framesPerWriter; i++)
                    {
                        var t = sink.BeginFrame(8, 8);
                        if (t.Address == IntPtr.Zero)
                            return;
                        sink.CompleteFrame(in t, TimeSpan.FromTicks(Interlocked.Increment(ref completed)));
                    }
                });
            }

            int consumed = 0;
            var allWriters = Task.WhenAll(writerTasks);
            var consumer = Task.Run(() =>
            {
                while (!allWriters.IsCompleted)
                {
                    if (sink.TryAcquireLatest(out var frame))
                    {
                        consumed++;
                        frame.Return();
                    }
                    else
                    {
                        Thread.Yield();
                    }
                }
            });

            await Task.WhenAll(writerTasks);
            await consumer;

            Assert.Equal(writers * framesPerWriter, Interlocked.Read(ref completed));

            // Drain any final frame, then every buffer ever allocated must be back in the pool.
            if (sink.TryAcquireLatest(out var last))
                last.Return();
            Assert.Equal(sink.Pool.TotalAllocated, sink.Pool.PooledCount);
        }
    }
}

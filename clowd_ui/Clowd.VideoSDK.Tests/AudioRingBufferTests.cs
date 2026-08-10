using System;
using System.Threading.Tasks;
using Clowd.VideoSDK.Playback;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    public class AudioRingBufferTests
    {
        [Fact]
        public void Write_then_read_round_trips()
        {
            var ring = new AudioRingBuffer(16);
            var src = new float[] { 1, 2, 3, 4, 5 };
            Assert.Equal(5, ring.Write(src));
            Assert.Equal(5, ring.Available);

            var dst = new float[5];
            Assert.Equal(5, ring.Read(dst));
            Assert.Equal(src, dst);
            Assert.Equal(0, ring.Available);
        }

        [Fact]
        public void Write_is_bounded_by_capacity()
        {
            var ring = new AudioRingBuffer(8);
            Assert.Equal(8, ring.Write(new float[12]));
            Assert.Equal(0, ring.Write(new float[4]));
            Assert.Equal(0, ring.Free);
        }

        [Fact]
        public void Wraparound_preserves_order()
        {
            var ring = new AudioRingBuffer(8);
            ring.Write(new float[] { 1, 2, 3, 4, 5, 6 });
            var dst = new float[4];
            ring.Read(dst); // consume 4, tail=4
            ring.Write(new float[] { 7, 8, 9, 10, 11, 12 }); // wraps

            var all = new float[8];
            Assert.Equal(8, ring.Read(all));
            Assert.Equal(new float[] { 5, 6, 7, 8, 9, 10, 11, 12 }, all);
        }

        [Fact]
        public void Read_from_empty_returns_zero()
        {
            var ring = new AudioRingBuffer(8);
            Assert.Equal(0, ring.Read(new float[4]));
        }

        [Fact]
        public void Clear_discards_buffered_samples()
        {
            var ring = new AudioRingBuffer(8);
            ring.Write(new float[6]);
            ring.Clear();
            Assert.Equal(0, ring.Available);
            Assert.Equal(8, ring.Free);

            // and the ring still works afterwards
            Assert.Equal(3, ring.Write(new float[] { 1, 2, 3 }));
            var dst = new float[3];
            Assert.Equal(3, ring.Read(dst));
            Assert.Equal(new float[] { 1, 2, 3 }, dst);
        }

        [Fact]
        public async Task Concurrent_producer_consumer_transfers_everything_in_order()
        {
            var ring = new AudioRingBuffer(1024);
            const int total = 200_000;

            var producer = Task.Run(() =>
            {
                var chunk = new float[311];
                int produced = 0;
                while (produced < total)
                {
                    int n = Math.Min(chunk.Length, total - produced);
                    for (int i = 0; i < n; i++)
                        chunk[i] = produced + i;
                    int off = 0;
                    while (off < n)
                    {
                        int w = ring.Write(chunk.AsSpan(off, n - off));
                        off += w;
                        if (w == 0)
                            Task.Yield().GetAwaiter().GetResult();
                    }

                    produced += n;
                }
            });

            var consumed = 0;
            var dst = new float[473];
            while (consumed < total)
            {
                int r = ring.Read(dst);
                for (int i = 0; i < r; i++)
                    Assert.Equal(consumed + i, dst[i]);
                consumed += r;
                if (r == 0)
                    await Task.Yield();
            }

            await producer;
            Assert.Equal(0, ring.Available);
        }
    }
}

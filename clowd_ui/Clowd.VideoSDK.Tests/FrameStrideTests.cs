using System;
using System.IO;
using System.Runtime.InteropServices;
using Clowd.VideoSDK.Composition;
using Clowd.VideoSDK.Media;
using Clowd.VideoSDK.Thumbs;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The stride and the slack a pooled BGRA frame is built with, both of which exist for
    /// swscale's benefit rather than ours.
    ///
    /// <para>
    /// swscale's unscaled yuv420p-to-BGRA converter writes whole 16-pixel blocks with no scalar
    /// tail, and decides per row how many blocks fit the stride it was handed. At a tight
    /// <c>width * 4</c> stride that costs a frame one of two ways, and both of them are silent:
    /// a width whose remainder mod 16 falls in 1..7 loses its last 1..7 columns on every row (a
    /// 1366-wide frame keeps 6 columns of whatever the pooled buffer held before), and a frame
    /// narrower than a block gets a full block written anyway, past the end of an exactly-sized
    /// allocation and into the next heap block — a 2x2 frame overran its 16 bytes by 56 and took
    /// the test process down somewhere else entirely, minutes later.
    /// </para>
    /// </summary>
    public class FrameStrideTests
    {
        /// <summary>A 2x2 frame is the smallest thing the suite decodes (see
        /// <see cref="PixelAspectTests"/>) and the one that overran.</summary>
        [Theory]
        [InlineData(1, 64)]
        [InlineData(2, 64)]
        [InlineData(16, 64)]
        [InlineData(17, 128)]
        [InlineData(500, 2048)]   // 500 * 4 = 2000, mod 16 == 4
        [InlineData(1360, 5440)]  // already block-aligned: unchanged
        [InlineData(1366, 5504)]  // 1366 * 4 = 5464, mod 16 == 6
        [InlineData(1920, 7680)]
        public void The_stride_rounds_the_used_width_up_to_a_whole_block(int width, int expected)
        {
            int rowBytes = FrameBufferPool.BgraRowBytes(width);

            Assert.Equal(expected, rowBytes);
            Assert.True(rowBytes >= width * 4, "the stride never loses columns");
            Assert.Equal(0, rowBytes % 64);
        }

        /// <summary>End to end through the render path's decoder: a solid frame decodes solid all
        /// the way to its right edge. At a tight stride the last 6 columns of a 1366-wide frame
        /// were never written and held the pool buffer's previous contents.</summary>
        [Theory]
        [InlineData(18)]   // mod 16 == 2 — the narrowest frame that loses columns
        [InlineData(1366)] // mod 16 == 6 — columns were dropped
        [InlineData(500)]  // mod 16 == 4 — columns were dropped
        [InlineData(1360)] // mod 16 == 0 — control, always worked
        public void Every_column_of_a_decoded_frame_is_written(int width)
        {
            Assert.SkipUnless(TestFFmpeg.Available, TestFFmpeg.SkipReason);
            const int Height = 64;
            const byte Green = 0xC0;

            string path = Path.Combine(Path.GetTempPath(), $"clowd-stride-{width}-{Guid.NewGuid():N}.mp4");
            try
            {
                WriteSolid(path, width, Height, Green);

                using var pool = new FrameBufferPool();
                using var decoder = new SyncStreamDecoder(path, 0, pool);
                Assert.True(decoder.DecodeNext(out _, out var buffer, out int w, out int h, out int rowBytes));
                try
                {
                    var pixels = new byte[rowBytes * h];
                    Marshal.Copy(buffer.Address, pixels, 0, pixels.Length);

                    // the whole frame is one colour, so every used pixel must land near it —
                    // 4:2:0 and the BGRA conversion are lossy, hence a band rather than equality
                    for (int y = 0; y < h; y++)
                        for (int x = 0; x < w; x++)
                            Assert.InRange(pixels[y * rowBytes + x * 4 + 1], Green - 0x30, Green + 0x30);
                }
                finally
                {
                    buffer.Return();
                }
            }
            finally
            {
                try { File.Delete(path); }
                catch { /* best effort */ }
            }
        }

        /// <summary>The same defect through <see cref="ThumbnailDecoder"/>, which writes into a
        /// MANAGED array: a source already at the thumb's height scales 1:1, so swscale takes the
        /// same unscaled converter. At a tight stride a 20-wide thumb kept 4 columns of whatever
        /// the array held, and the blocks written past the last row landed on the GC heap.</summary>
        [Theory]
        [InlineData(20)]  // mod 16 == 4 — columns were dropped
        [InlineData(100)] // mod 16 == 4 — columns were dropped
        [InlineData(24)]  // mod 16 == 8 — written, but a block ran past the array's end
        [InlineData(32)]  // mod 16 == 0 — control
        public void Every_column_of_an_unscaled_thumbnail_is_written(int width)
        {
            Assert.SkipUnless(TestFFmpeg.Available, TestFFmpeg.SkipReason);
            int height = ThumbnailDecoder.DefaultThumbHeightPx; // source height == thumb height: 1:1
            const byte Green = 0xC0;

            string path = Path.Combine(Path.GetTempPath(), $"clowd-thumbstride-{width}-{Guid.NewGuid():N}.mp4");
            try
            {
                WriteSolid(path, width, height, Green);

                using var decoder = new ThumbnailDecoder(path, 0);
                Assert.Equal(decoder.SourceWidth, decoder.ThumbWidth);   // the 1:1 case this covers
                Assert.Equal(decoder.SourceHeight, decoder.ThumbHeight);
                Assert.True(decoder.DecodeNext(out _));

                var pixels = decoder.CopyThumb();
                for (int y = 0; y < decoder.ThumbHeight; y++)
                    for (int x = 0; x < decoder.ThumbWidth; x++)
                        Assert.InRange(pixels[y * decoder.ThumbStride + x * 4 + 1], Green - 0x30, Green + 0x30);
            }
            finally
            {
                try { File.Delete(path); }
                catch { /* best effort */ }
            }
        }

        /// <summary>A shape no real file has but a crafted or broken one can declare: 8194x2,
        /// whose aspect asks for a thumb 2,097,664 pixels wide at the 512px row. Its byte count
        /// is 4,296,015,872 — which wrapped a 32-bit int to 1,048,576, so the decoder allocated
        /// 1 MiB for a destination it then told swscale was gigabytes wide. The width is capped
        /// instead, and the count is checked so a wrap can never be silent again.</summary>
        [Fact]
        public void A_degenerate_shape_cannot_wrap_the_thumbnail_byte_count()
        {
            Assert.SkipUnless(TestFFmpeg.Available, TestFFmpeg.SkipReason);

            string dir = Path.Combine(Path.GetTempPath(), "clowd-thumbwrap-" + Guid.NewGuid().ToString("N"));
            try
            {
                string path = TestY4m.Write(Path.Combine(dir, "wide.y4m"), frames: 2,
                    aspectNum: 1, aspectDen: 1, width: 8194, height: 2);

                using var decoder = new ThumbnailDecoder(path, 0, ThumbnailDecoder.MaxThumbHeightPx);

                Assert.Equal(8194, decoder.SourceWidth);
                Assert.InRange(decoder.ThumbWidth, 2, ThumbnailDecoder.MaxThumbWidthPx);
                Assert.True(decoder.ThumbByteCount > 0, "the byte count wrapped");
                // the count describes the whole buffer, and the buffer is big enough for it
                Assert.Equal((long)decoder.ThumbStride * decoder.ThumbHeight, decoder.ThumbByteCount);
                Assert.True(decoder.DecodeNext(out _));
                Assert.Equal(decoder.ThumbByteCount, decoder.CopyThumb().Length);
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); }
                catch { /* best effort */ }
            }
        }

        private static void WriteSolid(string path, int width, int height, byte green)
        {
            using var writer = new Mp4Writer(path, new Mp4WriterOptions
            {
                Width = width,
                Height = height,
                FpsNum = 30,
                FpsDen = 1,
            });

            var bgra = new byte[width * height * 4];
            for (int i = 0; i < bgra.Length; i += 4)
            {
                bgra[i] = 0x20;          // B
                bgra[i + 1] = green;     // G
                bgra[i + 2] = 0x40;      // R
                bgra[i + 3] = 0xFF;
            }

            var pin = GCHandle.Alloc(bgra, GCHandleType.Pinned);
            try
            {
                for (int n = 0; n < 4; n++)
                    writer.SubmitVideoFrame(pin.AddrOfPinnedObject(), width * 4, width, height, n);
            }
            finally
            {
                pin.Free();
            }

            writer.Finish();
        }
    }
}

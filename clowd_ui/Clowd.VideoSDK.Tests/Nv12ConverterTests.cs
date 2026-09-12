using System;
using System.IO;
using System.Runtime.InteropServices;
using Clowd.VideoSDK.Media;
using Clowd.VideoSDK.Playback;
using FFmpeg.AutoGen.Abstractions;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    // The render pipeline's convert stage: slice-threaded sws_scale_frame must produce exactly
    // what the legacy single-threaded sws_scale produces, so the staged loop encodes the same
    // bytes the old loop did. Needs the FFmpeg natives (skips without them).
    public class Nv12ConverterTests
    {
        private static void RequireFFmpeg() => Assert.SkipUnless(TestFFmpeg.Available, TestFFmpeg.SkipReason);

        /// <summary>Deterministic BGRA noise with sharp colour structure (gradients plus a
        /// checker), rows of <paramref name="rowBytes"/> with junk in any padding.</summary>
        private static byte[] NoiseFrame(int w, int h, int rowBytes, int seed)
        {
            var rng = new Random(seed);
            var pixels = new byte[rowBytes * h];
            rng.NextBytes(pixels); // padding (and everything else) starts as junk
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * rowBytes + x * 4;
                    bool checker = ((x / 5) + (y / 3)) % 2 == 0;
                    pixels[i] = (byte)(checker ? x * 255 / w : rng.Next(256));   // B
                    pixels[i + 1] = (byte)(checker ? y * 255 / h : rng.Next(256)); // G
                    pixels[i + 2] = (byte)((x * y) & 0xff);                        // R
                    pixels[i + 3] = 0xff;
                }
            }
            return pixels;
        }

        /// <summary>The two NV12 planes, tightly packed: Y (w x h) and interleaved UV (w x h/2).</summary>
        private static byte[][] Planes(Nv12Frame frame)
        {
            var planes = new byte[2][];
            for (int p = 0; p < 2; p++)
            {
                int ph = p == 0 ? frame.Height : frame.Height / 2;
                var (address, stride) = frame.Plane(p);
                planes[p] = new byte[frame.Width * ph];
                for (int y = 0; y < ph; y++)
                    Marshal.Copy(address + y * stride, planes[p], y * frame.Width, frame.Width);
            }
            return planes;
        }

        /// <summary>The conversion as the writer's original single-threaded path did it: the
        /// legacy API on a context from sws_getContext with the same flags.</summary>
        private static unsafe byte[][] LegacyConvert(byte[] bgra, int rowBytes, int w, int h)
        {
            using var dst = new Nv12Frame(w, h);
            var sws = ffmpeg.sws_getContext(w, h, AVPixelFormat.AV_PIX_FMT_BGRA, w, h,
                AVPixelFormat.AV_PIX_FMT_NV12, ffmpeg.SWS_BILINEAR, null, null, null);
            Assert.True(sws != null, "sws_getContext failed");
            try
            {
                fixed (byte* src = bgra)
                {
                    var srcData = new[] { src, null, null, null };
                    var srcStride = new[] { rowBytes, 0, 0, 0 };
                    var dstData = new byte*[4];
                    var dstStride = new int[4];
                    for (uint i = 0; i < 4; i++)
                    {
                        dstData[i] = dst.Frame->data[i];
                        dstStride[i] = dst.Frame->linesize[i];
                    }
                    Assert.Equal(h, ffmpeg.sws_scale(sws, srcData, srcStride, 0, h, dstData, dstStride));
                }
            }
            finally
            {
                ffmpeg.sws_freeContext(sws);
            }
            return Planes(dst);
        }

        private static byte[][] Convert(byte[] bgra, int rowBytes, int w, int h, int threads)
        {
            using var converter = new Nv12Converter(w, h, w, h, threads);
            Assert.Equal(threads, converter.Threads);
            using var dst = new Nv12Frame(w, h);
            var pin = GCHandle.Alloc(bgra, GCHandleType.Pinned);
            try
            {
                converter.Convert(pin.AddrOfPinnedObject(), rowBytes, dst);
            }
            finally
            {
                pin.Free();
            }
            return Planes(dst);
        }

        private static void AssertPlanesEqual(byte[][] expected, byte[][] actual, string what)
        {
            Assert.Equal(expected.Length, actual.Length);
            for (int p = 0; p < expected.Length; p++)
            {
                Assert.Equal(expected[p].Length, actual[p].Length);
                for (int i = 0; i < expected[p].Length; i++)
                {
                    if (expected[p][i] != actual[p][i])
                        Assert.Fail($"{what}: plane {p} byte {i} is {actual[p][i]}, expected {expected[p][i]}");
                }
            }
        }

        [Theory]
        [InlineData(322, 146)] // awkward for slicing: 146 rows do not split evenly across 4 or 8
        [InlineData(64, 64)]
        [InlineData(1240, 30)]
        public void Threaded_conversion_matches_legacy_sws_scale_exactly(int w, int h)
        {
            RequireFFmpeg();
            var bgra = NoiseFrame(w, h, w * 4, seed: w + h);
            var legacy = LegacyConvert(bgra, w * 4, w, h);
            AssertPlanesEqual(legacy, Convert(bgra, w * 4, w, h, threads: 1), "1 thread");
            AssertPlanesEqual(legacy, Convert(bgra, w * 4, w, h, threads: 4), "4 threads");
            AssertPlanesEqual(legacy, Convert(bgra, w * 4, w, h, threads: 8), "8 threads");
        }

        /// <summary>NV12 is the same 4:2:0 picture as yuv420p with the chroma interleaved, and
        /// swscale must compute the same values for it — the guarantee that switching the
        /// pipeline's target from yuv420p to NV12 changed no pixel.</summary>
        [Fact]
        public unsafe void Nv12_carries_the_same_samples_as_yuv420p()
        {
            RequireFFmpeg();
            const int w = 322, h = 146;
            var bgra = NoiseFrame(w, h, w * 4, seed: 3);
            var nv12 = Convert(bgra, w * 4, w, h, threads: 4);

            var planar = ffmpeg.av_frame_alloc();
            planar->format = (int)AVPixelFormat.AV_PIX_FMT_YUV420P;
            planar->width = w;
            planar->height = h;
            Assert.True(ffmpeg.av_frame_get_buffer(planar, 0) >= 0);
            var sws = ffmpeg.sws_getContext(w, h, AVPixelFormat.AV_PIX_FMT_BGRA, w, h,
                AVPixelFormat.AV_PIX_FMT_YUV420P, ffmpeg.SWS_BILINEAR, null, null, null);
            try
            {
                fixed (byte* src = bgra)
                {
                    var srcData = new[] { src, null, null, null };
                    var srcStride = new[] { w * 4, 0, 0, 0 };
                    var dstData = new byte*[4];
                    var dstStride = new int[4];
                    for (uint i = 0; i < 4; i++)
                    {
                        dstData[i] = planar->data[i];
                        dstStride[i] = planar->linesize[i];
                    }
                    Assert.Equal(h, ffmpeg.sws_scale(sws, srcData, srcStride, 0, h, dstData, dstStride));
                }

                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        Assert.Equal(planar->data[0][y * planar->linesize[0] + x], nv12[0][y * w + x]);
                for (int y = 0; y < h / 2; y++)
                {
                    for (int x = 0; x < w / 2; x++)
                    {
                        Assert.Equal(planar->data[1][y * planar->linesize[1] + x], nv12[1][y * w + x * 2]);
                        Assert.Equal(planar->data[2][y * planar->linesize[2] + x], nv12[1][y * w + x * 2 + 1]);
                    }
                }
            }
            finally
            {
                ffmpeg.sws_freeContext(sws);
                ffmpeg.av_frame_free(&planar);
            }
        }

        [Fact]
        public void Padded_source_rows_convert_identically()
        {
            RequireFFmpeg();
            // a D3D12 readback heap pads rows to 256 bytes: 333 * 4 = 1332 -> 1536, and its
            // footprint ends with the last row's pixels (RowPitch * (h - 1) + w * 4, no trailing
            // padding) — the buffer is sized exactly so, as the readback ring's memory is
            const int w = 333 & ~1, h = 40, tight = w * 4, padded = 1536;
            var tightFrame = NoiseFrame(w, h, tight, seed: 7);
            var paddedFrame = new byte[padded * (h - 1) + tight];
            new Random(99).NextBytes(paddedFrame); // junk in the padding
            for (int y = 0; y < h; y++)
                Array.Copy(tightFrame, y * tight, paddedFrame, y * padded, tight);

            AssertPlanesEqual(Convert(tightFrame, tight, w, h, 1), Convert(paddedFrame, padded, w, h, 4), "padded rows");
        }

        [Fact]
        public void Default_threads_are_bounded()
        {
            Assert.InRange(Nv12Converter.DefaultThreads, 1, 8);
        }

        [Fact]
        public void Converter_validates_its_inputs()
        {
            RequireFFmpeg();
            Assert.Throws<ArgumentOutOfRangeException>(() => new Nv12Converter(0, 8, 8, 8));
            Assert.Throws<ArgumentOutOfRangeException>(() => new Nv12Converter(8, 8, 7, 8));  // odd output
            Assert.Throws<ArgumentOutOfRangeException>(() => new Nv12Frame(8, 7));
            using var converter = new Nv12Converter(8, 8, 8, 8, 1);
            using var wrong = new Nv12Frame(16, 16);
            var pixels = new byte[8 * 4 * 8];
            var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                Assert.Throws<ArgumentException>(() => converter.Convert(pin.AddrOfPinnedObject(), 8 * 4, wrong));
                using var right = new Nv12Frame(8, 8);
                Assert.Throws<ArgumentOutOfRangeException>(() => converter.Convert(pin.AddrOfPinnedObject(), 8 * 3, right));
                Assert.Throws<ArgumentNullException>(() => converter.Convert(IntPtr.Zero, 8 * 4, right));
                Assert.Throws<ArgumentOutOfRangeException>(() => right.Plane(2));
            }
            finally
            {
                pin.Free();
            }
        }

        [Fact]
        public void FillBlack_writes_limited_range_black()
        {
            RequireFFmpeg();
            using var frame = new Nv12Frame(16, 8);
            frame.FillBlack();
            var planes = Planes(frame);
            Assert.All(planes[0], b => Assert.Equal(16, b));
            Assert.All(planes[1], b => Assert.Equal(128, b));
        }

        [Fact]
        public void Writer_encodes_pre_converted_frames_identically_to_bgra_frames()
        {
            RequireFFmpeg();
            const int w = 64, h = 48, fps = 30, frames = 45;
            string viaBgra = TempMp4(), viaNv12 = TempMp4();
            try
            {
                var options = new Mp4WriterOptions { Width = w, Height = h, FpsNum = fps, FpsDen = 1 };

                using (var writer = new Mp4Writer(viaBgra, options))
                {
                    for (int n = 0; n < frames; n++)
                    {
                        var bgra = NoiseFrame(w, h, w * 4, seed: n);
                        var pin = GCHandle.Alloc(bgra, GCHandleType.Pinned);
                        try
                        {
                            writer.SubmitVideoFrame(pin.AddrOfPinnedObject(), w * 4, w, h, n);
                        }
                        finally
                        {
                            pin.Free();
                        }
                    }
                    writer.Finish();
                }

                using (var writer = new Mp4Writer(viaNv12, options))
                using (var converter = new Nv12Converter(w, h, w, h))
                {
                    // two frames in rotation, as the render pipeline's pool does
                    var pool = new[] { new Nv12Frame(w, h), new Nv12Frame(w, h) };
                    try
                    {
                        for (int n = 0; n < frames; n++)
                        {
                            var bgra = NoiseFrame(w, h, w * 4, seed: n);
                            var pin = GCHandle.Alloc(bgra, GCHandleType.Pinned);
                            try
                            {
                                converter.Convert(pin.AddrOfPinnedObject(), w * 4, pool[n % 2]);
                            }
                            finally
                            {
                                pin.Free();
                            }
                            writer.SubmitVideoFrame(pool[n % 2], n);
                        }
                        writer.Finish();
                    }
                    finally
                    {
                        foreach (var f in pool)
                            f.Dispose();
                    }
                }

                // x264 is deterministic for identical input, so the files match byte for byte
                Assert.Equal(File.ReadAllBytes(viaBgra), File.ReadAllBytes(viaNv12));
                var probe = MediaProbe.ProbeDetailed(viaNv12);
                Assert.Equal(w, Assert.Single(probe.VideoStreams).Width);
            }
            finally
            {
                File.Delete(viaBgra);
                File.Delete(viaNv12);
            }
        }

        /// <summary>The pool hands a frame back to the convert stage as soon as the writer returns,
        /// which is only safe because the encoder copies the picture inside avcodec_send_frame and
        /// drops its reference before returning — the buffer must be exclusively ours again.
        /// Checked for x264 always, and for NVENC (whose system-memory path copies into a locked
        /// input buffer) when it opens on this machine.</summary>
        [Theory]
        [InlineData(VideoEncoder.Software)]
        [InlineData(VideoEncoder.Nvenc)]
        public unsafe void Encoder_releases_the_frame_when_submit_returns(VideoEncoder encoder)
        {
            RequireFFmpeg();
            if (encoder != VideoEncoder.Software)
                Assert.SkipUnless(H264EncoderProbe.CanOpen(encoder, out var reason), $"{encoder} unavailable: {reason}");
            string path = TempMp4();
            try
            {
                const int w = H264EncoderProbe.ProbeWidth, h = H264EncoderProbe.ProbeHeight;
                using var writer = new Mp4Writer(path, new Mp4WriterOptions { Width = w, Height = h, FpsNum = 30, Encoder = encoder });
                Assert.Equal(encoder, writer.Encoder);
                using var converter = new Nv12Converter(w, h, w, h, 2);
                using var frame = new Nv12Frame(w, h);
                var bgra = NoiseFrame(w, h, w * 4, seed: 1);
                var pin = GCHandle.Alloc(bgra, GCHandleType.Pinned);
                try
                {
                    for (int n = 0; n < 8; n++)
                    {
                        converter.Convert(pin.AddrOfPinnedObject(), w * 4, frame);
                        byte* planeBefore = frame.Frame->data[0];
                        writer.SubmitVideoFrame(frame, n);
                        Assert.Equal(1, ffmpeg.av_buffer_is_writable(frame.Frame->buf[0]));
                        // and Convert did not have to swap in fresh planes on the next round
                        converter.Convert(pin.AddrOfPinnedObject(), w * 4, frame);
                        Assert.True(planeBefore == frame.Frame->data[0], "frame planes were reallocated");
                    }
                }
                finally
                {
                    pin.Free();
                }
                writer.Finish();
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Writer_rejects_a_converted_frame_of_the_wrong_size()
        {
            RequireFFmpeg();
            string path = TempMp4();
            try
            {
                using var writer = new Mp4Writer(path, new Mp4WriterOptions { Width = 64, Height = 64, FpsNum = 30 });
                using var wrong = new Nv12Frame(32, 32);
                Assert.Throws<ArgumentException>(() => writer.SubmitVideoFrame(wrong, 0));
                writer.Abandon();
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static string TempMp4() =>
            Path.Combine(Path.GetTempPath(), $"clowd-nv12-test-{Guid.NewGuid():N}.mp4");
    }
}

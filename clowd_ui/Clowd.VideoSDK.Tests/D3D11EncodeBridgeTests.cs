using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Clowd.VideoSDK.Composition;
using Clowd.VideoSDK.Media;
using Clowd.VideoSDK.Model;
using Clowd.VideoSDK.Playback;
using FFmpeg.AutoGen.Abstractions;
using SkiaSharp;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    // The zero-copy encode path's Direct3D 11 half: the ring's shared textures and fences opened
    // on a D3D11 device, the video-processor BGRA->NV12 conversion, the pooled encoder textures
    // and their release by the encoder. Needs a D3D12 GPU whose driver shares the ring's
    // resources (skips otherwise); the NVENC tests also need an NVIDIA encoder.
    [SupportedOSPlatform("windows")] // every test skips off Windows before touching the D3D types
    [Collection("HardwareFrames")] // HardwareFrame.LiveCount is process-wide: no parallel producers
    public class D3D11EncodeBridgeTests
    {
        private sealed class Rig : IDisposable
        {
            public GpuSurfaceFactory Gpu;
            public D3D12ReadbackRing Ring;
            public D3D11EncodeBridge Bridge;

            public void Dispose()
            {
                Bridge?.Dispose();
                Ring?.Dispose();
                Gpu?.Dispose();
            }
        }

        /// <summary>A shareable ring with no bridge yet, or a skip when this machine cannot
        /// share (no D3D12, a driver refusing shared heaps) or has no FFmpeg (the bridge's frames
        /// context is FFmpeg's).</summary>
        private static Rig RequireShareableRing(int width, int height, int slots)
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "Direct3D is Windows-only.");
            Assert.SkipUnless(TestFFmpeg.Available, TestFFmpeg.SkipReason);
            var rig = new Rig();
            try
            {
                rig.Gpu = GpuSurfaceFactory.TryCreate(out var reason);
                Assert.SkipWhen(rig.Gpu == null, "GPU backend unavailable: " + reason);
                var log = new List<string>();
                rig.Ring = rig.Gpu.CreateReadbackRing(width, height, slots, log.Add) as D3D12ReadbackRing;
                Assert.SkipWhen(rig.Ring == null || !rig.Ring.Shareable,
                    "the driver did not give a shareable ring: " + string.Join(" | ", log));
                return rig;
            }
            catch
            {
                rig.Dispose();
                throw;
            }
        }

        /// <summary>A shareable ring with a bridge over it (or the skips of
        /// <see cref="RequireShareableRing"/>). A shareable ring on which the bridge fails to
        /// come up is a failure, not a skip: that is exactly the regression to catch.</summary>
        private static Rig Require(int width, int height, int slots)
        {
            var rig = RequireShareableRing(width, height, slots);
            try
            {
                rig.Bridge = D3D11EncodeBridge.TryCreate(rig.Ring, out var reason);
                Assert.True(rig.Bridge != null, "the bridge did not come up on a shareable ring: " + reason);
                return rig;
            }
            catch
            {
                rig.Dispose();
                throw;
            }
        }

        /// <summary>Draws one frame: left half <paramref name="left"/>, right half
        /// <paramref name="right"/>, a 16x16 <paramref name="corner"/> block at the top-left —
        /// enough to detect a flip, a swap or a scale as well as a wrong matrix.</summary>
        private static void DrawHalves(SKCanvas canvas, int w, int h, SKColor left, SKColor right, SKColor corner)
        {
            canvas.Clear(left);
            using var paint = new SKPaint { IsAntialias = false, Color = right };
            canvas.DrawRect(w / 2, 0, w - w / 2, h, paint);
            paint.Color = corner;
            canvas.DrawRect(0, 0, 16, 16, paint);
        }

        private static (int Y, int U, int V) Sample((byte[] Y, byte[] Uv) planes, int w, int x, int y)
            => (planes.Y[y * w + x], planes.Uv[(y / 2) * w + (x / 2) * 2], planes.Uv[(y / 2) * w + (x / 2) * 2 + 1]);

        private static void AssertBt601(SKColor color, (int Y, int U, int V) sample, string where, int tolerance = 4)
        {
            var (ey, eu, ev) = D3D11EncodeBridge.Bt601Limited(color);
            Assert.True(Math.Abs(sample.Y - ey) <= tolerance && Math.Abs(sample.U - eu) <= tolerance && Math.Abs(sample.V - ev) <= tolerance,
                $"{where}: got YUV ({sample.Y},{sample.U},{sample.V}) for {color}, expected BT.601 limited ({ey:F1},{eu:F1},{ev:F1})");
        }

        /// <summary>One frame through the shared-slot protocol: draw, submit for the bridge,
        /// convert, release the slot; returns the encoder frame.</summary>
        private static HardwareFrame RoundTrip(Rig rig, int slot, Action<SKCanvas> draw)
        {
            draw(rig.Ring.Begin(slot));
            ulong fence = rig.Bridge.SubmitFrame(slot);
            var frame = rig.Bridge.Convert(slot, fence);
            rig.Bridge.ReleaseSlot(slot);
            return frame;
        }

        [Fact]
        public void Converts_a_composed_frame_to_bt601_limited_nv12_in_place()
        {
            const int w = 160, h = 120;
            using var rig = Require(w, h, 2);
            Assert.Contains("VideoProcessor BGRA->NV12", rig.Bridge.Description, StringComparison.Ordinal);
            Assert.Equal(w, rig.Bridge.Frames.Width);
            Assert.Equal(h, rig.Bridge.Frames.Height);

            var left = new SKColor(200, 60, 30);
            var right = new SKColor(20, 90, 220);
            var corner = new SKColor(240, 240, 240);
            using var frame = RoundTrip(rig, 1, c => DrawHalves(c, w, h, left, right, corner));
            Assert.Equal(w, frame.Width);
            Assert.Equal(h, frame.Height);

            var planes = rig.Bridge.ReadNv12(frame.Texture);
            Assert.Equal(w * h, planes.Y.Length);
            Assert.Equal(w * h / 2, planes.Uv.Length);
            AssertBt601(corner, Sample(planes, w, 4, 4), "top-left corner block");
            AssertBt601(left, Sample(planes, w, 40, 60), "left half");
            AssertBt601(left, Sample(planes, w, 8, h - 4), "bottom-left");
            AssertBt601(right, Sample(planes, w, 120, 60), "right half");
            AssertBt601(right, Sample(planes, w, w - 2, 2), "top-right");
            AssertBt601(right, Sample(planes, w, w - 2, h - 2), "bottom-right");
        }

        /// <summary>The conversion must land on what the readback path's swscale produces for the
        /// same pixels, since the two paths stand in for each other: compare a full frame of
        /// flat colours against <see cref="Nv12Converter"/>.</summary>
        [Fact]
        public void Conversion_matches_swscale_within_a_level_or_two()
        {
            Assert.SkipUnless(TestFFmpeg.Available, TestFFmpeg.SkipReason);
            const int w = 96, h = 64;
            using var rig = Require(w, h, 2);
            var left = new SKColor(180, 120, 40);
            var right = new SKColor(40, 200, 160);
            var corner = new SKColor(10, 10, 10);
            using var frame = RoundTrip(rig, 0, c => DrawHalves(c, w, h, left, right, corner));
            var gpu = rig.Bridge.ReadNv12(frame.Texture);

            // the same picture through swscale: draw it on the CPU, convert with the pipeline's converter
            var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var bitmap = new SKBitmap(info);
            using (var surface = SKSurface.Create(info, bitmap.GetPixels(), bitmap.RowBytes))
                DrawHalves(surface.Canvas, w, h, left, right, corner);
            using var converter = new Nv12Converter(w, h, w, h, 1);
            using var nv12 = new Nv12Frame(w, h);
            converter.Convert(bitmap.GetPixels(), bitmap.RowBytes, nv12);

            // Luma is per pixel and must agree everywhere. Chroma is subsampled, and the two
            // converters filter differently across a colour edge (swscale's bilinear taps against
            // the video processor's), so chroma is compared only where the source is flat around
            // the sample — a 6x6 neighbourhood, wider than either filter's support.
            int worstY = 0, worstUv = 0, comparedUv = 0;
            var (yAddr, yStride) = nv12.Plane(0);
            var (uvAddr, uvStride) = nv12.Plane(1);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int sws = Marshal.ReadByte(yAddr + y * yStride + x);
                    worstY = Math.Max(worstY, Math.Abs(sws - gpu.Y[y * w + x]));
                }
            }
            bool Flat(int cx, int cy)
            {
                uint first = (uint)bitmap.GetPixel(2 * cx, 2 * cy);
                for (int sy = Math.Max(0, 2 * cy - 2); sy <= Math.Min(h - 1, 2 * cy + 3); sy++)
                    for (int sx = Math.Max(0, 2 * cx - 2); sx <= Math.Min(w - 1, 2 * cx + 3); sx++)
                        if ((uint)bitmap.GetPixel(sx, sy) != first)
                            return false;
                return true;
            }
            for (int cy = 0; cy < h / 2; cy++)
            {
                for (int cx = 0; cx < w / 2; cx++)
                {
                    if (!Flat(cx, cy))
                        continue;
                    comparedUv++;
                    for (int p = 0; p < 2; p++)
                    {
                        int sws = Marshal.ReadByte(uvAddr + cy * uvStride + cx * 2 + p);
                        worstUv = Math.Max(worstUv, Math.Abs(sws - gpu.Uv[cy * w + cx * 2 + p]));
                    }
                }
            }
            Assert.True(comparedUv > w * h / 8, $"only {comparedUv} flat chroma samples to compare");
            Assert.True(worstY <= 2 && worstUv <= 2,
                $"video processor vs swscale: worst luma difference {worstY}, worst chroma difference {worstUv} over {comparedUv} flat chroma samples");
        }

        [Fact]
        public unsafe void Frames_reference_pool_textures_that_return_when_the_last_reference_goes()
        {
            const int w = 64, h = 48;
            using var rig = Require(w, h, 3);
            var frames = rig.Bridge.Frames;
            Assert.Equal(0, frames.PoolInUse); // the self-check gave its texture back

            // three frames held at once (as an encoder holds its in-flight inputs): the pool grows to three
            var held = new List<HardwareFrame>();
            for (int n = 0; n < 3; n++)
            {
                var color = new SKColor((byte)(40 * n), (byte)(200 - 50 * n), 90);
                var frame = RoundTrip(rig, n, c => c.Clear(color));
                held.Add(frame);
                // the AVFrame is shaped the way nvenc.c / amfenc.c read it
                var av = frame.Frame;
                Assert.Equal((int)AVPixelFormat.AV_PIX_FMT_D3D11, av->format);
                Assert.Equal(frame.Texture, (IntPtr)av->data[0]);
                Assert.Equal(IntPtr.Zero, (IntPtr)av->data[1]); // array slice 0
                Assert.True(av->buf[0] != null, "no buffer reference");
                Assert.True(av->hw_frames_ctx != null, "no frames context reference");
                Assert.Equal(w, av->width);
                Assert.Equal(h, av->height);
            }
            Assert.Equal(3, frames.PoolInUse);
            Assert.Equal(3, frames.PoolPeakInUse);
            Assert.Equal(3, frames.PoolTextures);
            Assert.Equal(3, new HashSet<IntPtr> { held[0].Texture, held[1].Texture, held[2].Texture }.Count);

            // each holds its own picture
            for (int n = 0; n < 3; n++)
                AssertBt601(new SKColor((byte)(40 * n), (byte)(200 - 50 * n), 90), Sample(rig.Bridge.ReadNv12(held[n].Texture), w, w / 2, h / 2), $"held frame {n}");

            // a second reference (the encoder's) keeps a texture out after ours is dropped
            var encoderHold = ffmpeg.av_buffer_ref(held[0].Frame->buf[0]);
            held[0].Dispose();
            Assert.Equal(3, frames.PoolInUse);
            ffmpeg.av_buffer_unref(&encoderHold);
            Assert.Equal(2, frames.PoolInUse);
            held[1].Dispose();
            held[2].Dispose();
            Assert.Equal(0, frames.PoolInUse);

            // frames dropped as soon as they are made: the pool does not grow past what is held
            for (int n = 0; n < 9; n++)
                RoundTrip(rig, n % 3, c => c.Clear(SKColors.Gray)).Dispose();
            Assert.Equal(3, frames.PoolTextures);
            Assert.Equal(0, frames.PoolInUse);
        }

        [Fact]
        public void Shared_and_readback_submits_release_through_their_own_calls_only()
        {
            using var rig = Require(32, 32, 2);

            rig.Ring.Begin(0).Clear(SKColors.Red);
            ulong fence = rig.Bridge.SubmitFrame(0);
            Assert.Throws<InvalidOperationException>(() => rig.Ring.Wait(0)); // shared: no pixels to read back
            Assert.Throws<InvalidOperationException>(() => rig.Ring.Begin(0)); // in flight
            rig.Bridge.Convert(0, fence).Dispose();
            rig.Bridge.ReleaseSlot(0);
            rig.Ring.Begin(0); // idle again
            Assert.Throws<InvalidOperationException>(() => rig.Bridge.ReleaseSlot(0)); // composing, not submitted

            rig.Ring.Begin(1).Clear(SKColors.Blue);
            rig.Ring.Submit(1); // the readback protocol
            Assert.Throws<InvalidOperationException>(() => rig.Ring.ReleaseShared(1));
            rig.Ring.Wait(1);
        }

        /// <summary>A self-check that fails after the slot was submitted — here the first pool
        /// allocation, standing in for a driver refusing the first GPU wait or blit — must hand
        /// slot 0 back to the ring idle: TryCreate's caller falls back to readback over the same
        /// ring, and its first Begin is on that very slot. The frame count must not move either
        /// (nothing was wrapped), and a healthy bridge must still come up over the ring.</summary>
        [Fact]
        public void Failed_self_check_leaves_the_ring_idle_for_readback()
        {
            const int w = 48, h = 32;
            using var rig = RequireShareableRing(w, h, 2);
            int live = HardwareFrame.LiveCount;

            var failed = D3D11EncodeBridge.TryCreate(rig.Ring, out var reason, bridge => bridge.Frames.Dispose());
            Assert.Null(failed);
            Assert.Contains("HardwareFrames", reason, StringComparison.Ordinal); // the disposed pool, not a ring-state message
            Assert.Equal(live, HardwareFrame.LiveCount);

            // the readback protocol on slot 0: what RenderJob does next
            rig.Ring.Begin(0).Clear(new SKColor(0x11, 0x22, 0x33));
            rig.Ring.Submit(0);
            var pixels = rig.Ring.Wait(0);
            var first = new byte[4];
            Marshal.Copy(pixels.Address, first, 0, 4);
            Assert.Equal(new byte[] { 0x33, 0x22, 0x11, 0xFF }, first);

            // and the shared protocol again, through a bridge that does come up
            rig.Bridge = D3D11EncodeBridge.TryCreate(rig.Ring, out reason);
            Assert.True(rig.Bridge != null, "the bridge did not come up after a failed attempt: " + reason);
            var probe = new SKColor(200, 60, 30);
            using var frame = RoundTrip(rig, 0, c => c.Clear(probe));
            AssertBt601(probe, Sample(rig.Bridge.ReadNv12(frame.Texture), w, w / 2, h / 2), "after recovery");
            Assert.Equal(live + 1, HardwareFrame.LiveCount);
        }

        /// <summary>The convert stage's half of the protocol on its own thread, as the pipeline
        /// runs it (a plain thread, joined: the bridge is used from one thread at a time).</summary>
        private static HardwareFrame ConvertOnAnotherThread(Rig rig, int slot, ulong fence)
        {
            HardwareFrame frame = null;
            Exception error = null;
            var thread = new Thread(() =>
            {
                try
                {
                    frame = rig.Bridge.Convert(slot, fence);
                    rig.Bridge.ReleaseSlot(slot);
                }
                catch (Exception ex)
                {
                    error = ex;
                }
            });
            thread.Start();
            thread.Join();
            if (error != null)
                throw new InvalidOperationException("conversion failed on the convert thread", error);
            return frame;
        }

        /// <summary>The pipeline's split: submits on the ring's thread, conversion and release on
        /// another, every slot reused several times, with frames kept alive for a while so the
        /// slot and the pool texture lifetimes visibly differ.</summary>
        [Fact]
        public void Slots_are_reused_correctly_across_threads_while_frames_are_held()
        {
            const int w = 48, h = 40, slots = 3;
            using var rig = Require(w, h, slots);
            var pending = new Queue<(int Frame, HardwareFrame Hw)>();
            int frames = 3 * slots + 1;
            for (int n = 0; n < frames; n++)
            {
                var color = new SKColor((byte)(n * 37), (byte)(n * 91), (byte)(n * 53));
                int slot = n % slots;
                rig.Ring.Begin(slot).Clear(color);
                ulong fence = rig.Bridge.SubmitFrame(slot);
                pending.Enqueue((n, ConvertOnAnotherThread(rig, slot, fence)));
                if (pending.Count > 4)
                {
                    var (index, done) = pending.Dequeue();
                    AssertBt601(new SKColor((byte)(index * 37), (byte)(index * 91), (byte)(index * 53)),
                        Sample(rig.Bridge.ReadNv12(done.Texture), w, w / 2, h / 2), $"frame {index}");
                    done.Dispose();
                }
            }
            while (pending.Count > 0)
            {
                var (index, done) = pending.Dequeue();
                AssertBt601(new SKColor((byte)(index * 37), (byte)(index * 91), (byte)(index * 53)),
                    Sample(rig.Bridge.ReadNv12(done.Texture), w, w / 2, h / 2), $"frame {index}");
                done.Dispose();
            }
            Assert.Equal(0, rig.Bridge.Frames.PoolInUse);
            Assert.InRange(rig.Bridge.Frames.PoolPeakInUse, 5, 6); // four held plus the one in hand
        }

        // ------------------------------------------------------------------ with an encoder

        private static string TempMp4() =>
            Path.Combine(Path.GetTempPath(), $"clowd-bridge-test-{Guid.NewGuid():N}.mp4");

        /// <summary>The centre pixel (BGRA) of frame <paramref name="frame"/> of a rendered file.</summary>
        private static byte[] CenterPixel(string path, int w, int h, int fps, int frame)
        {
            var project = new Project { Output = new OutputSettings { WidthPx = w, HeightPx = h, FpsNum = fps, FpsDen = 1, SampleRate = 48000 } };
            var sourceId = Guid.NewGuid();
            project.Sources.Add(new Source
            {
                Id = sourceId,
                Path = path,
                Streams = { new SourceStream { Index = 0, Kind = StreamKind.Video, Width = w, Height = h, AvgFrameRateNum = fps, AvgFrameRateDen = 1 } },
            });
            using var factory = new CpuSurfaceFactory();
            using var cache = new FrameTextureCache(factory);
            using var source = new SequentialFrameSource(project, cache);
            Assert.True(source.TryGetFrame(sourceId, 0, TimeBase.FrameIndexToTicks(frame, fps, 1), out var frameRef));
            var native = Marshal.AllocHGlobal(4);
            try
            {
                Assert.True(frameRef.Image.ReadPixels(new SKImageInfo(1, 1, SKColorType.Bgra8888, SKAlphaType.Premul), native, 4, w / 2, h / 2));
                var px = new byte[4];
                Marshal.Copy(native, px, 0, 4);
                return px;
            }
            finally
            {
                Marshal.FreeHGlobal(native);
            }
        }

        [Fact]
        public void Nvenc_encodes_bridge_frames_and_hands_the_textures_back()
        {
            Assert.SkipUnless(TestFFmpeg.Available, TestFFmpeg.SkipReason);
            const int w = H264EncoderProbe.ProbeWidth, h = H264EncoderProbe.ProbeHeight, fps = 30, frames = 45;
            using var rig = Require(w, h, 3);
            Assert.SkipUnless(H264EncoderProbe.CanOpen(VideoEncoder.Nvenc, out var reason), "h264_nvenc does not open here: " + reason);

            string path = TempMp4();
            var log = new List<string>();
            try
            {
                using (var writer = new Mp4Writer(path, new Mp4WriterOptions
                {
                    Width = w,
                    Height = h,
                    FpsNum = fps,
                    Encoder = VideoEncoder.Nvenc,
                    HardwareFrames = rig.Bridge.Frames,
                    DiagnosticLog = log.Add,
                }))
                {
                    Assert.Equal(VideoEncoder.Nvenc, writer.Encoder);
                    Assert.Same(rig.Bridge.Frames, writer.HardwareFrames);
                    // the probe's exercise frame and the real context's first registration are back
                    Assert.Equal(0, rig.Bridge.Frames.PoolInUse);

                    for (int n = 0; n < frames; n++)
                    {
                        var color = new SKColor((byte)(60 + 4 * n), (byte)(200 - 3 * n), 80);
                        var frame = RoundTrip(rig, n % 3, c => c.Clear(color));
                        writer.SubmitVideoFrame(frame, n);
                        frame.Dispose(); // the encoder keeps its own reference while it reads
                    }
                    // NVENC holds a lookahead's worth of inputs until they are encoded
                    Assert.True(rig.Bridge.Frames.PoolPeakInUse > 1, $"peak in use {rig.Bridge.Frames.PoolPeakInUse}");
                    writer.Finish();
                    Assert.Equal(0, rig.Bridge.Frames.PoolInUse); // flushed: every input released
                    // system-memory submissions are refused on a hardware-frame writer
                    using var nv12 = new Nv12Frame(w, h);
                    Assert.Throws<InvalidOperationException>(() => writer.SubmitVideoFrame(nv12, frames));
                }

                Assert.Contains(log, line => line.StartsWith("Mp4Writer: video encoder h264_nvenc", StringComparison.Ordinal)
                                             && line.Contains("input: Direct3D 11 NV12 textures", StringComparison.Ordinal));

                var probe = MediaProbe.ProbeDetailed(path);
                var v = Assert.Single(probe.VideoStreams);
                Assert.Equal("h264", v.CodecName);
                Assert.Equal(w, v.Width);
                Assert.Equal(h, v.Height);
                Assert.InRange(probe.DurationTicks, 14_000_000, 16_500_000); // 1.5 s

                // decoded colours are the drawn ones, in order (a swapped or stale texture
                // would show a neighbouring frame's colour: 4 levels of red per frame)
                foreach (int n in new[] { 0, 7, 22, 44 })
                {
                    var px = CenterPixel(path, w, h, fps, n);
                    int expectedR = 60 + 4 * n, expectedG = 200 - 3 * n, expectedB = 80;
                    Assert.True(Math.Abs(px[2] - expectedR) <= 6 && Math.Abs(px[1] - expectedG) <= 6 && Math.Abs(px[0] - expectedB) <= 6,
                        $"frame {n}: decoded RGB ({px[2]},{px[1]},{px[0]}), expected ({expectedR},{expectedG},{expectedB})");
                }
            }
            finally
            {
                File.Delete(path);
            }
        }

        /// <summary>x264 has no GPU input: offered hardware frames, the writer says so, opens the
        /// system-memory path and refuses hardware submissions.</summary>
        [Fact]
        public void Writer_declines_hardware_frames_for_the_software_encoder()
        {
            Assert.SkipUnless(TestFFmpeg.Available, TestFFmpeg.SkipReason);
            const int w = 64, h = 64;
            using var rig = Require(w, h, 2);
            string path = TempMp4();
            var log = new List<string>();
            try
            {
                using var writer = new Mp4Writer(path, new Mp4WriterOptions
                {
                    Width = w,
                    Height = h,
                    FpsNum = 30,
                    Encoder = VideoEncoder.Software,
                    HardwareFrames = rig.Bridge.Frames,
                    DiagnosticLog = log.Add,
                });
                Assert.Null(writer.HardwareFrames);
                Assert.Contains(log, line => line.Contains("libx264 has no Direct3D 11 input; using system-memory frames", StringComparison.Ordinal));
                using var frame = RoundTrip(rig, 0, c => c.Clear(SKColors.Green));
                Assert.Throws<InvalidOperationException>(() => writer.SubmitVideoFrame(frame, 0));
                using var nv12 = new Nv12Frame(w, h);
                nv12.FillBlack();
                writer.SubmitVideoFrame(nv12, 0); // the system-memory path is live
                writer.Abandon();
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Clowd.VideoSDK.Ai;
using Clowd.VideoSDK.Composition;
using Clowd.VideoSDK.Media;
using Clowd.VideoSDK.Model;
using SkiaSharp;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    // Real-decode round trip: Mp4Writer encodes a fixture whose frames are distinguishable
    // (grayscale ramp), SequentialFrameSource decodes it back and the frame-at-time-t pull is
    // asserted against known frame instants. Skips when the FFmpeg natives are absent (same
    // resolver as EncoderTests).
    public class SequentialFrameSourceTests : IDisposable
    {
        private const int W = 64, H = 64, Fps = 30, Frames = 24;
        private const int GrayStep = 10; // frame n is solid gray n*10

        private static bool FFmpegAvailable => FFmpegLoader.TryInitialize(FindFFmpegDirectory);

        private static string FindFFmpegDirectory()
        {
            string probeFile = OperatingSystem.IsWindows() ? "avcodec-61.dll" : "libavcodec.so.61";
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                foreach (var cfg in new[] { "release", "debug" })
                {
                    var candidate = Path.Combine(dir.FullName, "obs-express-rs", "target", cfg);
                    if (File.Exists(Path.Combine(candidate, probeFile)))
                        return candidate;
                }
                dir = dir.Parent;
            }
            return null;
        }

        private static void RequireFFmpeg() =>
            Assert.SkipUnless(FFmpegAvailable,
                $"FFmpeg natives not found (set {FFmpegLoader.EnvVarName} or build obs-express-rs): {FFmpegLoader.FailureReason}");

        // ----------------------------------------------------------------------------- fixture

        private readonly List<string> _tempFiles = new List<string>();

        public void Dispose()
        {
            foreach (var f in _tempFiles)
            {
                try { File.Delete(f); }
                catch { /* best effort */ }
            }
        }

        /// <summary>Encodes a CFR mp4 whose frame n is solid gray n*GrayStep — bright enough
        /// steps to survive the BGRA→yuv420p→BGRA round trip unambiguously.</summary>
        private string EncodeFixture()
        {
            string path = Path.Combine(Path.GetTempPath(), $"clowd-seqsrc-test-{Guid.NewGuid():N}.mp4");
            _tempFiles.Add(path);

            using var writer = new Mp4Writer(path, new Mp4WriterOptions
            {
                Width = W,
                Height = H,
                FpsNum = Fps,
                FpsDen = 1,
            });

            var bgra = new byte[W * H * 4];
            var pin = GCHandle.Alloc(bgra, GCHandleType.Pinned);
            try
            {
                for (int n = 0; n < Frames; n++)
                {
                    byte v = (byte)(n * GrayStep);
                    for (int i = 0; i < bgra.Length; i += 4)
                    {
                        bgra[i] = v;
                        bgra[i + 1] = v;
                        bgra[i + 2] = v;
                        bgra[i + 3] = 0xFF;
                    }
                    writer.SubmitVideoFrame(pin.AddrOfPinnedObject(), W * 4, W, H, n);
                }
            }
            finally
            {
                pin.Free();
            }

            writer.Finish();
            return path;
        }

        private static Project ProjectFor(string path, out Guid sourceId)
        {
            sourceId = Guid.NewGuid();
            var p = new Project
            {
                Output = new OutputSettings { WidthPx = W, HeightPx = H, FpsNum = Fps, FpsDen = 1, SampleRate = 48000 },
            };
            p.Sources.Add(new Source
            {
                Id = sourceId,
                Path = path,
                Streams = { new SourceStream { Index = 0, Kind = StreamKind.Video, Width = W, Height = H, AvgFrameRateNum = Fps, AvgFrameRateDen = 1 } },
            });
            return p;
        }

        private static byte GrayOf(SKImage image)
        {
            var native = Marshal.AllocHGlobal(4);
            try
            {
                var info = new SKImageInfo(1, 1, SKColorType.Bgra8888, SKAlphaType.Premul);
                Assert.True(image.ReadPixels(info, native, 4, image.Width / 2, image.Height / 2));
                var px = new byte[4];
                Marshal.Copy(native, px, 0, 4);
                return px[1]; // G of the gray
            }
            finally
            {
                Marshal.FreeHGlobal(native);
            }
        }

        private static long FrameTicks(int n) => TimeBase.FrameIndexToTicks(n, Fps, 1);

        // ------------------------------------------------------------------------------- tests

        [Fact]
        public void Delivers_monotonic_frames_at_their_instants()
        {
            RequireFFmpeg();
            string path = EncodeFixture();
            var project = ProjectFor(path, out var sourceId);

            using var factory = new CpuSurfaceFactory();
            using var cache = new FrameTextureCache(factory);
            using var source = new SequentialFrameSource(project, cache);

            long lastPts = long.MinValue;
            for (int n = 0; n < Frames; n++)
            {
                Assert.True(source.TryGetFrame(sourceId, 0, FrameTicks(n), out var frame));
                Assert.NotNull(frame.Image);

                // pts is the frame's own instant (mp4 time base rescales to the same rounded tick)
                Assert.InRange(frame.PtsTicks, FrameTicks(n) - 10_000, FrameTicks(n) + 10_000);
                Assert.True(frame.PtsTicks >= lastPts, "frame delivery went backwards");
                lastPts = frame.PtsTicks;

                int expected = n * GrayStep;
                Assert.InRange(GrayOf(frame.Image), Math.Max(0, expected - 12), Math.Min(255, expected + 12));
            }

            // the cache holds exactly the one live stream entry
            Assert.Equal(1, cache.Count);
        }

        [Fact]
        public void Holds_frame_between_instants_and_past_eof()
        {
            RequireFFmpeg();
            string path = EncodeFixture();
            var project = ProjectFor(path, out var sourceId);

            using var factory = new CpuSurfaceFactory();
            using var cache = new FrameTextureCache(factory);
            using var source = new SequentialFrameSource(project, cache);

            // halfway between frame 3 and 4 → frame 3 holds
            long midTicks = (FrameTicks(3) + FrameTicks(4)) / 2;
            Assert.True(source.TryGetFrame(sourceId, 0, midTicks, out var frame));
            Assert.InRange(frame.PtsTicks, FrameTicks(3) - 10_000, FrameTicks(3) + 10_000);
            Assert.InRange(GrayOf(frame.Image), 3 * GrayStep - 12, 3 * GrayStep + 12);

            // far past the last frame → the last frame holds (no failure, no rewind)
            Assert.True(source.TryGetFrame(sourceId, 0, 10 * TimeBase.TicksPerSecond, out frame));
            long lastInstant = FrameTicks(Frames - 1);
            Assert.InRange(frame.PtsTicks, lastInstant - 10_000, lastInstant + 10_000);
            int lastGray = (Frames - 1) * GrayStep;
            Assert.InRange(GrayOf(frame.Image), lastGray - 12, lastGray + 12);

            // and holds again on a later request
            Assert.True(source.TryGetFrame(sourceId, 0, 20 * TimeBase.TicksPerSecond, out frame));
            Assert.InRange(frame.PtsTicks, lastInstant - 10_000, lastInstant + 10_000);
        }

        [Fact]
        public void Regressing_request_repositions_and_unknown_source_throws()
        {
            RequireFFmpeg();
            string path = EncodeFixture();
            var project = ProjectFor(path, out var sourceId);

            using var factory = new CpuSurfaceFactory();
            using var cache = new FrameTextureCache(factory);
            using var source = new SequentialFrameSource(project, cache);

            Assert.True(source.TryGetFrame(sourceId, 0, FrameTicks(5), out _));
            Assert.Equal(0, source.RepositionCount);

            // a timeline that reads this stream out of source order seeks back instead of failing
            Assert.True(source.TryGetFrame(sourceId, 0, FrameTicks(1), out var frame));
            Assert.Equal(1, source.RepositionCount);
            Assert.InRange(frame.PtsTicks, FrameTicks(1) - 10_000, FrameTicks(1) + 10_000);
            Assert.InRange(GrayOf(frame.Image), GrayStep - 12, GrayStep + 12);

            // forward again from there, no further seek
            Assert.True(source.TryGetFrame(sourceId, 0, FrameTicks(2), out frame));
            Assert.Equal(1, source.RepositionCount);
            Assert.InRange(GrayOf(frame.Image), 2 * GrayStep - 12, 2 * GrayStep + 12);

            Assert.Throws<ArgumentException>(() => source.TryGetFrame(Guid.NewGuid(), 0, 0, out _));
        }

        /// <summary>A matte sidecar written on the same PTS grid as its source, at a different
        /// resolution, with frame n solid gray n*GrayStep — distinguishable mattes, so the
        /// pairing (latest-PTS-at-or-before per requested instant) is directly observable.</summary>
        private string EncodeMatteSidecar(string cacheDir, Guid sourceId, string sourcePath, int size)
        {
            string mattePath = AiSidecars.MattePath(cacheDir, sourceId, 0);
            using (var writer = new Mp4Writer(mattePath, new Mp4WriterOptions
            {
                Width = size,
                Height = size,
                FpsNum = Fps,
                FpsDen = 1,
            }))
            {
                var bgra = new byte[size * size * 4];
                var pin = GCHandle.Alloc(bgra, GCHandleType.Pinned);
                try
                {
                    for (int n = 0; n < Frames; n++)
                    {
                        byte v = (byte)(n * GrayStep);
                        for (int i = 0; i < bgra.Length; i += 4)
                        {
                            bgra[i] = v;
                            bgra[i + 1] = v;
                            bgra[i + 2] = v;
                            bgra[i + 3] = 0xFF;
                        }
                        writer.SubmitVideoFrame(pin.AddrOfPinnedObject(), size * 4, size, size, n);
                    }
                }
                finally
                {
                    pin.Free();
                }
                writer.Finish();
            }

            Assert.True(AiSidecars.TryWriteCompanion(mattePath,
                AiSidecars.DescribeSource(sourcePath, size, size)));
            return mattePath;
        }

        /// <summary>Adds the segmented-effect item that makes the stream want a matte.</summary>
        private static void AddSegmentedItem(Project project, Guid sourceId)
        {
            var track = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Order = 0 };
            project.Tracks.Add(track);
            project.Items.Add(new Item
            {
                Id = Guid.NewGuid(),
                TrackId = track.Id,
                TimelineStartTicks = 0,
                DurationTicks = FrameTicks(Frames),
                Content = new MediaContent { SourceId = sourceId, StreamIndex = 0 },
                Effect = new VideoEffect { Kind = VideoEffectKind.BgRemove },
            });
        }

        [Fact]
        public void Attaches_masks_from_a_valid_matte_sidecar_paired_by_pts()
        {
            RequireFFmpeg();
            string cacheDir = Path.Combine(Path.GetTempPath(), $"clowd-seqsrc-matte-{Guid.NewGuid():N}");
            Directory.CreateDirectory(cacheDir);
            try
            {
                string path = EncodeFixture();
                var project = ProjectFor(path, out var sourceId);
                AddSegmentedItem(project, sourceId);
                EncodeMatteSidecar(cacheDir, sourceId, path, size: 32); // half the frame size

                using var factory = new CpuSurfaceFactory();
                using var cache = new FrameTextureCache(factory);
                using var source = new SequentialFrameSource(project, cache, null, cacheDir);

                // masks pair by PTS at the frames' own instants, at their own resolution
                Assert.True(source.TryGetFrame(sourceId, 0, FrameTicks(5), out var frame));
                Assert.NotNull(frame.Mask);
                Assert.Equal(32, frame.Mask.Width);
                Assert.InRange(GrayOf(frame.Mask), 5 * GrayStep - 12, 5 * GrayStep + 12);

                Assert.True(source.TryGetFrame(sourceId, 0, FrameTicks(6), out frame));
                Assert.InRange(GrayOf(frame.Mask), 6 * GrayStep - 12, 6 * GrayStep + 12);

                // a backwards request repositions the matte in step with the frames
                Assert.True(source.TryGetFrame(sourceId, 0, FrameTicks(2), out frame));
                Assert.InRange(GrayOf(frame.Mask), 2 * GrayStep - 12, 2 * GrayStep + 12);
                Assert.InRange(GrayOf(frame.Image), 2 * GrayStep - 12, 2 * GrayStep + 12);
            }
            finally
            {
                try { Directory.Delete(cacheDir, recursive: true); }
                catch { /* best effort */ }
            }
        }

        [Fact]
        public void A_sidecar_without_a_valid_companion_delivers_maskless_frames()
        {
            RequireFFmpeg();
            string cacheDir = Path.Combine(Path.GetTempPath(), $"clowd-seqsrc-matte-{Guid.NewGuid():N}");
            Directory.CreateDirectory(cacheDir);
            try
            {
                string path = EncodeFixture();
                var project = ProjectFor(path, out var sourceId);
                AddSegmentedItem(project, sourceId);
                string mattePath = EncodeMatteSidecar(cacheDir, sourceId, path, size: 32);
                File.Delete(AiSidecars.CompanionPath(mattePath)); // now stale by the validity rule

                using var factory = new CpuSurfaceFactory();
                using var cache = new FrameTextureCache(factory);
                using var source = new SequentialFrameSource(project, cache, null, cacheDir);

                Assert.True(source.TryGetFrame(sourceId, 0, FrameTicks(3), out var frame));
                Assert.NotNull(frame.Image);
                Assert.Null(frame.Mask);
            }
            finally
            {
                try { Directory.Delete(cacheDir, recursive: true); }
                catch { /* best effort */ }
            }
        }

        [Fact]
        public void Composes_encoded_media_through_frame_composer()
        {
            RequireFFmpeg();
            string path = EncodeFixture();
            var project = ProjectFor(path, out var sourceId);

            var track = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Order = 0 };
            project.Tracks.Add(track);
            project.Items.Add(new Item
            {
                Id = Guid.NewGuid(),
                TrackId = track.Id,
                TimelineStartTicks = 0,
                DurationTicks = FrameTicks(Frames),
                Content = new MediaContent { SourceId = sourceId, StreamIndex = 0, SourceInTicks = 0 },
            });

            using var factory = new CpuSurfaceFactory();
            using var cache = new FrameTextureCache(factory);
            using var source = new SequentialFrameSource(project, cache);
            using var surface = factory.CreateSurface(W, H);

            FrameComposer.Compose(project, FrameTicks(10), source, surface.Canvas, W, H);

            int rowBytes = W * 4;
            var native = Marshal.AllocHGlobal(rowBytes * H);
            try
            {
                Assert.True(factory.TryReadPixels(surface, W, H, native, rowBytes));
                var pixels = new byte[rowBytes * H];
                Marshal.Copy(native, pixels, 0, pixels.Length);

                int centre = (H / 2) * rowBytes + (W / 2) * 4;
                int expected = 10 * GrayStep;
                Assert.InRange(pixels[centre + 1], expected - 12, expected + 12); // G of the gray
            }
            finally
            {
                Marshal.FreeHGlobal(native);
            }
        }
    }
}

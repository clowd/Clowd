using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Clowd.VideoSDK.Composition;
using Clowd.VideoSDK.Editing;
using Clowd.VideoSDK.Media;
using Clowd.VideoSDK.Model;
using Clowd.VideoSDK.Playback;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    // An imported file can store its frame at one size and declare that its pixels are not
    // square (FFmpeg's sample_aspect_ratio — the classic case is a 720×720 file with 3:4 pixels
    // meant to be seen at 540×720). Every decoded buffer is still the stored size; what these
    // tests pin is that the shape math everywhere reads the DISPLAYED size instead: the probe
    // carries the ratio, the import keeps it on the stream and sizes a fresh project's canvas by
    // it, the mapping keeps sampling in stored pixels while boxing the picture at its displayed
    // shape, and a project written before the field existed reads as square pixels.
    public class PixelAspectTests : IDisposable
    {
        private const long Ms = TimeSpan.TicksPerMillisecond;

        private readonly string _dir = Path.Combine(Path.GetTempPath(), "clowd-par-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); }
            catch { /* best effort */ }
        }

        // ------------------------------------------------------------------------------- model

        [Fact]
        public void An_undeclared_pixel_aspect_is_square()
        {
            var stream = new SourceStream { Kind = StreamKind.Video, Width = 1920, Height = 1080 };

            Assert.Equal(1.0, stream.PixelAspect);
            Assert.Equal(1920, stream.DisplayWidth);
            Assert.Equal(1080, stream.DisplayHeight);

            // a half-declared ratio is no ratio
            stream.PixelAspectNum = 3;
            Assert.Equal(1.0, stream.PixelAspect);
        }

        [Fact]
        public void The_displayed_width_applies_the_pixel_aspect()
        {
            var stream = Anamorphic();

            Assert.Equal(0.75, stream.PixelAspect);
            Assert.Equal(540, stream.DisplayWidth);
            Assert.Equal(720, stream.DisplayHeight);
        }

        [Fact]
        public void The_pixel_aspect_round_trips_through_project_json_and_an_old_project_reads_square()
        {
            var project = new Project
            {
                Output = new OutputSettings { WidthPx = 540, HeightPx = 720, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
                Sources = { new Source { Id = Guid.NewGuid(), Path = TestPath.Native(@"C:\media\dv.mp4"), Streams = { Anamorphic() } } },
            };

            var json = project.ToJson();
            Assert.Contains("\"PixelAspectNum\": 3", json);
            var loaded = Project.FromJson(json).Sources[0].Streams[0];
            Assert.Equal(3, loaded.PixelAspectNum);
            Assert.Equal(4, loaded.PixelAspectDen);
            Assert.Equal(540, loaded.DisplayWidth);

            // the computed views are not persisted, and a file written before the field existed
            // carries nothing — which must mean square, not zero-width
            Assert.DoesNotContain("DisplayWidth", json);
            var legacy = Project.FromJson(Regex.Replace(json, @"\s*""PixelAspect(Num|Den)"": \d+,", ""));
            var stream = legacy.Sources[0].Streams[0];
            Assert.Equal(0, stream.PixelAspectNum);
            Assert.Equal(1.0, stream.PixelAspect);
            Assert.Equal(720, stream.DisplayWidth);
        }

        // ------------------------------------------------------------------------------ mapping

        /// <summary>The whole 720×720 buffer is sampled, and it lands in a 540×720 box — the
        /// horizontal squeeze that turns 3:4 pixels back into square ones.</summary>
        [Fact]
        public void The_mapping_samples_stored_pixels_into_a_display_shaped_box()
        {
            Assert.True(PictureMapping.TryMap(new Transform(), ItemEffects.Identity, 720, 720,
                540, 720, out var map, pixelAspect: 0.75));

            Assert.Equal(0, map.Source.Left);
            Assert.Equal(0, map.Source.Top);
            Assert.Equal(720, map.Source.Right);
            Assert.Equal(720, map.Source.Bottom);

            Assert.Equal(0, map.Dest.Left, 3);
            Assert.Equal(0, map.Dest.Top, 3);
            Assert.Equal(540, map.Dest.Right, 3);
            Assert.Equal(720, map.Dest.Bottom, 3);
            Assert.Equal(0.75, map.ScaleX, 6);
            Assert.Equal(1.0, map.ScaleY, 6);
        }

        /// <summary>A fill aspect preset trims the DISPLAYED shape to the ratio: 540×720 shown
        /// into a square loses 90 shown rows top and bottom — 90 stored rows, since the ratio only
        /// touches the horizontal axis — and keeps every stored column.</summary>
        [Fact]
        public void A_fill_aspect_trims_the_displayed_shape_not_the_stored_one()
        {
            var transform = new Transform { Aspect = 1.0 };

            Assert.True(PictureMapping.TryMap(transform, ItemEffects.Identity, 720, 720,
                720, 720, out var map, pixelAspect: 0.75));

            Assert.Equal(0, map.Source.Left, 3);
            Assert.Equal(90, map.Source.Top, 3);
            Assert.Equal(720, map.Source.Right, 3);
            Assert.Equal(630, map.Source.Bottom, 3);
            Assert.Equal(720, map.Dest.Width, 3);
            Assert.Equal(720, map.Dest.Height, 3);
        }

        [Fact]
        public void A_square_or_invalid_pixel_aspect_changes_nothing()
        {
            Assert.True(PictureMapping.TryMap(new Transform(), ItemEffects.Identity, 720, 720, 720, 720, out var plain));
            Assert.True(PictureMapping.TryMap(new Transform(), ItemEffects.Identity, 720, 720, 720, 720, out var one, pixelAspect: 1.0));
            Assert.True(PictureMapping.TryMap(new Transform(), ItemEffects.Identity, 720, 720, 720, 720, out var zero, pixelAspect: 0));
            Assert.True(PictureMapping.TryMap(new Transform(), ItemEffects.Identity, 720, 720, 720, 720, out var nan, pixelAspect: Double.NaN));

            foreach (var map in new[] { one, zero, nan })
            {
                Assert.Equal(plain.Source, map.Source);
                Assert.Equal(plain.Dest, map.Dest);
            }
        }

        // ------------------------------------------------------------------------------- import

        [Fact]
        public void Importing_into_an_empty_project_adopts_the_displayed_size_and_keeps_the_ratio()
        {
            var session = new EditorSession(new Project
            {
                Output = new OutputSettings { WidthPx = 1920, HeightPx = 1080, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
            }, null, null);

            var created = session.ImportMedia(TestPath.Native(@"C:\media\dv.mp4"), AnamorphicProbe(), 0);

            var item = Assert.Single(created);
            Assert.Equal(540, session.Project.Output.WidthPx);
            Assert.Equal(720, session.Project.Output.HeightPx);
            // the defining import fills the canvas it just set
            Assert.Equal(1.0, item.Transform.Scale);

            var stream = Assert.Single(session.Project.Sources).Streams.Single();
            Assert.Equal(720, stream.Width);
            Assert.Equal(3, stream.PixelAspectNum);
            Assert.Equal(4, stream.PixelAspectDen);
            Assert.Equal(0.75, FrameComposer.PixelAspectOf(session.Project, (MediaContent)item.Content));
            Assert.Equal((540, 720), EditorSession.GetNativeSize(session.Project));
        }

        [Fact]
        public void The_composer_reads_a_square_ratio_for_an_unknown_source()
        {
            var project = new Project();
            var media = new MediaContent { SourceId = Guid.NewGuid(), StreamIndex = 0 };

            Assert.Equal(1.0, FrameComposer.PixelAspectOf(project, media));
        }

        // -------------------------------------------------------------------------------- probe

        /// <summary>A yuv4mpeg header declares its sample aspect ratio in plain text, so the
        /// hand-written fixture (<see cref="TestY4m"/>) is the one file the suite can declare
        /// non-square pixels in without an encoder. (A GIF's aspect byte is ignored by FFmpeg.)</summary>
        [Fact]
        public void The_probe_reads_the_declared_pixel_aspect()
        {
            Assert.SkipUnless(TestFFmpeg.Available, TestFFmpeg.SkipReason);

            var probe = MediaProbe.ProbeDetailed(TestY4m.Write(Path.Combine(_dir, "par.y4m"), frames: 2, aspectNum: 3, aspectDen: 4));

            var video = Assert.Single(probe.VideoStreams);
            Assert.Equal(2, video.Width);
            Assert.Equal(2, video.Height);
            Assert.Equal(3, video.SampleAspectNum);
            Assert.Equal(4, video.SampleAspectDen);
        }

        [Fact]
        public void The_probe_reports_square_pixels_when_nothing_is_declared()
        {
            Assert.SkipUnless(TestFFmpeg.Available, TestFFmpeg.SkipReason);

            var probe = MediaProbe.ProbeDetailed(TestGif.Write(Path.Combine(_dir, "square.gif"), frames: 2));

            var video = Assert.Single(probe.VideoStreams);
            Assert.Equal(1, video.SampleAspectNum);
            Assert.Equal(1, video.SampleAspectDen);
        }

        // ------------------------------------------------------------------------------ compose

        /// <summary>End to end: a 2×2 file with 3:4 pixels, on the 3:4 canvas its import would
        /// define, fills that canvas edge to edge. Were the composer boxing the picture at its
        /// stored (square) shape, the box would be 30×30 centered on the 30×40 canvas and the top
        /// and bottom rows would stay unpainted.</summary>
        [Fact]
        public void The_composer_draws_an_anamorphic_frame_at_its_displayed_shape()
        {
            Assert.SkipUnless(TestFFmpeg.Available, TestFFmpeg.SkipReason);
            const int W = 30, H = 40;

            var path = TestY4m.Write(Path.Combine(_dir, "grey.y4m"), frames: 2, aspectNum: 3, aspectDen: 4);
            var track = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Video", Order = 0 };
            var sourceId = Guid.NewGuid();
            var project = new Project
            {
                Output = new OutputSettings { WidthPx = W, HeightPx = H, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
                Sources =
                {
                    new Source
                    {
                        Id = sourceId,
                        Path = path,
                        Streams = { new SourceStream { Index = 0, Kind = StreamKind.Video, Width = 2, Height = 2, PixelAspectNum = 3, PixelAspectDen = 4 } },
                    },
                },
                Tracks = { track },
                Items =
                {
                    new Item
                    {
                        Id = Guid.NewGuid(),
                        TrackId = track.Id,
                        TimelineStartTicks = 0,
                        DurationTicks = 1000 * Ms,
                        Content = new MediaContent { SourceId = sourceId, StreamIndex = 0 },
                        Transform = new Transform(),
                    },
                },
            };

            using var factory = new CpuSurfaceFactory();
            using var cache = new FrameTextureCache(factory);
            using var frames = new SequentialFrameSource(project, cache);
            using var surface = factory.CreateSurface(W, H);
            FrameComposer.Compose(project, 10 * Ms, frames, surface.Canvas, W, H);

            int rowBytes = W * 4;
            var native = System.Runtime.InteropServices.Marshal.AllocHGlobal(rowBytes * H);
            try
            {
                Assert.True(factory.TryReadPixels(surface, W, H, native, rowBytes));
                var px = new byte[rowBytes * H];
                System.Runtime.InteropServices.Marshal.Copy(native, px, 0, px.Length);
                // the fixture is mid grey; anything unpainted is the cleared canvas
                byte At(int x, int y) => px[y * rowBytes + x * 4 + 1]; // green channel
                Assert.InRange(At(W / 2, H / 2), 100, 160);
                Assert.InRange(At(1, 1), 100, 160);
                Assert.InRange(At(W - 2, 1), 100, 160);
                Assert.InRange(At(1, H - 2), 100, 160);
                Assert.InRange(At(W - 2, H - 2), 100, 160);
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(native);
            }
        }

        // ----------------------------------------------------------------------------- fixtures

        /// <summary>720×720 stored, 3:4 pixels — shown at 540×720.</summary>
        private static SourceStream Anamorphic() => new SourceStream
        {
            Index = 0,
            Kind = StreamKind.Video,
            Width = 720,
            Height = 720,
            PixelAspectNum = 3,
            PixelAspectDen = 4,
            AvgFrameRateNum = 30,
            AvgFrameRateDen = 1,
            DurationTicks = 8_000 * Ms,
        };

        private static MediaProbeResult AnamorphicProbe() => new MediaProbeResult
        {
            Path = TestPath.Native(@"C:\media\dv.mp4"),
            DurationTicks = 8_000 * Ms,
            VideoStreams = new[]
            {
                new VideoStreamProbe
                {
                    StreamIndex = 0, Width = 720, Height = 720, SampleAspectNum = 3, SampleAspectDen = 4,
                    AvgFrameRateNum = 30, AvgFrameRateDen = 1, DurationTicks = 8_000 * Ms,
                },
            },
        };
    }
}

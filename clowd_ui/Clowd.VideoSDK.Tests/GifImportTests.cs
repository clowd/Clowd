using System;
using System.IO;
using System.Runtime.InteropServices;
using Clowd.VideoSDK.Composition;
using Clowd.VideoSDK.Editing;
using Clowd.VideoSDK.Media;
using Clowd.VideoSDK.Model;
using Clowd.VideoSDK.Playback;
using SkiaSharp;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    // An animated GIF is imported as an ordinary media source — nothing in the model or the decode
    // path knows it is a GIF — so what these tests pin is that the claim is true end to end: the
    // probe reports a real video stream, ImportMedia lays it on a video row for its own duration,
    // and the render path's frame source hands back the frame covering a given time (frame 0 is
    // black, frame 1 white — see TestGif). The last one matters most: a GIF's only keyframe is
    // usually its first frame, so every seek restarts at the top and decodes forward, and a frame
    // read at 0.15s that came back black would mean the forward decode never happened.
    public class GifImportTests : IDisposable
    {
        private const long Second = TimeBase.TicksPerSecond;

        private readonly string _dir = Path.Combine(Path.GetTempPath(), "clowd-gif-import-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); }
            catch { /* best effort */ }
        }

        [Fact]
        public void AnimatedGif_probes_as_a_video_stream()
        {
            Assert.SkipUnless(TestFFmpeg.Available, TestFFmpeg.SkipReason);

            var probe = MediaProbe.ProbeDetailed(Gif("anim.gif", frames: 2));

            Assert.Empty(probe.AudioStreams);
            var video = Assert.Single(probe.VideoStreams);
            Assert.Equal("gif", video.CodecName);
            Assert.Equal(1, video.Width);
            Assert.Equal(1, video.Height);
            Assert.Equal(2, video.NbFrames);
            // two 10ms frames — the item an import creates is this long, not a default guess.
            Assert.Equal(Second / 5, probe.DurationTicks);
        }

        [Fact]
        public void ImportMedia_puts_an_animated_gif_on_a_video_track_for_its_own_duration()
        {
            Assert.SkipUnless(TestFFmpeg.Available, TestFFmpeg.SkipReason);

            var path = Gif("anim.gif", frames: 2);
            var probe = MediaProbe.ProbeDetailed(path);
            var session = new EditorSession(BlankProject(), null, null);

            var created = session.ImportMedia(path, probe, startTicks: 0);

            var item = Assert.Single(created);
            Assert.IsType<MediaContent>(item.Content);
            Assert.Equal(TrackKind.Video, session.Project.Tracks.Find(t => t.Id == item.TrackId).Kind);
            Assert.Equal(probe.DurationTicks, item.DurationTicks);
        }

        [Fact]
        public void FrameSource_decodes_each_gif_frame_at_its_own_time()
        {
            Assert.SkipUnless(TestFFmpeg.Available, TestFFmpeg.SkipReason);

            var path = Gif("anim.gif", frames: 2);
            var project = BlankProject();
            var sourceId = Guid.NewGuid();
            project.Sources.Add(new Source
            {
                Id = sourceId,
                Path = path,
                Streams = { new SourceStream { Index = 0, Kind = StreamKind.Video, Width = 1, Height = 1 } },
            });

            using var factory = new CpuSurfaceFactory();
            using var cache = new FrameTextureCache(factory);
            using var source = new SequentialFrameSource(project, cache);

            // frame 0 covers [0, 0.1), frame 1 covers [0.1, 0.2) — 10ms delays, as written.
            Assert.Equal(0, Luma(source, sourceId, Second / 20));       // 0.05s: black
            Assert.Equal(255, Luma(source, sourceId, 3 * Second / 20)); // 0.15s: white
        }

        // ----------------------------------------------------------------------------- helpers

        private string Gif(string name, int frames) => TestGif.Write(Path.Combine(_dir, name), frames);

        private static Project BlankProject() => new Project
        {
            Output = new OutputSettings { WidthPx = 64, HeightPx = 64, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
        };

        /// <summary>The blue channel of the 1x1 fixture's only pixel at <paramref name="ticks"/>
        /// — 0 for a black frame, 255 for a white one.</summary>
        private static byte Luma(SequentialFrameSource source, Guid sourceId, long ticks)
        {
            Assert.True(source.TryGetFrame(sourceId, 0, ticks, out var frame));

            var native = Marshal.AllocHGlobal(4);
            try
            {
                var info = new SKImageInfo(1, 1, SKColorType.Bgra8888, SKAlphaType.Premul);
                Assert.True(frame.Image.ReadPixels(info, native, 4, 0, 0));
                var px = new byte[4];
                Marshal.Copy(native, px, 0, 4);
                return px[0];
            }
            finally
            {
                Marshal.FreeHGlobal(native);
            }
        }
    }
}

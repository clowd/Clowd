using System;
using System.Linq;
using Clowd.VideoSDK.Editing;
using Clowd.VideoSDK.Model;
using Clowd.VideoSDK.Playback;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// Effect items and the content span: adds clamp into it (an effect item never extends the
    /// project, so one past the content end would be unreachable on the timeline), and the pinned
    /// speed row keeps the top of the Order space through an import.
    /// </summary>
    public class EffectClampTests
    {
        private static long Ms(long ms) => ms * TimeSpan.TicksPerMillisecond;

        /// <summary>One 20s solid on a video row plus an empty audio row.</summary>
        private static EditorSession NewSession(long contentMs = 20_000)
        {
            var videoTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Screen", Order = 0 };
            var audioTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Audio, Name = "Audio", Order = 1 };
            var project = new Project
            {
                Output = new OutputSettings { WidthPx = 1920, HeightPx = 1080, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
                Tracks = { videoTrack, audioTrack },
            };
            if (contentMs > 0)
                project.Items.Add(new Item
                {
                    Id = Guid.NewGuid(),
                    TrackId = videoTrack.Id,
                    TimelineStartTicks = 0,
                    DurationTicks = Ms(contentMs),
                    Content = new SolidContent { Color = "#FF000000" },
                });
            return new EditorSession(project, null, save => save());
        }

        private static MediaProbeResult ClipProbe() => new MediaProbeResult
        {
            Path = @"C:\media\clip.mp4",
            DurationTicks = Ms(8_000),
            VideoStreams = new[]
            {
                new VideoStreamProbe { StreamIndex = 0, Width = 1280, Height = 720, AvgFrameRateNum = 30, AvgFrameRateDen = 1, DurationTicks = Ms(8_000) },
            },
            AudioStreams = new[]
            {
                new AudioStreamProbe { StreamIndex = 1, SampleRate = 48_000, Channels = 2, DurationTicks = Ms(7_900) },
            },
            HasAudio = true,
        };

        [Fact]
        public void AddZoomEffect_at_the_content_end_backs_the_item_off_the_end()
        {
            var session = NewSession();

            var item = session.AddZoomEffect(session.DurationTicks, Ms(5_000));

            // no room in front of the playhead, so the item takes its full length behind it
            // rather than becoming a sliver at the end of the recording.
            Assert.NotNull(item);
            Assert.Equal(session.DurationTicks - Ms(5_000), item.TimelineStartTicks);
            Assert.Equal(session.DurationTicks, item.TimelineEndTicks);
        }

        [Fact]
        public void AddSpeedEffect_at_the_content_end_backs_the_item_off_the_end()
        {
            var session = NewSession();

            var item = session.AddSpeedEffect(session.DurationTicks, Ms(5_000));

            Assert.NotNull(item);
            Assert.Equal(session.DurationTicks - Ms(5_000), item.TimelineStartTicks);
            Assert.Equal(session.DurationTicks, item.TimelineEndTicks);
        }

        [Fact]
        public void Effect_adds_at_the_end_keep_at_least_the_insert_minimum()
        {
            var session = NewSession();

            // an add asking for less than the minimum still gets it — the point of the floor is
            // that a fresh clip can be grabbed, not that it is 5 seconds long.
            var item = session.AddSpeedEffect(session.DurationTicks, TimelineOps.MinSegmentTicks);

            Assert.NotNull(item);
            Assert.Equal(TimelineOps.MinInsertTicks, item.DurationTicks);
            Assert.Equal(session.DurationTicks, item.TimelineEndTicks);
        }

        [Fact]
        public void Effect_adds_take_a_content_span_shorter_than_the_insert_minimum_whole()
        {
            var session = NewSession(contentMs: 400);

            var item = session.AddSpeedEffect(session.DurationTicks, Ms(5_000));

            Assert.NotNull(item);
            Assert.Equal(0, item.TimelineStartTicks);
            Assert.Equal(session.DurationTicks, item.TimelineEndTicks);
        }

        [Fact]
        public void AddZoomEffect_duration_is_clamped_to_the_content_end()
        {
            var session = NewSession();

            var item = session.AddZoomEffect(Ms(18_000), Ms(5_000));

            Assert.NotNull(item);
            Assert.Equal(Ms(18_000), item.TimelineStartTicks);
            Assert.Equal(session.DurationTicks, item.TimelineEndTicks);
        }

        [Fact]
        public void Effect_adds_are_refused_on_an_empty_project()
        {
            var session = NewSession(contentMs: 0);

            Assert.Null(session.AddZoomEffect(0, Ms(5_000)));
            Assert.Null(session.AddSpeedEffect(0, Ms(5_000)));
        }

        [Fact]
        public void Imported_audio_rows_stay_below_the_speed_row()
        {
            var session = NewSession();
            var speed = session.AddSpeedEffect(Ms(1_000), Ms(3_000));
            Assert.NotNull(speed);

            var created = session.ImportMedia(@"C:\media\clip.mp4", ClipProbe(), 0);

            Assert.Equal(2, created.Count);
            Assert.Empty(session.Project.Validate());
            var speedTrack = session.Project.Tracks.Single(t => t.Id == speed.TrackId);
            Assert.Equal(session.Project.Tracks.Max(t => t.Order), speedTrack.Order);
        }
    }
}

using System;
using System.Collections.Generic;
using Clowd.VideoSDK.Model;
using Clowd.VideoSDK.Playback;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    // The timeline↔source mapping CompositionPlayer plays a project through (internal class,
    // reached via InternalsVisibleTo). Pure math — no FFmpeg, no timing.
    public class ProjectTimelineMapTests
    {
        private const long Second = 10_000_000;

        private static readonly Guid SourceId = Guid.NewGuid();

        private static Project NewProject()
        {
            var project = new Project
            {
                Output = new OutputSettings { WidthPx = 64, HeightPx = 64, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
            };
            project.Sources.Add(new Source
            {
                Id = SourceId,
                Path = "fixture.mp4",
                Streams =
                {
                    new SourceStream { Index = 0, Kind = StreamKind.Video },
                    new SourceStream { Index = 1, Kind = StreamKind.Audio },
                },
            });
            return project;
        }

        private static Track AddTrack(Project project, TrackKind kind, int order = 0,
            bool hidden = false, bool muted = false)
        {
            var track = new Track { Id = Guid.NewGuid(), Kind = kind, Order = order, Hidden = hidden, Muted = muted };
            project.Tracks.Add(track);
            return track;
        }

        private static Item AddMedia(Project project, Track track, long tlStart, long duration,
            long srcIn, int streamIndex = 0, double volume = 1.0)
        {
            var item = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = track.Id,
                TimelineStartTicks = tlStart,
                DurationTicks = duration,
                Content = new MediaContent { SourceId = SourceId, StreamIndex = streamIndex, SourceInTicks = srcIn },
                Volume = volume,
            };
            project.Items.Add(item);
            return item;
        }

        [Fact]
        public void Back_to_back_items_with_cut_map_both_directions()
        {
            // A: timeline [0, 0.5s) ← source [0, 0.5s); B: timeline [0.5s, 1.5s) ← source [1s, 2s)
            var project = NewProject();
            var track = AddTrack(project, TrackKind.Video);
            AddMedia(project, track, 0, Second / 2, 0);
            AddMedia(project, track, Second / 2, Second, Second);

            var map = ProjectTimelineMap.Build(project);
            var stream = map.VideoStreams[(SourceId, 0)];

            // timeline → source, mirroring FrameComposer's per-item mapping
            Assert.Equal(Second / 4, stream.TimelineToSource(Second / 4));
            Assert.Equal(Second + Second / 4, stream.TimelineToSource(Second / 2 + Second / 4));
            Assert.Equal(Second, stream.TimelineToSource(Second / 2)); // seam → B's in-point
            // past the end clamps to the last KEPT source instant — SrcEnd itself is the
            // exclusive out-point (the first instant the edit removed) and seeking there would
            // present a trimmed-away frame.
            Assert.Equal(2 * Second - 1, stream.TimelineToSource(5 * Second));

            // source → timeline (the worker pacing map)
            Assert.Equal(Second / 4, stream.SourceToTimeline(Second / 4));
            Assert.Equal(Second / 2 + Second / 4, stream.SourceToTimeline(Second + Second / 4));
            // a source instant inside the cut clamps to the seam
            Assert.Equal(Second / 2, stream.SourceToTimeline(Second / 2 + Second / 4));

            // offsets: 0 inside A, +0.5s inside B (that change is the seam detector)
            Assert.Equal(0, stream.OffsetAtTimeline(Second / 4));
            Assert.Equal(Second / 2, stream.OffsetAtTimeline(Second));
            Assert.Equal(long.MinValue, stream.OffsetAtTimeline(2 * Second)); // outside

            // the cut-out source span [0.5s, 1s) is a skip range
            var range = Assert.Single(stream.SourceCuts.Ranges);
            Assert.Equal(new TimeSpan(Second / 2), range.Start);
            Assert.Equal(new TimeSpan(Second), range.End);

            Assert.Equal((SourceId, 0), map.PrimaryVideo);
            Assert.Equal(Second / 2 + Second, map.DurationTicks);
        }

        [Fact]
        public void Split_without_cut_produces_no_skip_range_and_constant_offset()
        {
            var project = NewProject();
            var track = AddTrack(project, TrackKind.Video);
            AddMedia(project, track, 0, Second, 0);
            AddMedia(project, track, Second, Second, Second); // source-continuous

            var map = ProjectTimelineMap.Build(project);
            var stream = map.VideoStreams[(SourceId, 0)];

            Assert.Empty(stream.SourceCuts.Ranges);
            Assert.Equal(stream.OffsetAtTimeline(Second / 2), stream.OffsetAtTimeline(Second + Second / 2));
        }

        [Fact]
        public void Timeline_gap_maps_to_next_in_point_and_reports_no_offset()
        {
            var project = NewProject();
            var track = AddTrack(project, TrackKind.Video);
            AddMedia(project, track, 0, Second, 0);
            AddMedia(project, track, 2 * Second, Second, 3 * Second);

            var map = ProjectTimelineMap.Build(project);
            var stream = map.VideoStreams[(SourceId, 0)];

            Assert.Equal(3 * Second, stream.TimelineToSource(Second + Second / 2)); // inside the gap
            Assert.Equal(long.MinValue, stream.OffsetAtTimeline(Second + Second / 2));
        }

        [Fact]
        public void Hidden_video_and_muted_audio_tracks_are_excluded()
        {
            var project = NewProject();
            var hidden = AddTrack(project, TrackKind.Video, order: 0, hidden: true);
            AddMedia(project, hidden, 0, Second, 0);
            var muted = AddTrack(project, TrackKind.Audio, order: 1, muted: true);
            AddMedia(project, muted, 0, Second, 0, streamIndex: 1);

            var map = ProjectTimelineMap.Build(project);
            Assert.Empty(map.VideoStreams);
            Assert.Null(map.PrimaryVideo);
            Assert.Null(map.AudioStream);
            Assert.Null(map.AudioMap);
        }

        [Fact]
        public void First_audible_audio_stream_is_selected_with_item_volume()
        {
            var project = NewProject();
            var video = AddTrack(project, TrackKind.Video, order: 0);
            AddMedia(project, video, 0, 2 * Second, 0);
            var audio = AddTrack(project, TrackKind.Audio, order: 1);
            AddMedia(project, audio, 0, Second, 0, streamIndex: 1, volume: 0.25);
            AddMedia(project, audio, Second, Second, Second, streamIndex: 1, volume: 0.75);

            var map = ProjectTimelineMap.Build(project);
            Assert.Equal((SourceId, 1), map.AudioStream);
            Assert.Equal(0.25, map.AudioMap.VolumeAtTimeline(Second / 2));
            Assert.Equal(0.75, map.AudioMap.VolumeAtTimeline(Second + Second / 2));
            Assert.Equal(1.0, map.AudioMap.VolumeAtTimeline(3 * Second)); // outside → unity
        }
    }
}

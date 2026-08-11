using System;
using System.Collections.Generic;
using System.Linq;
using Clowd.VideoSDK.Model;
using Clowd.VideoSDK.Playback;
using Clowd.VideoSDK.Render;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// <see cref="RecordingProject.Build"/> — the one mapping from "a recording plus a keep-segment
    /// list" onto a project — exercised over the audio-row cases: none, the single row v1 knew, and
    /// the several rows a multi-track recording produces. Pure model math, so no FFmpeg and no
    /// filesystem are involved.
    /// </summary>
    public class RecordingProjectTests
    {
        private const long Second = TimeSpan.TicksPerSecond;
        private const int ScreenW = 1920, ScreenH = 1080;

        // ----------------------------------------------------------------------------- fixtures

        private static VideoStreamProbe Screen(long durationTicks = 30 * Second) => new VideoStreamProbe
        {
            StreamIndex = 0,
            Width = ScreenW,
            Height = ScreenH,
            AvgFrameRateNum = 30,
            AvgFrameRateDen = 1,
            RFrameRateNum = 30,
            RFrameRateDen = 1,
            DurationTicks = durationTicks,
        };

        private static AudioStreamProbe Audio(int streamIndex, int sampleRate = 48_000,
            long durationTicks = 30 * Second) => new AudioStreamProbe
        {
            StreamIndex = streamIndex,
            SampleRate = sampleRate,
            Channels = 2,
            DurationTicks = durationTicks,
        };

        private static RecordingProjectSpec Spec(IReadOnlyList<AudioStreamProbe> audio = null,
            IReadOnlyList<KeepSegment> segments = null, RecordingIds ids = null) => new RecordingProjectSpec
        {
            InputPath = @"C:\rec\in.mp4",
            Screen = Screen(),
            AudioStreams = audio ?? Array.Empty<AudioStreamProbe>(),
            FpsNum = 30,
            FpsDen = 1,
            Segments = segments ?? new[] { new KeepSegment(0, 30 * Second) },
            Ids = ids,
        };

        private static IReadOnlyList<Item> ItemsOn(Project project, string trackName)
        {
            var track = project.Tracks.Single(t => t.Name == trackName);
            return project.Items.Where(i => i.TrackId == track.Id)
                                .OrderBy(i => i.TimelineStartTicks)
                                .ToList();
        }

        private static IReadOnlyList<Track> AudioTracks(Project project) =>
            project.Tracks.Where(t => t.Kind == TrackKind.Audio).OrderBy(t => t.Order).ToList();

        private static int StreamIndexOf(Item item) => ((MediaContent)item.Content).StreamIndex;

        private static long SourceInOf(Item item) => ((MediaContent)item.Content).SourceInTicks;

        // -------------------------------------------------------------------------- no audio

        [Fact]
        public void A_recording_without_audio_has_no_audio_row()
        {
            var project = RecordingProject.Build(Spec());

            Assert.Empty(project.Validate());
            Assert.Empty(AudioTracks(project));
            Assert.Equal(RecordingProject.FallbackSampleRate, project.Output.SampleRate);
            Assert.Equal(new[] { 0 }, project.Sources[0].Streams.Select(s => s.Index).ToArray());
        }

        [Fact]
        public void A_null_audio_stream_list_is_the_same_as_none()
        {
            var spec = Spec();
            spec.AudioStreams = null;

            var project = RecordingProject.Build(spec);

            Assert.Empty(project.Validate());
            Assert.Empty(AudioTracks(project));
        }

        // ------------------------------------------------------------------------ single audio

        /// <summary>The v1 shape, unchanged: a lone stream is the row called "Audio" at order 2, and
        /// the output runs at its rate.</summary>
        [Fact]
        public void A_single_audio_stream_is_one_row_called_Audio()
        {
            var project = RecordingProject.Build(Spec(new[] { Audio(2, sampleRate: 44_100) }));

            Assert.Empty(project.Validate());
            var track = Assert.Single(AudioTracks(project));
            Assert.Equal("Audio", track.Name);
            Assert.Equal(2, track.Order);
            Assert.Equal(44_100, project.Output.SampleRate);

            var item = Assert.Single(ItemsOn(project, "Audio"));
            Assert.Equal(0, item.TimelineStartTicks);
            Assert.Equal(30 * Second, item.DurationTicks);
            Assert.Equal(2, StreamIndexOf(item));
        }

        // ------------------------------------------------------------------------- multi audio

        [Fact]
        public void Two_audio_streams_become_two_numbered_rows_below_the_video_rows()
        {
            var segments = new[] { new KeepSegment(0, 10 * Second), new KeepSegment(20 * Second, 5 * Second) };
            var project = RecordingProject.Build(Spec(new[] { Audio(2), Audio(3) }, segments));

            Assert.Empty(project.Validate());

            var tracks = AudioTracks(project);
            Assert.Equal(new[] { "Audio 1", "Audio 2" }, tracks.Select(t => t.Name).ToArray());
            Assert.Equal(new[] { 2, 3 }, tracks.Select(t => t.Order).ToArray());
            Assert.Equal(new[] { 0, 2, 3 }, project.Sources[0].Streams.Select(s => s.Index).ToArray());

            // one item per keep segment on each row, placed back to back, each referencing its own
            // stream and carrying its own source in-point
            foreach (var (name, streamIndex) in new[] { ("Audio 1", 2), ("Audio 2", 3) })
            {
                var items = ItemsOn(project, name);
                Assert.Equal(new[] { 0L, 10 * Second }, items.Select(i => i.TimelineStartTicks).ToArray());
                Assert.Equal(new[] { 10 * Second, 5 * Second }, items.Select(i => i.DurationTicks).ToArray());
                Assert.Equal(new[] { 0L, 20 * Second }, items.Select(SourceInOf).ToArray());
                Assert.All(items, i => Assert.Equal(streamIndex, StreamIndexOf(i)));
            }

            // one recording is one link group, however many rows it made
            Assert.Single(project.Items.Select(i => i.LinkGroupId).Distinct());
        }

        [Fact]
        public void The_output_sample_rate_is_the_highest_of_the_streams()
        {
            Assert.Equal(48_000, RecordingProject.Build(
                Spec(new[] { Audio(2, sampleRate: 44_100), Audio(3, sampleRate: 48_000) })).Output.SampleRate);

            // …whichever stream carries it
            Assert.Equal(48_000, RecordingProject.Build(
                Spec(new[] { Audio(2, sampleRate: 48_000), Audio(3, sampleRate: 44_100) })).Output.SampleRate);
        }

        [Fact]
        public void Supplied_track_names_label_the_rows_and_blanks_fall_back()
        {
            var spec = Spec(new[] { Audio(2), Audio(3), Audio(4) });
            spec.AudioTrackNames = new[] { "Microphone", "  " };

            var project = RecordingProject.Build(spec);

            // a blank label, and a list shorter than the stream list, both take the numbered default
            Assert.Equal(new[] { "Microphone", "Audio 2", "Audio 3" },
                AudioTracks(project).Select(t => t.Name).ToArray());
        }

        /// <summary>Each stream is clamped to its own end: the mic of a real recording stops a few
        /// hundredths of a second before the system mix, and neither row may claim material that is
        /// not in the file.</summary>
        [Fact]
        public void Each_row_is_clamped_to_its_own_stream_duration()
        {
            long shortDuration = 29 * Second + Second / 2;
            var project = RecordingProject.Build(
                Spec(new[] { Audio(2, durationTicks: shortDuration), Audio(3) }));

            Assert.Equal(shortDuration, Assert.Single(ItemsOn(project, "Audio 1")).DurationTicks);
            Assert.Equal(30 * Second, Assert.Single(ItemsOn(project, "Audio 2")).DurationTicks);
        }

        [Fact]
        public void A_segment_past_a_streams_end_leaves_that_row_empty_but_present()
        {
            var segments = new[] { new KeepSegment(20 * Second, 5 * Second) };
            var project = RecordingProject.Build(
                Spec(new[] { Audio(2, durationTicks: 5 * Second), Audio(3) }, segments));

            Assert.Empty(project.Validate());
            Assert.Empty(ItemsOn(project, "Audio 1"));
            Assert.Single(ItemsOn(project, "Audio 2"));
        }

        // ------------------------------------------------------------------------------- ids

        [Fact]
        public void Minted_ids_carry_one_distinct_id_per_audio_stream()
        {
            var ids = RecordingIds.New(2);

            Assert.Equal(2, ids.AudioTrackIds.Count);
            Assert.Equal(2, ids.AudioTrackIds.Distinct().Count());
            Assert.DoesNotContain(Guid.Empty, ids.AudioTrackIds);
            Assert.Empty(RecordingIds.New(0).AudioTrackIds);
            Assert.Throws<ArgumentOutOfRangeException>(() => { RecordingIds.New(-1); });
        }

        /// <summary>The editor rebuilds the project on every edit and hands back the same ids each
        /// time; if the audio rows' identities moved, CompositionPlayer would tear its decoders down
        /// on every pointer move.</summary>
        [Fact]
        public void Rebuilding_with_the_same_ids_keeps_every_row_identity()
        {
            var ids = RecordingIds.New(2);
            var streams = new[] { Audio(2), Audio(3) };

            var before = RecordingProject.Build(Spec(streams, ids: ids));
            var after = RecordingProject.Build(Spec(streams,
                new[] { new KeepSegment(0, 4 * Second), new KeepSegment(6 * Second, 9 * Second) }, ids));

            Assert.Equal(before.Sources[0].Id, after.Sources[0].Id);
            Assert.Equal(before.Tracks.Select(t => t.Id).ToArray(), after.Tracks.Select(t => t.Id).ToArray());
            Assert.Equal(ids.AudioTrackIds.ToArray(), AudioTracks(after).Select(t => t.Id).ToArray());
            Assert.Single(after.Items.Select(i => i.LinkGroupId).Distinct());
        }

        // -------------------------------------------------------------------------- v1 shim

        /// <summary>v1 render-args know exactly one audio row, so a v1 file must keep producing one
        /// — the first stream, at its rate — however many the input turns out to have.</summary>
        [Fact]
        public void The_v1_shim_still_maps_a_multi_audio_input_onto_a_single_row()
        {
            var probe = new MediaProbeResult
            {
                Path = "C:/in.mp4",
                DurationTicks = 30 * Second,
                VideoStreams = new[] { Screen() },
                AudioStreams = new[] { Audio(1, sampleRate: 44_100), Audio(2) },
                HasAudio = true,
            };

            var plan = RenderArgsCompat.Build(
                RenderArgsCompat.Parse("{\"version\":1,\"input\":\"C:/in.mp4\",\"output\":\"C:/out.mp4\"}"),
                probe);

            var track = Assert.Single(AudioTracks(plan.Project));
            Assert.Equal("Audio", track.Name);
            Assert.Equal(44_100, plan.Project.Output.SampleRate);
            Assert.Equal(new[] { 0, 1 }, plan.Project.Sources[0].Streams.Select(s => s.Index).ToArray());
        }
    }
}

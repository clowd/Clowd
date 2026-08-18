using System;
using System.Collections.Generic;
using System.Linq;
using Clowd.VideoSDK.Editing;
using Clowd.VideoSDK.Model;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The AI item effect (<see cref="VideoEffect"/>: blur / background blur / background remove)
    /// and the audio rows' denoise flags (<see cref="Track.Denoise"/>/
    /// <see cref="Track.DenoiseStrength"/>): the model's own rules and wire format, and the
    /// session mutators that write them. The effect mirrors <see cref="Surround"/>'s shape on
    /// purpose — same null-is-none rule, same validation style — so this suite mirrors
    /// <see cref="SurroundTests"/>' model half. The sidecar-consuming audio path lives in
    /// <see cref="DenoisedAudioSourceTests"/>.
    /// </summary>
    public class VideoEffectTests
    {
        private static long Ms(long ms) => ms * TimeSpan.TicksPerMillisecond;

        /// <summary>The recording shape: a screen item and an audio item over one source, the
        /// minimum that exercises both the item effect and the track denoise flags.</summary>
        private static Project RecordingProject(out Item video, out Item audio,
            out Track videoTrack, out Track audioTrack)
        {
            var sourceId = Guid.NewGuid();
            videoTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Screen", Order = 0 };
            audioTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Audio, Name = "Audio", Order = 1 };

            video = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = videoTrack.Id,
                TimelineStartTicks = 0,
                DurationTicks = Ms(10_000),
                Content = new MediaContent { SourceId = sourceId, StreamIndex = 0 },
            };
            audio = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = audioTrack.Id,
                TimelineStartTicks = 0,
                DurationTicks = Ms(10_000),
                Content = new MediaContent { SourceId = sourceId, StreamIndex = 1 },
            };

            return new Project
            {
                Output = new OutputSettings { WidthPx = 1920, HeightPx = 1080, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
                Sources =
                {
                    new Source
                    {
                        Id = sourceId,
                        Path = @"C:\rec\input.mp4",
                        Streams =
                        {
                            new SourceStream { Index = 0, Kind = StreamKind.Video, Width = 1920, Height = 1080, AvgFrameRateNum = 30, AvgFrameRateDen = 1, DurationTicks = Ms(60_000) },
                            new SourceStream { Index = 1, Kind = StreamKind.Audio, DurationTicks = Ms(60_000) },
                        },
                    },
                },
                Tracks = { videoTrack, audioTrack },
                Items = { video, audio },
            };
        }

        private static EditorSession NewSession(out Item video, out Item audio,
            out Track videoTrack, out Track audioTrack) =>
            new EditorSession(RecordingProject(out video, out audio, out videoTrack, out audioTrack), null, null);

        // ----------------------------------------------------------------------------- the model

        [Fact]
        public void No_effect_is_a_null_effect_and_a_stored_None_is_a_validation_error()
        {
            var session = NewSession(out var video, out _, out _, out _);
            Assert.Null(video.Effect);
            Assert.Null(VideoEffect.Create(VideoEffectKind.None));
            Assert.Empty(session.Project.Validate());

            video.Effect = new VideoEffect { Kind = VideoEffectKind.None };
            Assert.Contains("kind None", String.Join("\n", session.Project.Validate()));
        }

        [Theory]
        [InlineData(-0.1)]
        [InlineData(1.1)]
        [InlineData(Double.NaN)]
        public void Amounts_outside_the_models_range_are_rejected(double amount)
        {
            var session = NewSession(out var video, out _, out _, out _);
            video.Effect = new VideoEffect { Kind = VideoEffectKind.Blur, Amount = amount };

            Assert.NotEmpty(session.Project.Validate());
        }

        [Theory]
        [InlineData(-0.1)]
        [InlineData(1.1)]
        [InlineData(Double.NaN)]
        public void Denoise_strength_outside_the_models_range_is_rejected(double strength)
        {
            var session = NewSession(out _, out _, out _, out var audioTrack);
            audioTrack.DenoiseStrength = strength;

            Assert.NotEmpty(session.Project.Validate());
        }

        [Fact]
        public void Create_seeds_the_default_amount_and_the_matte_need_follows_the_kind()
        {
            var blur = VideoEffect.Create(VideoEffectKind.Blur);
            Assert.Equal(VideoEffectKind.Blur, blur.Kind);
            Assert.Equal(VideoEffect.DefaultAmount, blur.Amount);

            Assert.False(VideoEffect.NeedsMatte(VideoEffectKind.None));
            Assert.False(VideoEffect.NeedsMatte(VideoEffectKind.Blur));
            Assert.True(VideoEffect.NeedsMatte(VideoEffectKind.BgBlur));
            Assert.True(VideoEffect.NeedsMatte(VideoEffectKind.BgRemove));
        }

        [Fact]
        public void Clone_carries_every_field_and_shares_nothing()
        {
            var effect = new VideoEffect { Kind = VideoEffectKind.BgBlur, Amount = 0.35 };
            var clone = effect.Clone();

            Assert.Equal(effect.Kind, clone.Kind);
            Assert.Equal(effect.Amount, clone.Amount);

            clone.Amount = 0.9;
            Assert.Equal(0.35, effect.Amount);
        }

        [Fact]
        public void Split_carries_the_effect_onto_both_halves()
        {
            var session = NewSession(out var video, out _, out _, out _);
            session.SetItemEffect(video.Id, new VideoEffect { Kind = VideoEffectKind.BgBlur, Amount = 0.4 });

            Assert.True(session.SplitAt(video.Id, Ms(4_000)));

            var halves = session.Project.Items
                .Where(i => i.TrackId == video.TrackId)
                .ToList();
            Assert.Equal(2, halves.Count);
            Assert.All(halves, i => Assert.Equal(VideoEffectKind.BgBlur, i.Effect.Kind));
            Assert.All(halves, i => Assert.Equal(0.4, i.Effect.Amount));
            // clones, not the one object: an edit to either half must not reach the other
            Assert.NotSame(halves[0].Effect, halves[1].Effect);
        }

        [Fact]
        public void Duplicate_track_deep_copies_the_effect_and_the_denoise_settings()
        {
            var session = NewSession(out var video, out _, out _, out var audioTrack);
            session.SetItemEffect(video.Id, new VideoEffect { Kind = VideoEffectKind.BgRemove });
            session.SetTrackDenoise(audioTrack.Id, true);
            session.SetTrackDenoiseStrength(audioTrack.Id, 0.7);

            Assert.True(session.DuplicateTrack(video.TrackId));
            Assert.True(session.DuplicateTrack(audioTrack.Id));

            var project = session.Project;
            var videoCopy = project.Tracks.Single(t => t.Kind == TrackKind.Video && t.Name == "Screen copy");
            var audioCopy = project.Tracks.Single(t => t.Kind == TrackKind.Audio && t.Name == "Audio copy");

            Assert.True(audioCopy.Denoise);
            Assert.Equal(0.7, audioCopy.DenoiseStrength);

            var originalTrack = project.Tracks.Single(t => t.Name == "Screen");
            var original = project.Items.Single(i => i.TrackId == originalTrack.Id);
            var copied = project.Items.Single(i => i.TrackId == videoCopy.Id);
            Assert.Equal(VideoEffectKind.BgRemove, copied.Effect.Kind);
            Assert.NotSame(original.Effect, copied.Effect);
        }

        // ------------------------------------------------------------------------- the wire format

        [Fact]
        public void Effect_and_denoise_round_trip_byte_identical_with_pinned_keys()
        {
            var project = RecordingProject(out var video, out _, out _, out var audioTrack);
            video.Effect = new VideoEffect { Kind = VideoEffectKind.BgBlur, Amount = 0.35 };
            audioTrack.Denoise = true;
            audioTrack.DenoiseStrength = 0.7;
            project.Normalize();
            Assert.Empty(project.Validate());

            var json = project.ToJson();

            // these strings are the file format — a rename here breaks every saved project.
            Assert.Contains("\"Effect\":", json);
            Assert.Contains("\"Kind\": \"BgBlur\"", json);
            Assert.Contains("\"Amount\": 0.35", json);
            Assert.Contains("\"Denoise\": true", json);
            Assert.Contains("\"DenoiseStrength\": 0.7", json);

            var restored = Project.FromJson(json);
            Assert.Equal(json, restored.ToJson());
            var restoredVideo = restored.Items.Single(i => i.Id == video.Id);
            Assert.Equal(VideoEffectKind.BgBlur, restoredVideo.Effect.Kind);
            Assert.Equal(0.35, restoredVideo.Effect.Amount);
            var restoredTrack = restored.Tracks.Single(t => t.Id == audioTrack.Id);
            Assert.True(restoredTrack.Denoise);
            Assert.Equal(0.7, restoredTrack.DenoiseStrength);
        }

        [Fact]
        public void A_null_effect_is_omitted_from_the_json()
        {
            var project = RecordingProject(out var video, out _, out _, out _);
            video.Effect = new VideoEffect { Kind = VideoEffectKind.Blur };
            var json = project.ToJson();

            // two items, one effect: the audio item's null slot writes no key at all
            Assert.Equal(1, CountOf(json, "\"Effect\":"));
        }

        private static int CountOf(string haystack, string needle)
        {
            var count = 0;
            for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
                 i = haystack.IndexOf(needle, i + 1, StringComparison.Ordinal))
                count++;
            return count;
        }

        // ----------------------------------------------------------------------- session mutators

        [Fact]
        public void Denoise_toggle_is_structural_and_the_strength_dial_is_a_mapping_change()
        {
            var session = NewSession(out _, out _, out _, out var audioTrack);
            var kinds = new List<ProjectChangeKind>();
            session.ProjectChanged += (_, e) => kinds.Add(e.Kind);

            session.SetTrackDenoise(audioTrack.Id, true);
            session.SetTrackDenoiseStrength(audioTrack.Id, 0.6);

            Assert.True(audioTrack.Denoise);
            Assert.Equal(0.6, audioTrack.DenoiseStrength);
            Assert.Equal(new[] { ProjectChangeKind.Structural, ProjectChangeKind.Mapping }, kinds);
        }

        [Theory]
        [InlineData(1.4, 1.0)]
        [InlineData(-0.2, 0.0)]
        [InlineData(Double.NaN, 1.0)]
        public void The_strength_dial_clamps_in_the_mutator_and_returns_what_it_stored(
            double requested, double stored)
        {
            var session = NewSession(out _, out _, out _, out var audioTrack);

            Assert.Equal(stored, session.SetTrackDenoiseStrength(audioTrack.Id, requested));
            Assert.Equal(stored, audioTrack.DenoiseStrength);
            Assert.Empty(session.Project.Validate());
        }

        [Fact]
        public void Strength_drags_coalesce_into_one_undo_per_track()
        {
            var session = NewSession(out _, out _, out _, out var audioTrack);
            var before = session.Project.ToJson();

            session.SetTrackDenoiseStrength(audioTrack.Id, 0.8);
            session.SetTrackDenoiseStrength(audioTrack.Id, 0.5);
            Assert.Equal(0.5, session.Project.Tracks.Single(t => t.Id == audioTrack.Id).DenoiseStrength);

            session.Undo();
            Assert.Equal(before, session.Project.ToJson());
            Assert.False(session.CanUndo);
        }

        [Fact]
        public void The_effect_write_is_structural_exactly_when_the_matte_need_changes()
        {
            var session = NewSession(out var video, out _, out _, out _);
            var kinds = new List<ProjectChangeKind>();
            session.ProjectChanged += (_, e) => kinds.Add(e.Kind);

            session.SetItemEffect(video.Id, new VideoEffect { Kind = VideoEffectKind.Blur });          // none -> blur: no matte either side
            session.SetItemEffect(video.Id, new VideoEffect { Kind = VideoEffectKind.BgBlur });        // matte appears
            session.SetItemEffect(video.Id, new VideoEffect { Kind = VideoEffectKind.BgBlur, Amount = 0.8 }); // dial only
            session.SetItemEffect(video.Id, new VideoEffect { Kind = VideoEffectKind.BgRemove });      // matte kept
            session.SetItemEffect(video.Id, null);                                                     // matte disappears

            Assert.Null(session.Project.Items.Single(i => i.Id == video.Id).Effect);
            Assert.Equal(new[]
            {
                ProjectChangeKind.Mapping,
                ProjectChangeKind.Structural,
                ProjectChangeKind.Mapping,
                ProjectChangeKind.Mapping,
                ProjectChangeKind.Structural,
            }, kinds);
        }

        [Fact]
        public void The_effect_write_stores_a_clone_the_caller_cannot_reach_into()
        {
            var session = NewSession(out var video, out _, out _, out _);
            var effect = new VideoEffect { Kind = VideoEffectKind.Blur, Amount = 0.3 };

            session.SetItemEffect(video.Id, effect);
            effect.Amount = 0.9;

            Assert.Equal(0.3, session.Project.Items.Single(i => i.Id == video.Id).Effect.Amount);
        }
    }
}

using System;
using System.Collections.Generic;
using Clowd.VideoSDK.Audio;
using Clowd.VideoSDK.Model;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    // Pure mixing math against a fake IAudioSource — no FFmpeg, no pixels: volume, transition
    // volume ramps (shared easing), overlap summing, clamping, span coverage and the
    // timeline→source sample mapping.
    public class AudioMixerTests
    {
        private const int Rate = 48000;
        private const long Second = 10_000_000;

        // ----------------------------------------------------------------------------- fixture

        private sealed class FakeAudioSource : IAudioSource
        {
            public readonly List<(Guid SourceId, int StreamIndex, long Pos, int Frames)> Requests
                = new List<(Guid, int, long, int)>();

            public Func<Guid, float> ValueOf { get; set; } = _ => 0.5f;

            public bool ReadSamples(Guid sourceId, int streamIndex, long sourcePosFrames,
                float[] dst, int frames, out int framesRead)
            {
                Requests.Add((sourceId, streamIndex, sourcePosFrames, frames));
                float v = ValueOf(sourceId);
                for (int i = 0; i < frames * AudioMixer.Channels; i++)
                    dst[i] = v;
                framesRead = frames;
                return true;
            }
        }

        private static Project NewProject() => new Project
        {
            Output = new OutputSettings
            {
                WidthPx = 64,
                HeightPx = 64,
                FpsNum = 30,
                FpsDen = 1,
                SampleRate = Rate,
            },
        };

        private static Item AddAudioItem(Project project, Guid sourceId, long startTicks,
            long durationTicks, double volume = 1.0, bool muted = false)
        {
            var track = new Track
            {
                Id = Guid.NewGuid(),
                Kind = TrackKind.Audio,
                Order = project.Tracks.Count,
                Muted = muted,
            };
            project.Tracks.Add(track);
            var item = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = track.Id,
                TimelineStartTicks = startTicks,
                DurationTicks = durationTicks,
                Volume = volume,
                Content = new MediaContent { SourceId = sourceId, StreamIndex = 0, SourceInTicks = 0 },
            };
            project.Items.Add(item);
            return item;
        }

        private static float[] Mix(AudioMixer mixer, long firstFrame, int frames)
        {
            var dst = new float[frames * AudioMixer.Channels];
            mixer.MixChunk(firstFrame, frames, dst);
            return dst;
        }

        // ------------------------------------------------------------------------------- tests

        [Fact]
        public void Scales_by_item_volume()
        {
            var project = NewProject();
            var sourceId = Guid.NewGuid();
            AddAudioItem(project, sourceId, 0, Second, volume: 0.5);

            var fake = new FakeAudioSource { ValueOf = _ => 0.8f };
            var mixer = new AudioMixer(project, fake);
            Assert.Equal(1, mixer.AudibleItemCount);

            var dst = Mix(mixer, 0, 480);
            foreach (var v in dst)
                Assert.Equal(0.4f, v, 3);
        }

        [Fact]
        public void Silence_outside_item_span()
        {
            var project = NewProject();
            var sourceId = Guid.NewGuid();
            AddAudioItem(project, sourceId, Second / 2, Second); // starts at sample 24000

            var mixer = new AudioMixer(project, new FakeAudioSource());
            var dst = Mix(mixer, 23990, 20);

            for (int f = 0; f < 20; f++)
            {
                float expected = f < 10 ? 0f : 0.5f;
                Assert.Equal(expected, dst[f * 2], 3);
                Assert.Equal(expected, dst[f * 2 + 1], 3);
            }
        }

        [Fact]
        public void Overlapping_items_on_two_tracks_sum()
        {
            var project = NewProject();
            var a = Guid.NewGuid();
            var b = Guid.NewGuid();
            AddAudioItem(project, a, 0, Second);
            AddAudioItem(project, b, 0, Second);

            var fake = new FakeAudioSource { ValueOf = id => id == a ? 0.4f : 0.25f };
            var mixer = new AudioMixer(project, fake);

            var dst = Mix(mixer, 0, 100);
            foreach (var v in dst)
                Assert.Equal(0.65f, v, 3);
        }

        [Fact]
        public void Sum_is_hard_clamped_to_unit_range()
        {
            var project = NewProject();
            AddAudioItem(project, Guid.NewGuid(), 0, Second);
            AddAudioItem(project, Guid.NewGuid(), 0, Second);

            var loud = new FakeAudioSource { ValueOf = _ => 0.8f };
            var dst = Mix(new AudioMixer(project, loud), 0, 100);
            foreach (var v in dst)
                Assert.Equal(1f, v);

            var negative = new FakeAudioSource { ValueOf = _ => -0.8f };
            dst = Mix(new AudioMixer(project, negative), 0, 100);
            foreach (var v in dst)
                Assert.Equal(-1f, v);
        }

        [Fact]
        public void Muted_track_is_silent_but_keeps_the_audio_stream()
        {
            var project = NewProject();
            AddAudioItem(project, Guid.NewGuid(), 0, Second, muted: true);

            // stream layout stays stable (RenderJob still writes an audio stream)...
            Assert.True(AudioMixer.HasAudioItems(project));

            // ...but the mix is silence
            var mixer = new AudioMixer(project, new FakeAudioSource());
            Assert.Equal(0, mixer.AudibleItemCount);
            var dst = Mix(mixer, 0, 100);
            foreach (var v in dst)
                Assert.Equal(0f, v);
        }

        [Fact]
        public void Audio_end_is_the_last_audio_items_end()
        {
            // The renderer's audio stream runs to the end of the audio items, not the video's end
            // — a recording whose audio track is shorter than its video gets a shorter output
            // audio track, exactly as vid-render's atrim graph produced (parity gate cell c1).
            var project = NewProject();
            var sourceId = Guid.NewGuid();
            AddAudioItem(project, sourceId, 0, Second);
            AddAudioItem(project, sourceId, 2 * Second, Second / 2);

            var video = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video };
            project.Tracks.Add(video);
            project.Items.Add(new Item
            {
                Id = Guid.NewGuid(),
                TrackId = video.Id,
                DurationTicks = 5 * Second, // video runs on past the audio
                Content = new SolidContent { Color = "#FF102030" },
            });

            Assert.Equal(2 * Second + Second / 2, AudioMixer.GetAudioEndTicks(project));
            Assert.Equal(0, AudioMixer.GetAudioEndTicks(NewProject()));
        }

        [Fact]
        public void Video_only_project_has_no_audio_items()
        {
            var project = NewProject();
            var track = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video };
            project.Tracks.Add(track);
            project.Items.Add(new Item
            {
                Id = Guid.NewGuid(),
                TrackId = track.Id,
                DurationTicks = Second,
                Content = new SolidContent { Color = "#FF102030" },
            });

            Assert.False(AudioMixer.HasAudioItems(project));
            Assert.Equal(0, new AudioMixer(project, new FakeAudioSource()).AudibleItemCount);
        }

        [Fact]
        public void Entry_fade_ramps_volume_with_linear_easing()
        {
            var project = NewProject();
            var item = AddAudioItem(project, Guid.NewGuid(), 0, 2 * Second);
            item.Entry = new Transition
            {
                Kind = TransitionKind.Fade,
                DurationTicks = Second,
                Easing = TransitionEasing.Linear,
            };

            var fake = new FakeAudioSource { ValueOf = _ => 0.8f };
            var mixer = new AudioMixer(project, fake);

            // halfway through the 1s entry: gain 0.5 → 0.4
            var dst = Mix(mixer, Rate / 2, 1);
            Assert.Equal(0.4f, dst[0], 3);
            Assert.Equal(0.4f, dst[1], 3);

            // past the transition: full volume
            dst = Mix(mixer, 3 * Rate / 2, 1);
            Assert.Equal(0.8f, dst[0], 3);
        }

        [Fact]
        public void Entry_ramp_honors_cubic_easing()
        {
            var project = NewProject();
            var item = AddAudioItem(project, Guid.NewGuid(), 0, 2 * Second);
            item.Entry = new Transition
            {
                Kind = TransitionKind.Fade,
                DurationTicks = Second,
                Easing = TransitionEasing.CubicIn,
            };

            var fake = new FakeAudioSource { ValueOf = _ => 0.8f };
            var dst = Mix(new AudioMixer(project, fake), Rate / 2, 1);

            // CubicIn(0.5) = 0.125 → 0.8 * 0.125 = 0.1
            Assert.Equal(0.1f, dst[0], 3);
        }

        [Fact]
        public void Exit_transition_ramps_volume_down()
        {
            var project = NewProject();
            var item = AddAudioItem(project, Guid.NewGuid(), 0, 2 * Second);
            item.Exit = new Transition
            {
                Kind = TransitionKind.Fade,
                DurationTicks = Second,
                Easing = TransitionEasing.Linear,
            };

            var fake = new FakeAudioSource { ValueOf = _ => 0.8f };
            var mixer = new AudioMixer(project, fake);

            // before the exit window: full volume
            var dst = Mix(mixer, Rate / 2, 1);
            Assert.Equal(0.8f, dst[0], 3);

            // halfway through the exit: gain 0.5
            dst = Mix(mixer, 3 * Rate / 2, 1);
            Assert.Equal(0.4f, dst[0], 3);
        }

        [Fact]
        public void Non_fade_transitions_also_ramp_audio()
        {
            // A slide-out that left audio at full volume would be jarring: every active
            // transition kind ramps audio by its shown-fraction.
            var project = NewProject();
            var item = AddAudioItem(project, Guid.NewGuid(), 0, 2 * Second);
            item.Entry = new Transition
            {
                Kind = TransitionKind.SlideLeft,
                DurationTicks = Second,
                Easing = TransitionEasing.Linear,
            };

            var fake = new FakeAudioSource { ValueOf = _ => 0.8f };
            var dst = Mix(new AudioMixer(project, fake), Rate / 2, 1);
            Assert.Equal(0.4f, dst[0], 3);
        }

        [Fact]
        public void Maps_timeline_samples_to_source_positions()
        {
            var project = NewProject();
            var sourceId = Guid.NewGuid();
            var item = AddAudioItem(project, sourceId, Second, Second);
            ((MediaContent)item.Content).SourceInTicks = 5 * Second / 2; // 2.5s into the source
            ((MediaContent)item.Content).StreamIndex = 3;

            var fake = new FakeAudioSource();
            var mixer = new AudioMixer(project, fake);

            // chunk beginning exactly at the item start (sample 48000): the source read must
            // begin at 2.5s of source time = sample 120000, on the item's stream
            Mix(mixer, Rate, 10);
            var request = Assert.Single(fake.Requests);
            Assert.Equal(sourceId, request.SourceId);
            Assert.Equal(3, request.StreamIndex);
            Assert.Equal(5L * Rate / 2, request.Pos);
            Assert.Equal(10, request.Frames);

            // consecutive chunks step by exact sample counts (no per-chunk tick re-rounding)
            fake.Requests.Clear();
            Mix(mixer, Rate + 10, 10);
            request = Assert.Single(fake.Requests);
            Assert.Equal(5L * Rate / 2 + 10, request.Pos);
        }

        [Fact]
        public void Rejects_undersized_destination()
        {
            var project = NewProject();
            AddAudioItem(project, Guid.NewGuid(), 0, Second);
            var mixer = new AudioMixer(project, new FakeAudioSource());
            Assert.Throws<ArgumentOutOfRangeException>(() => mixer.MixChunk(0, 100, new float[10]));
        }
    }
}

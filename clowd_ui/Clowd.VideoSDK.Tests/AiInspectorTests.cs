using System;
using System.Linq;
using Clowd.UI.Services;
using Clowd.UI.VideoEditor.Inspector;
using Clowd.VideoSDK.Editing;
using Clowd.VideoSDK.Model;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The inspector over the AI features: the EFFECT section's gating and tile/dial writes, the
    /// AUDIO section's denoise toggle and strength dial, and the pure needed-sidecar computation
    /// the analysis manager queues from. Framework-free like <see cref="EffectInspectorTests"/> —
    /// no Avalonia here, and no <see cref="AiAnalysisManager"/> instance (its job running needs a
    /// dispatcher; its decision logic is the static method under test).
    /// </summary>
    public class AiInspectorTests
    {
        private static long Ms(long ms) => ms * TimeSpan.TicksPerMillisecond;

        private static (EditorSession Session, SelectedItemViewModel Vm) NewInspector(
            out Item video, out Item audio, out Item image)
        {
            var sourceId = Guid.NewGuid();
            var videoTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Screen", Order = 0 };
            var imageTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Image", Order = 1 };
            var audioTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Audio, Name = "Audio", Order = 2 };

            video = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = videoTrack.Id,
                TimelineStartTicks = 0,
                DurationTicks = Ms(10_000),
                Content = new MediaContent { SourceId = sourceId, StreamIndex = 0 },
            };
            image = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = imageTrack.Id,
                TimelineStartTicks = 0,
                DurationTicks = Ms(10_000),
                Content = new ImageContent { Path = @"C:\rec\overlay.png" },
            };
            audio = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = audioTrack.Id,
                TimelineStartTicks = 0,
                DurationTicks = Ms(10_000),
                Content = new MediaContent { SourceId = sourceId, StreamIndex = 1 },
            };

            var project = new Project
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
                Tracks = { videoTrack, imageTrack, audioTrack },
                Items = { video, image, audio },
            };

            var session = new EditorSession(project, null, null);
            return (session, new SelectedItemViewModel { Session = session });
        }

        private static Item Live(EditorSession session, Guid id) =>
            session.Project.Items.First(i => i.Id == id);

        private static Track LiveTrack(EditorSession session, Guid itemId)
        {
            var item = Live(session, itemId);
            return session.Project.Tracks.First(t => t.Id == item.TrackId);
        }

        // ---------------------------------------------------------------------- section gating

        [Fact]
        public void EffectSection_ShowsForVideoMediaItemsOnly()
        {
            var (session, vm) = NewInspector(out var video, out var audio, out var image);

            session.Select(video.Id);
            Assert.True(vm.ShowEffect);

            session.Select(audio.Id);
            Assert.False(vm.ShowEffect);

            session.Select(image.Id);
            Assert.False(vm.ShowEffect);

            var zoom = session.AddZoomEffect(Ms(1_000), Ms(2_000));
            session.Select(zoom.Id);
            Assert.False(vm.ShowEffect);

            var text = session.AddText(0, Ms(2_000));
            session.Select(text.Id);
            Assert.False(vm.ShowEffect);
        }

        [Fact]
        public void EffectAmountRow_FollowsTheKind()
        {
            var (session, vm) = NewInspector(out var video, out _, out _);
            session.Select(video.Id);

            Assert.True(vm.EffectNone);
            Assert.False(vm.ShowEffectAmount);

            vm.EffectBlur = true;
            Assert.True(vm.ShowEffectAmount);

            vm.EffectBgBlur = true;
            Assert.True(vm.ShowEffectAmount);

            vm.EffectBgRemove = true;
            Assert.False(vm.ShowEffectAmount);
        }

        [Fact]
        public void DenoiseRows_ShowForAudioOnly_AndStrengthOnlyWhileOn()
        {
            var (session, vm) = NewInspector(out var video, out var audio, out _);

            session.Select(audio.Id);
            Assert.True(vm.ShowAudio);
            Assert.False(vm.Denoise);
            Assert.False(vm.ShowDenoiseStrength);

            vm.Denoise = true;
            Assert.True(vm.ShowDenoiseStrength);

            session.Select(video.Id);
            Assert.False(vm.ShowDenoiseStrength);
        }

        // ----------------------------------------------------------------------- effect tiles

        [Fact]
        public void EffectTiles_WriteTheKindAtItsDefaults()
        {
            var (session, vm) = NewInspector(out var video, out _, out _);
            session.Select(video.Id);

            Assert.Null(Live(session, video.Id).Effect);

            vm.EffectBlur = true;

            var effect = Live(session, video.Id).Effect;
            Assert.NotNull(effect);
            Assert.Equal(VideoEffectKind.Blur, effect.Kind);
            Assert.Equal(VideoEffect.DefaultAmount, effect.Amount);
            Assert.Equal(VideoEffect.DefaultAmount, vm.EffectAmount);

            vm.EffectBgRemove = true;
            Assert.Equal(VideoEffectKind.BgRemove, Live(session, video.Id).Effect.Kind);

            // None stores a null effect, never a kind of None
            vm.EffectNone = true;
            Assert.Null(Live(session, video.Id).Effect);
        }

        [Fact]
        public void EffectTiles_DeselectionWritesNothing()
        {
            var (session, vm) = NewInspector(out var video, out _, out _);
            session.Select(video.Id);
            vm.EffectBlur = true;

            // the radio group's false on the currently selected tile must not touch the model
            vm.EffectBlur = false;

            Assert.Equal(VideoEffectKind.Blur, Live(session, video.Id).Effect.Kind);
            Assert.True(vm.EffectBlur);
        }

        [Fact]
        public void EffectTiles_ReReadAChangeFromElsewhere()
        {
            var (session, vm) = NewInspector(out var video, out _, out _);
            session.Select(video.Id);

            session.SetItemEffect(video.Id, new VideoEffect { Kind = VideoEffectKind.BgBlur, Amount = 0.3 },
                origin: new object());

            Assert.True(vm.EffectBgBlur);
            Assert.Equal(0.3, vm.EffectAmount);
        }

        [Fact]
        public void MatteNeed_FlipsTheChangeKindStructural()
        {
            var (session, vm) = NewInspector(out var video, out _, out _);
            session.Select(video.Id);

            ProjectChangeKind? last = null;
            session.ProjectChanged += (_, e) => last = e.Kind;

            vm.EffectBlur = true;
            Assert.Equal(ProjectChangeKind.Mapping, last);

            vm.EffectBgBlur = true;
            Assert.Equal(ProjectChangeKind.Structural, last);
        }

        // ---------------------------------------------------------------------- effect amount

        [Fact]
        public void EffectAmount_WritesClampedThrough()
        {
            var (session, vm) = NewInspector(out var video, out _, out _);
            session.Select(video.Id);
            vm.EffectBlur = true;

            vm.EffectAmount = 0.8;
            Assert.Equal(0.8, Live(session, video.Id).Effect.Amount);

            vm.EffectAmount = 3;
            Assert.Equal(1, Live(session, video.Id).Effect.Amount);

            vm.EffectAmount = -1;
            Assert.Equal(0, Live(session, video.Id).Effect.Amount);
        }

        /// <summary>The Amount row is hidden without a dial-bearing effect, but its binding stays
        /// live — a value arriving from it must not conjure an effect up.</summary>
        [Fact]
        public void EffectAmount_IsInertWithoutADialBearingEffect()
        {
            var (session, vm) = NewInspector(out var video, out _, out _);
            session.Select(video.Id);

            vm.EffectAmount = 0.7;
            Assert.Null(Live(session, video.Id).Effect);

            vm.EffectBgRemove = true;
            vm.EffectAmount = 0.2;
            Assert.Equal(VideoEffect.DefaultAmount, Live(session, video.Id).Effect.Amount);
        }

        [Fact]
        public void EffectWrites_CoalescePerItem()
        {
            var (session, vm) = NewInspector(out var video, out _, out _);
            session.Select(video.Id);

            // tile + spinner mashing lands in one undo entry (one shared per-item key)
            vm.EffectBlur = true;
            vm.EffectAmount = 0.6;
            vm.EffectAmount = 0.9;

            session.Undo();
            Assert.Null(Live(session, video.Id).Effect);
        }

        [Fact]
        public void EffectEdits_DoNotReachAnotherItem()
        {
            var (session, vm) = NewInspector(out var video, out _, out _);
            Assert.True(session.SplitItemAt(video.Id, Ms(5_000)));
            var second = session.Project.Items.First(i => i.TrackId == video.TrackId && i.Id != video.Id);
            session.Select(video.Id);

            vm.EffectBlur = true;

            Assert.NotNull(Live(session, video.Id).Effect);
            Assert.Null(Live(session, second.Id).Effect);
        }

        // ---------------------------------------------------------------------------- denoise

        [Fact]
        public void DenoiseToggle_WritesTheTrackFlagStructurally()
        {
            var (session, vm) = NewInspector(out _, out var audio, out _);
            session.Select(audio.Id);

            ProjectChangeKind? last = null;
            session.ProjectChanged += (_, e) => last = e.Kind;

            vm.Denoise = true;
            Assert.True(LiveTrack(session, audio.Id).Denoise);
            Assert.Equal(ProjectChangeKind.Structural, last);

            vm.Denoise = false;
            Assert.False(LiveTrack(session, audio.Id).Denoise);
        }

        [Fact]
        public void DenoiseStrength_WritesClampedThrough_AndCoalescesPerTrack()
        {
            var (session, vm) = NewInspector(out _, out var audio, out _);
            session.Select(audio.Id);
            vm.Denoise = true;

            vm.DenoiseStrength = 0.4;
            Assert.Equal(0.4, LiveTrack(session, audio.Id).DenoiseStrength);

            vm.DenoiseStrength = 7;
            Assert.Equal(1, LiveTrack(session, audio.Id).DenoiseStrength);

            // both spinner writes are one undo entry; the toggle before them is its own
            session.Undo();
            Assert.Equal(1.0, LiveTrack(session, audio.Id).DenoiseStrength);
            Assert.True(LiveTrack(session, audio.Id).Denoise);
        }

        /// <summary>The Strength row is hidden while the toggle is off, but its binding stays
        /// live — a value arriving from it must not move the track.</summary>
        [Fact]
        public void DenoiseStrength_IsInertWhileTheToggleIsOff()
        {
            var (session, vm) = NewInspector(out _, out var audio, out _);
            session.Select(audio.Id);

            vm.DenoiseStrength = 0.25;

            Assert.Equal(1.0, LiveTrack(session, audio.Id).DenoiseStrength);
        }

        [Fact]
        public void DenoiseToggle_ReReadsAChangeFromElsewhere()
        {
            var (session, vm) = NewInspector(out _, out var audio, out _);
            session.Select(audio.Id);

            session.SetTrackDenoise(Live(session, audio.Id).TrackId, true, origin: new object());

            Assert.True(vm.Denoise);
            Assert.True(vm.ShowDenoiseStrength);
        }

        // ----------------------------------------------------------------------- status rows

        [Fact]
        public void StatusRows_StayAwayWithNoManagerAttached()
        {
            var (session, vm) = NewInspector(out var video, out var audio, out _);

            session.Select(video.Id);
            vm.EffectBgBlur = true;
            Assert.False(vm.ShowEffectStatus);
            Assert.Null(vm.EffectStatusText);

            session.Select(audio.Id);
            vm.Denoise = true;
            Assert.False(vm.ShowDenoiseStatus);
            Assert.Null(vm.DenoiseStatusText);
        }

        [Fact]
        public void ViewModel_ToleratesNoSessionAtAll()
        {
            var vm = new SelectedItemViewModel();

            vm.EffectBlur = true;
            vm.EffectAmount = 0.7;
            vm.Denoise = true;
            vm.DenoiseStrength = 0.5;

            Assert.False(vm.ShowEffect);
            Assert.False(vm.ShowEffectStatus);
            Assert.False(vm.ShowDenoiseStatus);
        }

        // ------------------------------------------------------------------ needed sidecars

        private static Project SidecarProject(out Track audioTrack, out Item videoItem, out Item audioItem)
        {
            var sourceId = Guid.NewGuid();
            var videoTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Order = 0 };
            audioTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Audio, Order = 1 };

            videoItem = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = videoTrack.Id,
                DurationTicks = Ms(10_000),
                Content = new MediaContent { SourceId = sourceId, StreamIndex = 0 },
            };
            audioItem = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = audioTrack.Id,
                DurationTicks = Ms(10_000),
                Content = new MediaContent { SourceId = sourceId, StreamIndex = 1 },
            };

            return new Project
            {
                Output = new OutputSettings { WidthPx = 1920, HeightPx = 1080, FpsNum = 30, FpsDen = 1 },
                Sources = { new Source { Id = sourceId, Path = @"C:\rec\input.mp4" } },
                Tracks = { videoTrack, audioTrack },
                Items = { videoItem, audioItem },
            };
        }

        [Fact]
        public void RequiredSidecars_EmptyWithNothingEnabled()
        {
            var project = SidecarProject(out _, out _, out _);

            Assert.Empty(AiAnalysisManager.RequiredSidecars(project));
        }

        [Fact]
        public void RequiredSidecars_DenoiseTrackNeedsItsStreams()
        {
            var project = SidecarProject(out var audioTrack, out _, out var audioItem);
            audioTrack.Denoise = true;

            var media = (MediaContent)audioItem.Content;
            var key = Assert.Single(AiAnalysisManager.RequiredSidecars(project));
            Assert.Equal(new AiSidecarKey(AiSidecarKind.Denoise, media.SourceId, media.StreamIndex), key);
        }

        [Fact]
        public void RequiredSidecars_MatteNeedFollowsTheEffectKind()
        {
            var project = SidecarProject(out _, out var videoItem, out _);
            var media = (MediaContent)videoItem.Content;

            videoItem.Effect = new VideoEffect { Kind = VideoEffectKind.Blur };
            Assert.Empty(AiAnalysisManager.RequiredSidecars(project));

            videoItem.Effect = new VideoEffect { Kind = VideoEffectKind.BgBlur };
            var key = Assert.Single(AiAnalysisManager.RequiredSidecars(project));
            Assert.Equal(new AiSidecarKey(AiSidecarKind.Matte, media.SourceId, media.StreamIndex), key);

            videoItem.Effect = new VideoEffect { Kind = VideoEffectKind.BgRemove };
            key = Assert.Single(AiAnalysisManager.RequiredSidecars(project));
            Assert.Equal(AiSidecarKind.Matte, key.Kind);
        }

        [Fact]
        public void RequiredSidecars_DeduplicatesSplitSegments()
        {
            var project = SidecarProject(out var audioTrack, out var videoItem, out var audioItem);
            audioTrack.Denoise = true;
            videoItem.Effect = new VideoEffect { Kind = VideoEffectKind.BgRemove };

            // split segments of the same stream must not queue the same job twice
            project.Items.Add(new Item
            {
                Id = Guid.NewGuid(),
                TrackId = audioItem.TrackId,
                TimelineStartTicks = Ms(10_000),
                DurationTicks = Ms(5_000),
                Content = ((MediaContent)audioItem.Content).Clone(),
            });
            project.Items.Add(new Item
            {
                Id = Guid.NewGuid(),
                TrackId = videoItem.TrackId,
                TimelineStartTicks = Ms(10_000),
                DurationTicks = Ms(5_000),
                Content = ((MediaContent)videoItem.Content).Clone(),
                Effect = new VideoEffect { Kind = VideoEffectKind.BgBlur },
            });

            var keys = AiAnalysisManager.RequiredSidecars(project);
            Assert.Equal(2, keys.Count);
            Assert.Single(keys, k => k.Kind == AiSidecarKind.Denoise);
            Assert.Single(keys, k => k.Kind == AiSidecarKind.Matte);
        }
    }
}

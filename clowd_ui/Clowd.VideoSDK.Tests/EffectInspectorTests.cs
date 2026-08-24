using System;
using System.Linq;
using Clowd.UI.VideoEditor.Inspector;
using Clowd.VideoSDK.Editing;
using Clowd.VideoSDK.Model;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The inspector over the two effect item kinds: which sections an effect item lights (only its
    /// own, plus the ramp), and that every one of its editors writes the model the effect actually
    /// reads. Framework-free like <see cref="SelectedItemViewModelTests"/> — no Avalonia here.
    /// </summary>
    public class EffectInspectorTests
    {
        private static long Ms(long ms) => ms * TimeSpan.TicksPerMillisecond;

        private static (EditorSession Session, SelectedItemViewModel Vm) NewInspector(
            out Item video, out Item audio)
        {
            var sourceId = Guid.NewGuid();
            var videoTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Screen", Order = 0 };
            var audioTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Audio, Name = "Audio", Order = 1 };

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
                Tracks = { videoTrack, audioTrack },
                Items = { video, audio },
            };

            var session = new EditorSession(project, null, null);
            return (session, new SelectedItemViewModel { Session = session });
        }

        private static Item Live(EditorSession session, Guid id) =>
            session.Project.Items.First(i => i.Id == id);

        // ---------------------------------------------------------------------- section gating

        [Fact]
        public void SpeedItem_ShowsItsOwnSectionAndNothingElse()
        {
            var (session, vm) = NewInspector(out _, out _);
            var speed = session.AddSpeedEffect(0, Ms(5_000));
            session.Select(speed.Id);

            Assert.True(vm.HasSelection);
            Assert.True(vm.ShowSpeedEffect);
            Assert.True(vm.ShowRamp);
            Assert.False(vm.ShowZoomEffect);
            Assert.False(vm.ShowTransform);
            Assert.False(vm.ShowScale);
            Assert.False(vm.ShowMask);
            Assert.False(vm.ShowCrop);
            Assert.False(vm.ShowText);
            Assert.False(vm.ShowAudio);
            Assert.False(vm.ShowSpeed);
            Assert.False(vm.ShowTransitions);
            Assert.False(vm.ShowTrackMuted);
            // the eye stays: hiding the row is how the effect is switched off
            Assert.True(vm.ShowTrackHidden);
            Assert.Equal("Speed", vm.SubjectKind);
        }

        [Fact]
        public void ZoomItem_ShowsItsOwnSectionAndNothingElse()
        {
            var (session, vm) = NewInspector(out _, out _);
            var zoom = session.AddZoomEffect(0, Ms(5_000));
            session.Select(zoom.Id);

            Assert.True(vm.ShowZoomEffect);
            Assert.True(vm.ShowRamp);
            Assert.False(vm.ShowSpeedEffect);
            Assert.False(vm.ShowTransform);
            Assert.False(vm.ShowCrop);
            Assert.False(vm.ShowAudio);
            Assert.False(vm.ShowTransitions);
            Assert.True(vm.ShowTrackHidden);
            Assert.Equal("Zoom", vm.SubjectKind);
        }

        [Fact]
        public void MediaItem_KeepsTheKindBasedAnimationSection()
        {
            var (session, vm) = NewInspector(out var video, out var audio);

            session.Select(video.Id);
            Assert.True(vm.ShowTransitions);
            Assert.False(vm.ShowRamp);
            Assert.False(vm.ShowSpeedEffect);
            Assert.False(vm.ShowZoomEffect);

            // audio animates its volume, which has no kind — the ramp section instead
            session.Select(audio.Id);
            Assert.False(vm.ShowTransitions);
            Assert.True(vm.ShowRamp);
            Assert.True(vm.ShowAudio);
        }

        // -------------------------------------------------------------------------------- speed

        [Fact]
        public void SpeedTarget_ReadsAndWritesTheFactor()
        {
            var (session, vm) = NewInspector(out _, out _);
            var speed = session.AddSpeedEffect(0, Ms(5_000));
            session.Select(speed.Id);

            Assert.Equal(2.0, vm.SpeedTarget.Value);

            vm.SpeedTarget = SelectedItemViewModel.SpeedTargetOptions.First(o => o.Value == 0.5);

            Assert.Equal(0.5, ((SpeedContent)Live(session, speed.Id).Content).Factor);
            Assert.Equal(0.5, vm.SpeedTarget.Value);
        }

        [Fact]
        public void SpeedTarget_ReReadsAChangeFromElsewhere()
        {
            var (session, vm) = NewInspector(out _, out _);
            var speed = session.AddSpeedEffect(0, Ms(5_000));
            session.Select(speed.Id);

            session.SetSpeedFactor(speed.Id, 4.0, origin: new object());

            Assert.Equal(4.0, vm.SpeedTarget.Value);
        }

        [Fact]
        public void SpeedPitchCorrect_ReadsAndWritesTheFlag()
        {
            var (session, vm) = NewInspector(out _, out _);
            var speed = session.AddSpeedEffect(0, Ms(5_000));
            session.Select(speed.Id);

            Assert.True(vm.SpeedPitchCorrect);

            vm.SpeedPitchCorrect = false;

            Assert.False(((SpeedContent)Live(session, speed.Id).Content).PitchCorrect);
            Assert.False(vm.SpeedPitchCorrect);
        }

        [Fact]
        public void SpeedPitchCorrect_ReReadsAChangeFromElsewhere()
        {
            var (session, vm) = NewInspector(out _, out _);
            var speed = session.AddSpeedEffect(0, Ms(5_000));
            session.Select(speed.Id);

            session.SetSpeedPitchCorrect(speed.Id, false, origin: new object());

            Assert.False(vm.SpeedPitchCorrect);
        }

        [Fact]
        public void SpeedTargetOptions_AreTheSameObjectsTheDropdownOffers()
        {
            Assert.Contains(SelectedItemViewModel.DefaultSpeedTargetOption, SelectedItemViewModel.SpeedTargetOptions);
            Assert.Equal(2.0, SelectedItemViewModel.DefaultSpeedTargetOption.Value);
        }

        // --------------------------------------------------------------------------------- zoom

        [Fact]
        public void ZoomSpinners_WriteTheItemsOwnContent()
        {
            var (session, vm) = NewInspector(out _, out _);
            var zoom = session.AddZoomEffect(0, Ms(5_000));
            session.Select(zoom.Id);

            Assert.Equal(1.5, vm.ZoomFactor);
            Assert.Equal(0.5, vm.ZoomFocusX);
            Assert.Equal(0.5, vm.ZoomFocusY);

            vm.ZoomFactor = 2.5;
            vm.ZoomFocusX = 0.2;
            vm.ZoomFocusY = 0.8;

            var content = (ZoomContent)Live(session, zoom.Id).Content;
            Assert.Equal(2.5, content.Zoom);
            Assert.Equal(0.2, content.FocusX);
            Assert.Equal(0.8, content.FocusY);
        }

        [Fact]
        public void ZoomSpinners_ClampToTheModelsRange()
        {
            var (session, vm) = NewInspector(out _, out _);
            var zoom = session.AddZoomEffect(0, Ms(5_000));
            session.Select(zoom.Id);

            vm.ZoomFactor = 9;
            vm.ZoomFocusX = -1;
            vm.ZoomFocusY = Double.NaN;

            var content = (ZoomContent)Live(session, zoom.Id).Content;
            Assert.Equal(SelectedItemViewModel.MaxZoom, content.Zoom);
            Assert.Equal(0, content.FocusX);
            Assert.Equal(0, content.FocusY);
        }

        [Fact]
        public void ZoomEdits_DoNotReachAnotherZoomRow()
        {
            var (session, vm) = NewInspector(out _, out _);
            var first = session.AddZoomEffect(0, Ms(2_000));
            var second = session.AddZoomEffect(Ms(4_000), Ms(2_000));
            session.Select(second.Id);

            vm.ZoomFactor = 3;

            Assert.Equal(3, ((ZoomContent)Live(session, second.Id).Content).Zoom);
            Assert.Equal(1.5, ((ZoomContent)Live(session, first.Id).Content).Zoom);
        }

        // --------------------------------------------------------------------------------- ramp

        [Fact]
        public void Ramp_CheckboxIsTheSwitchAndSeedsTheDefaults()
        {
            var (session, vm) = NewInspector(out _, out _);
            var zoom = session.AddZoomEffect(0, Ms(5_000));
            session.Select(zoom.Id);

            Assert.False(vm.RampEntryEnabled);
            Assert.Null(Live(session, zoom.Id).Entry);

            vm.RampEntryEnabled = true;

            var entry = Live(session, zoom.Id).Entry;
            Assert.NotNull(entry);
            Assert.Equal(TransitionKind.Ramp, entry.Kind);
            Assert.Equal(Ms((long)SelectedItemViewModel.DefaultTransitionMs), entry.DurationTicks);
            Assert.Equal(SelectedItemViewModel.DefaultTransitionEasing, entry.Easing);
            Assert.Equal(SelectedItemViewModel.DefaultTransitionMs, vm.RampEntryMs);

            vm.RampEntryEnabled = false;

            Assert.Null(Live(session, zoom.Id).Entry);
        }

        [Fact]
        public void Ramp_LengthAndEasingWriteWhileItIsOn()
        {
            var (session, vm) = NewInspector(out _, out _);
            var zoom = session.AddZoomEffect(0, Ms(5_000));
            session.Select(zoom.Id);

            vm.RampEntryEnabled = true;
            vm.RampEntryMs = 400;
            vm.RampEntryEasing = TransitionEasing.CubicIn;

            var entry = Live(session, zoom.Id).Entry;
            Assert.Equal(Ms(400), entry.DurationTicks);
            Assert.Equal(TransitionEasing.CubicIn, entry.Easing);
        }

        /// <summary>The rows are hidden while the box is unticked, but their bindings stay live —
        /// a value arriving from one must not put a ramp back on the item.</summary>
        [Fact]
        public void Ramp_LengthAndEasingAreInertWhileItIsOff()
        {
            var (session, vm) = NewInspector(out _, out _);
            var zoom = session.AddZoomEffect(0, Ms(5_000));
            session.Select(zoom.Id);

            vm.RampEntryMs = 400;
            vm.RampEntryEasing = TransitionEasing.Linear;

            Assert.Null(Live(session, zoom.Id).Entry);
            Assert.False(vm.RampEntryEnabled);
        }

        [Fact]
        public void Ramp_ValuesAreStickyAcrossAnOffAndOn()
        {
            var (session, vm) = NewInspector(out _, out _);
            var speed = session.AddSpeedEffect(0, Ms(5_000));
            session.Select(speed.Id);

            vm.RampExitEnabled = true;
            vm.RampExitMs = 250;
            vm.RampExitEasing = TransitionEasing.Linear;

            var exit = Live(session, speed.Id).Exit;
            Assert.Equal(TransitionKind.Ramp, exit.Kind);
            Assert.Equal(TransitionEasing.Linear, exit.Easing);

            vm.RampExitEnabled = false;
            Assert.Null(Live(session, speed.Id).Exit);
            Assert.Equal(250, vm.RampExitMs);
            Assert.Equal(TransitionEasing.Linear, vm.RampExitEasing);

            vm.RampExitEnabled = true;
            exit = Live(session, speed.Id).Exit;
            Assert.Equal(Ms(250), exit.DurationTicks);
            Assert.Equal(TransitionEasing.Linear, exit.Easing);
        }

        [Fact]
        public void Ramp_EndsAreIndependent()
        {
            var (session, vm) = NewInspector(out _, out _);
            var zoom = session.AddZoomEffect(0, Ms(5_000));
            session.Select(zoom.Id);

            vm.RampEntryEnabled = true;

            Assert.NotNull(Live(session, zoom.Id).Entry);
            Assert.Null(Live(session, zoom.Id).Exit);
            Assert.False(vm.RampExitEnabled);
        }

        [Fact]
        public void Ramp_ReadsAnItemsExistingTransition()
        {
            var (session, vm) = NewInspector(out _, out _);
            var zoom = session.AddZoomEffect(0, Ms(5_000));
            session.EditItem(zoom.Id, i => i.Entry = new Transition
            {
                Kind = TransitionKind.Ramp,
                DurationTicks = Ms(750),
                Easing = TransitionEasing.CubicIn,
            }, origin: null);
            session.Select(zoom.Id);

            Assert.True(vm.RampEntryEnabled);
            Assert.Equal(750, vm.RampEntryMs);
            Assert.Equal(TransitionEasing.CubicIn, vm.RampEntryEasing);
        }

        /// <summary>Selecting an item without a ramp unticks the box — the section describes the
        /// item under it, not the last one that had a ramp.</summary>
        [Fact]
        public void Ramp_UnticksForAnItemWithoutOne()
        {
            var (session, vm) = NewInspector(out _, out _);
            var first = session.AddZoomEffect(0, Ms(2_000));
            var second = session.AddZoomEffect(Ms(4_000), Ms(2_000));

            session.Select(first.Id);
            vm.RampEntryEnabled = true;

            session.Select(second.Id);

            Assert.False(vm.RampEntryEnabled);
            Assert.Null(Live(session, second.Id).Entry);
        }

        [Fact]
        public void AudioRamp_WritesARampNotAKind()
        {
            var (session, vm) = NewInspector(out _, out var audio);
            session.Select(audio.Id);

            vm.RampEntryEnabled = true;
            vm.RampEntryMs = 500;

            var entry = Live(session, audio.Id).Entry;
            Assert.Equal(TransitionKind.Ramp, entry.Kind);
            Assert.Equal(Ms(500), entry.DurationTicks);
        }

        [Fact]
        public void SelectionChange_SwapsTheSections()
        {
            var (session, vm) = NewInspector(out var video, out _);
            var speed = session.AddSpeedEffect(0, Ms(5_000));

            session.Select(speed.Id);
            Assert.True(vm.ShowSpeedEffect);

            session.Select(video.Id);
            Assert.False(vm.ShowSpeedEffect);
            Assert.False(vm.ShowRamp);
            Assert.True(vm.ShowTransform);
            Assert.True(vm.ShowTransitions);
        }
    }
}

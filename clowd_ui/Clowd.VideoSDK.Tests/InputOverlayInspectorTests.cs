using System;
using System.Linq;
using Clowd.UI.VideoEditor.Inspector;
using Clowd.VideoSDK.Editing;
using Clowd.VideoSDK.Model;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The inspector over the two input-overlay item kinds: which sections each lights (the cursor
    /// carries no placement at all, the keystroke block carries position and width but nothing
    /// picture-shaped), and that every editor writes the whole overlay row — the split segments of
    /// one overlay must never end up with different styles or timings. Framework-free like
    /// <see cref="EffectInspectorTests"/> — no Avalonia here.
    /// </summary>
    public class InputOverlayInspectorTests
    {
        private const string CapturePath = @"C:\rec\input-capture.jsonl";

        private static long Ms(long ms) => ms * TimeSpan.TicksPerMillisecond;

        /// <summary>A recording cut into two keep-segments (one link group each) over a source that
        /// carries input capture — so every overlay row the factories build has two items, which is
        /// what the fan-out tests are about.</summary>
        private static (EditorSession Session, SelectedItemViewModel Vm) NewInspector(out Item screen)
        {
            var sourceId = Guid.NewGuid();
            var screenTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Screen", Order = 0 };
            var audioTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Audio, Name = "Audio", Order = 1 };

            var project = new Project
            {
                Output = new OutputSettings { WidthPx = 1920, HeightPx = 1080, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
                Sources =
                {
                    new Source
                    {
                        Id = sourceId,
                        Path = @"C:\rec\input.mp4",
                        InputCapturePath = CapturePath,
                        CursorStreamIndex = 2,
                        Streams =
                        {
                            new SourceStream { Index = 0, Kind = StreamKind.Video, Width = 1920, Height = 1080, AvgFrameRateNum = 30, AvgFrameRateDen = 1, DurationTicks = Ms(60_000) },
                            new SourceStream { Index = 1, Kind = StreamKind.Audio, DurationTicks = Ms(60_000) },
                            new SourceStream { Index = 2, Kind = StreamKind.Video, Width = 512, Height = 512, DurationTicks = Ms(60_000) },
                        },
                    },
                },
                Tracks = { screenTrack, audioTrack },
            };

            screen = AddSegment(project, screenTrack, audioTrack, sourceId, 0, Ms(8_000));
            AddSegment(project, screenTrack, audioTrack, sourceId, Ms(8_000), Ms(12_000));

            var session = new EditorSession(project, null, null);
            return (session, new SelectedItemViewModel { Session = session });
        }

        private static Item AddSegment(Project project, Track screen, Track audio, Guid sourceId,
            long startTicks, long durationTicks)
        {
            var group = Guid.NewGuid();
            var item = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = screen.Id,
                TimelineStartTicks = startTicks,
                DurationTicks = durationTicks,
                Content = new MediaContent { SourceId = sourceId, StreamIndex = 0, SourceInTicks = startTicks },
                LinkGroupId = group,
            };
            project.Items.Add(item);
            project.Items.Add(new Item
            {
                Id = Guid.NewGuid(),
                TrackId = audio.Id,
                TimelineStartTicks = startTicks,
                DurationTicks = durationTicks,
                Content = new MediaContent { SourceId = sourceId, StreamIndex = 1, SourceInTicks = startTicks },
                LinkGroupId = group,
            });
            return item;
        }

        private static Item Live(EditorSession session, Guid id) =>
            session.Project.Items.First(i => i.Id == id);

        private static CursorContent[] CursorItems(EditorSession session) =>
            session.Project.Items.Select(i => i.Content).OfType<CursorContent>().ToArray();

        private static KeyboardContent[] KeyboardItems(EditorSession session) =>
            session.Project.Items.Select(i => i.Content).OfType<KeyboardContent>().ToArray();

        // ---------------------------------------------------------------------- section gating

        [Fact]
        public void CursorItem_ShowsItsOwnSectionAndNoPlacement()
        {
            var (session, vm) = NewInspector(out _);
            var cursor = session.AddCursorTrack();
            session.Select(cursor.Id);

            Assert.True(vm.HasSelection);
            Assert.True(vm.ShowCursorTrack);
            // position, size, aspect, crop and shape all come from the screen row it is synced to
            Assert.False(vm.ShowTransform);
            Assert.False(vm.ShowScale);
            Assert.False(vm.ShowMask);
            Assert.False(vm.ShowCrop);
            Assert.False(vm.ShowKeyboardTrack);
            Assert.False(vm.ShowText);
            Assert.False(vm.ShowAudio);
            Assert.False(vm.ShowSpeed);
            Assert.False(vm.ShowSpeedEffect);
            Assert.False(vm.ShowZoomEffect);
            Assert.False(vm.ShowRamp);
            Assert.False(vm.ShowTransitions);
            Assert.False(vm.ShowTrackMuted);
            // the eye stays: hiding the row is how the overlay is switched off
            Assert.True(vm.ShowTrackHidden);
            Assert.Equal("Cursor", vm.SubjectKind);
            Assert.Equal("Cursor", vm.SubjectName);
        }

        [Fact]
        public void KeyboardItem_KeepsPlacementButNothingPictureShaped()
        {
            var (session, vm) = NewInspector(out _);
            var keys = session.AddKeyboardTrack();
            session.Select(keys.Id);

            Assert.True(vm.ShowKeyboardTrack);
            // the transform IS the block's anchor and wrap width
            Assert.True(vm.ShowTransform);
            Assert.True(vm.ShowScale);
            Assert.Equal(0.5, vm.PositionX);
            Assert.Equal(0.5, vm.Scale);
            // …but a keystroke block is not a picture: no ratio, crop or mask — and no rotation,
            // which the composer never reads for it (DrawKeyboard draws the block upright)
            Assert.False(vm.ShowRotation);
            Assert.False(vm.ShowMask);
            Assert.False(vm.ShowCrop);
            Assert.False(vm.ShowCursorTrack);
            Assert.False(vm.ShowSpeed);
            Assert.False(vm.ShowRamp);
            Assert.Equal("Keys", vm.SubjectKind);
        }

        [Fact]
        public void RotationRow_StaysForOrdinaryPictureItems()
        {
            var (session, vm) = NewInspector(out var screen);
            session.Select(screen.Id);

            Assert.True(vm.ShowTransform);
            Assert.True(vm.ShowRotation);
        }

        // -------------------------------------------------------------------------------- cursor

        [Fact]
        public void CursorStyle_ReadsAndWritesEverySegmentOfTheRow()
        {
            var (session, vm) = NewInspector(out _);
            var cursor = session.AddCursorTrack();
            session.Select(cursor.Id);

            Assert.Equal(2, CursorItems(session).Length);
            Assert.Equal("ios-glyph", vm.CursorStyle.Value);
            Assert.True(vm.CursorGlyphEnabled);

            vm.CursorStyle = SelectedItemViewModel.CursorStyleOptions.First(o => o.Value == "material");

            Assert.All(CursorItems(session), c => Assert.Equal("material", c.Style));
            Assert.Equal("material", vm.CursorStyle.Value);
        }

        [Fact]
        public void NativeStyle_TurnsOffTheGlyphOnlyRows()
        {
            var (session, vm) = NewInspector(out _);
            var cursor = session.AddCursorTrack();
            session.Select(cursor.Id);

            vm.CursorStyle = SelectedItemViewModel.CursorStyleOptions
                .First(o => o.Value == SelectedItemViewModel.NativeCursorStyle);

            Assert.False(vm.CursorGlyphEnabled);

            vm.CursorStyle = SelectedItemViewModel.DefaultCursorStyleOption;
            Assert.True(vm.CursorGlyphEnabled);
        }

        [Fact]
        public void CursorSizeShadowAndClicks_WriteTheWholeRow()
        {
            var (session, vm) = NewInspector(out _);
            var cursor = session.AddCursorTrack();
            session.Select(cursor.Id);

            Assert.Equal(1.0, vm.CursorSize);
            Assert.False(vm.CursorDropShadow);
            Assert.Equal("none", vm.CursorClickAnimation.Value);

            vm.CursorSize = 2.0;
            vm.CursorDropShadow = true;
            vm.CursorClickAnimation = SelectedItemViewModel.ClickAnimationOptions.First(o => o.Value == "ripple");

            Assert.All(CursorItems(session), c =>
            {
                Assert.Equal(2.0, c.Size);
                Assert.True(c.DropShadow);
                Assert.Equal("ripple", c.ClickAnimation);
            });
        }

        [Fact]
        public void CursorSize_ClampsToTheSpinnersRange()
        {
            var (session, vm) = NewInspector(out _);
            var cursor = session.AddCursorTrack();
            session.Select(cursor.Id);

            vm.CursorSize = 99;
            Assert.Equal(SelectedItemViewModel.MaxCursorSize, vm.CursorSize);

            vm.CursorSize = -5;
            Assert.Equal(SelectedItemViewModel.MinCursorSize, vm.CursorSize);

            vm.CursorSize = Double.NaN;
            Assert.Equal(SelectedItemViewModel.MinCursorSize, vm.CursorSize);
        }

        [Fact]
        public void CursorSection_ReReadsAChangeFromElsewhere()
        {
            var (session, vm) = NewInspector(out _);
            var cursor = session.AddCursorTrack();
            session.Select(cursor.Id);

            session.EditItem(cursor.Id, i =>
            {
                var content = (CursorContent)i.Content;
                content.Style = "doodle";
                content.Size = 1.75;
                content.DropShadow = true;
                content.ClickAnimation = "pulse";
            }, "test", structural: false, origin: new object());

            Assert.Equal("doodle", vm.CursorStyle.Value);
            Assert.Equal(1.75, vm.CursorSize);
            Assert.True(vm.CursorDropShadow);
            Assert.Equal("pulse", vm.CursorClickAnimation.Value);
        }

        // ------------------------------------------------------------------------------ keyboard

        [Fact]
        public void KeyboardSpinners_WriteTheWholeRow()
        {
            var (session, vm) = NewInspector(out _);
            var keys = session.AddKeyboardTrack();
            session.Select(keys.Id);

            Assert.Equal(2, KeyboardItems(session).Length);
            Assert.Equal(28, vm.KeyboardFontSize);
            Assert.Equal(300, vm.KeyboardLingerMs);
            Assert.Equal(250, vm.KeyboardFadeMs);
            Assert.Equal(1000, vm.KeyboardPauseBreakMs);

            vm.KeyboardFontSize = 40;
            vm.KeyboardLingerMs = 500;
            vm.KeyboardFadeMs = 100;
            vm.KeyboardPauseBreakMs = 2000;

            Assert.All(KeyboardItems(session), k =>
            {
                Assert.Equal(40, k.FontSize);
                Assert.Equal(500, k.LingerMs);
                Assert.Equal(100, k.FadeMs);
                Assert.Equal(2000, k.PauseBreakMs);
            });
        }

        [Fact]
        public void KeyboardSpinners_ClampToTheModelsRanges()
        {
            var (session, vm) = NewInspector(out _);
            var keys = session.AddKeyboardTrack();
            session.Select(keys.Id);

            vm.KeyboardFontSize = 5000;
            vm.KeyboardLingerMs = 99_999;
            vm.KeyboardFadeMs = -1;
            vm.KeyboardPauseBreakMs = Double.NaN;

            Assert.Equal(SelectedItemViewModel.MaxKeyboardFontSize, vm.KeyboardFontSize);
            Assert.Equal(SelectedItemViewModel.MaxKeyboardMs, vm.KeyboardLingerMs);
            Assert.Equal(0, vm.KeyboardFadeMs);
            Assert.Equal(0, vm.KeyboardPauseBreakMs);

            var content = (KeyboardContent)Live(session, keys.Id).Content;
            Assert.Equal(SelectedItemViewModel.MaxKeyboardFontSize, content.FontSize);
            Assert.Equal((int)SelectedItemViewModel.MaxKeyboardMs, content.LingerMs);
            Assert.Equal(0, content.FadeMs);
            Assert.Equal(0, content.PauseBreakMs);
        }

        [Fact]
        public void KeyboardPlacement_StillWritesTheTransform()
        {
            var (session, vm) = NewInspector(out _);
            var keys = session.AddKeyboardTrack();
            session.Select(keys.Id);

            vm.PositionY = 0.7;
            vm.Scale = 0.8;

            var transform = Live(session, keys.Id).Transform;
            Assert.Equal(0.7, transform.Y);
            Assert.Equal(0.8, transform.Scale);
        }

        [Fact]
        public void KeyboardSection_ReReadsAChangeFromElsewhere()
        {
            var (session, vm) = NewInspector(out _);
            var keys = session.AddKeyboardTrack();
            session.Select(keys.Id);

            session.EditItem(keys.Id, i =>
            {
                var content = (KeyboardContent)i.Content;
                content.FontSize = 64;
                content.LingerMs = 111;
                content.FadeMs = 222;
                content.PauseBreakMs = 333;
            }, "test", structural: false, origin: new object());

            Assert.Equal(64, vm.KeyboardFontSize);
            Assert.Equal(111, vm.KeyboardLingerMs);
            Assert.Equal(222, vm.KeyboardFadeMs);
            Assert.Equal(333, vm.KeyboardPauseBreakMs);
        }

        // ------------------------------------------------------------------------------- options

        [Fact]
        public void OptionLists_MirrorTheModelsOwnValues()
        {
            Assert.Equal(CursorContent.Styles.ToArray(),
                SelectedItemViewModel.CursorStyleOptions.Select(o => o.Value).ToArray());
            Assert.Equal(CursorContent.ClickAnimations.ToArray(),
                SelectedItemViewModel.ClickAnimationOptions.Select(o => o.Value).ToArray());

            // the dropdown selects by reference, so the defaults must be list members
            Assert.Contains(SelectedItemViewModel.DefaultCursorStyleOption, SelectedItemViewModel.CursorStyleOptions);
            Assert.Contains(SelectedItemViewModel.DefaultClickAnimationOption, SelectedItemViewModel.ClickAnimationOptions);
            Assert.Equal("ios-glyph", SelectedItemViewModel.DefaultCursorStyleOption.Value);
            Assert.Equal("none", SelectedItemViewModel.DefaultClickAnimationOption.Value);
            Assert.Equal("iOS Glyph", SelectedItemViewModel.DefaultCursorStyleOption.Label);
            Assert.Equal("Native", SelectedItemViewModel.CursorStyleOptions[0].Label);
        }

        [Fact]
        public void CursorStyleGetter_IsTheSingletonTheDropdownOffers()
        {
            var (session, vm) = NewInspector(out _);
            var cursor = session.AddCursorTrack();
            session.Select(cursor.Id);

            Assert.Same(SelectedItemViewModel.DefaultCursorStyleOption, vm.CursorStyle);
            Assert.Same(SelectedItemViewModel.DefaultClickAnimationOption, vm.CursorClickAnimation);
        }

        // ------------------------------------------------------------------------------- writes

        [Fact]
        public void CursorWrites_AreIgnoredWhenSomethingElseIsSelected()
        {
            var (session, vm) = NewInspector(out var screen);
            var cursor = session.AddCursorTrack();
            session.Select(cursor.Id);
            session.Select(screen.Id);

            // the section's bindings stay live while it is hidden: a stale write must find nothing
            // to do rather than reach the screen item under the selection
            vm.CursorSize = 2.5;
            vm.KeyboardFontSize = 64;

            Assert.All(CursorItems(session), c => Assert.Equal(1.0, c.Size));
            Assert.IsType<MediaContent>(Live(session, screen.Id).Content);
        }
    }
}

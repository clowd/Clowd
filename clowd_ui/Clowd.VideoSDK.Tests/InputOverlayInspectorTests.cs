using System;
using System.Linq;
using Clowd.UI.VideoEditor.Inspector;
using Clowd.VideoSDK.Composition;
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
            Assert.Equal("vision", vm.CursorStyle.Value);
            Assert.True(vm.CursorGlyphEnabled);

            vm.CursorStyle = SelectedItemViewModel.CursorStyleOptions.First(o => o.Value == "native");

            Assert.All(CursorItems(session), c => Assert.Equal("native", c.Style));
            Assert.Equal("native", vm.CursorStyle.Value);
        }

        /// <summary>The colourway row is the style's own: it appears only for a style that offers
        /// more than one, and writing it fans out over the row exactly as the style does.</summary>
        [Fact]
        public void CursorVariant_ShowsOnlyForAStyleWithColourwaysAndWritesTheWholeRow()
        {
            var (session, vm) = NewInspector(out _);
            var cursor = session.AddCursorTrack();
            session.Select(cursor.Id);

            // vision offers two; nothing is stored until the user picks, and the getter reads the
            // style's default rather than showing an empty tile row
            Assert.True(vm.CursorVariantsVisible);
            Assert.Equal(new[] { "dark", "light" }, vm.CursorVariantOptions.Select(o => o.Value).ToArray());
            Assert.Equal("dark", vm.CursorVariant.Value);
            Assert.All(CursorItems(session), c => Assert.Null(c.Variant));

            vm.CursorVariant = vm.CursorVariantOptions.First(o => o.Value == "light");

            Assert.All(CursorItems(session), c => Assert.Equal("light", c.Variant));
            Assert.Equal("light", vm.CursorVariant.Value);

            // native has no artwork at all, so it has no colourways and the row leaves the panel
            vm.CursorStyle = SelectedItemViewModel.CursorStyleOptions
                .First(o => o.Value == SelectedItemViewModel.NativeCursorStyle);

            Assert.False(vm.CursorVariantsVisible);
            Assert.Empty(vm.CursorVariantOptions);
            Assert.Null(vm.CursorVariant);

            // ...and the pick survives the trip: going back re-selects what was stored
            vm.CursorStyle = SelectedItemViewModel.DefaultCursorStyleOption;
            Assert.True(vm.CursorVariantsVisible);
            Assert.Equal("light", vm.CursorVariant.Value);
        }

        [Fact]
        public void NativeStyle_HidesTheGlyphOnlyRows()
        {
            // the size row and the whole EFFECT section bind their IsVisible to this: under native
            // they do not merely grey out, they leave the panel
            var (session, vm) = NewInspector(out _);
            var cursor = session.AddCursorTrack();
            session.Select(cursor.Id);

            vm.CursorStyle = SelectedItemViewModel.CursorStyleOptions
                .First(o => o.Value == SelectedItemViewModel.NativeCursorStyle);

            Assert.False(vm.CursorGlyphEnabled);
            Assert.False(vm.ShowSurround);

            vm.CursorStyle = SelectedItemViewModel.DefaultCursorStyleOption;
            Assert.True(vm.CursorGlyphEnabled);
            Assert.True(vm.ShowSurround);
        }

        [Fact]
        public void OverlayRows_AreSyncedButOfferNoDesync()
        {
            // the overlays read the recording's input capture at the recording's own times, so
            // their sync is not a toggle: the banner shows, the Desync button does not
            var (session, vm) = NewInspector(out var screen);
            var cursor = session.AddCursorTrack();
            var keys = session.AddKeyboardTrack();

            session.Select(cursor.Id);
            Assert.True(vm.IsLinked);
            Assert.False(vm.CanDesync);
            Assert.False(((System.Windows.Input.ICommand)vm.CommandUnlink).CanExecute(null));

            session.Select(keys.Id);
            Assert.True(vm.IsLinked);
            Assert.False(vm.CanDesync);
            Assert.False(((System.Windows.Input.ICommand)vm.CommandUnlink).CanExecute(null));

            // an ordinary linked media row keeps the way out
            session.Select(screen.Id);
            Assert.True(vm.IsLinked);
            Assert.True(vm.CanDesync);
            Assert.True(((System.Windows.Input.ICommand)vm.CommandUnlink).CanExecute(null));
        }

        [Fact]
        public void ClickColor_ReachesTheHighlightPreviews()
        {
            var (session, vm) = NewInspector(out _);
            var cursor = session.AddCursorTrack();
            session.Select(cursor.Id);

            Assert.Equal(SelectedItemViewModel.DefaultCursorClickColor, vm.CursorClickColor);
            Assert.All(CursorItems(session), c => Assert.Equal(vm.CursorClickColor, c.ClickColor));

            session.EditItem(cursor.Id, i => ((CursorContent)i.Content).ClickColor = 0x8000FF00,
                "test", structural: false, origin: new object());

            Assert.Equal(0x8000FF00u, vm.CursorClickColor);
        }

        [Fact]
        public void CursorSizeAndClicks_WriteTheWholeRow()
        {
            var (session, vm) = NewInspector(out _);
            var cursor = session.AddCursorTrack();
            session.Select(cursor.Id);

            Assert.Equal(1.0, vm.CursorSize);
            Assert.Equal("none", vm.CursorClickAnimation.Value);

            vm.CursorSize = 2.0;
            vm.CursorClickAnimation = SelectedItemViewModel.ClickAnimationOptions.First(o => o.Value == "ripple");

            Assert.All(CursorItems(session), c =>
            {
                Assert.Equal(2.0, c.Size);
                Assert.Equal("ripple", c.ClickAnimation);
            });
        }

        [Fact]
        public void HighlightDials_WriteTheWholeRowAndClampToTheModelsRange()
        {
            var (session, vm) = NewInspector(out _);
            var cursor = session.AddCursorTrack();
            session.Select(cursor.Id);

            Assert.Equal(1.0, vm.CursorHoldSize);
            Assert.Equal(1.0, vm.CursorClickSize);
            Assert.Equal(1.0, vm.CursorAnimationSpeed);

            vm.CursorHoldSize = 2.5;
            vm.CursorClickSize = 0.5;
            vm.CursorAnimationSpeed = 3.0;

            Assert.All(CursorItems(session), c =>
            {
                Assert.Equal(2.5, c.HoldSize);
                Assert.Equal(0.5, c.ClickSize);
                Assert.Equal(3.0, c.AnimationSpeed);
            });

            // the spinners offer exactly the range Project.Validate accepts, so a clamped write
            // can never produce a project that fails validation
            vm.CursorHoldSize = 99;
            vm.CursorClickSize = -1;
            vm.CursorAnimationSpeed = Double.NaN;

            Assert.Equal(SelectedItemViewModel.MaxHighlightFactor, vm.CursorHoldSize);
            Assert.Equal(SelectedItemViewModel.MinHighlightFactor, vm.CursorClickSize);
            Assert.Equal(SelectedItemViewModel.MinHighlightFactor, vm.CursorAnimationSpeed);
            Assert.Equal(CursorContent.MinHighlightFactor, SelectedItemViewModel.MinHighlightFactor);
            Assert.Equal(CursorContent.MaxHighlightFactor, SelectedItemViewModel.MaxHighlightFactor);
        }

        [Fact]
        public void HighlightDials_AreHiddenWhileNothingIsDrawn()
        {
            // the three rows bind their IsVisible to this: "none" draws no highlight, so its size
            // and speed dials leave the panel rather than sit there inert
            var (session, vm) = NewInspector(out _);
            var cursor = session.AddCursorTrack();
            session.Select(cursor.Id);

            Assert.Equal("none", vm.CursorClickAnimation.Value);
            Assert.False(vm.CursorHighlightEnabled);

            vm.CursorClickAnimation = SelectedItemViewModel.ClickAnimationOptions.First(o => o.Value == "pulse");
            Assert.True(vm.CursorHighlightEnabled);

            vm.CursorClickAnimation = SelectedItemViewModel.DefaultClickAnimationOption;
            Assert.False(vm.CursorHighlightEnabled);
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
                content.Style = "native";
                content.Size = 1.75;
                content.ClickAnimation = "pulse";
                content.HoldSize = 1.5;
                content.ClickSize = 2.5;
                content.AnimationSpeed = 0.75;
            }, "test", structural: false, origin: new object());

            Assert.Equal("native", vm.CursorStyle.Value);
            Assert.Equal(1.75, vm.CursorSize);
            Assert.Equal("pulse", vm.CursorClickAnimation.Value);
            Assert.True(vm.CursorHighlightEnabled);
            Assert.Equal(1.5, vm.CursorHoldSize);
            Assert.Equal(2.5, vm.CursorClickSize);
            Assert.Equal(0.75, vm.CursorAnimationSpeed);
        }

        // ------------------------------------------------------------------------------ keyboard

        [Fact]
        public void KeyboardSpinners_WriteTheWholeRow()
        {
            var (session, vm) = NewInspector(out _);
            var keys = session.AddKeyboardTrack();
            session.Select(keys.Id);

            Assert.Equal(2, KeyboardItems(session).Length);
            Assert.Equal(40, vm.KeyboardFontSize);
            Assert.Equal(1000, vm.KeyboardLingerMs);
            Assert.Equal(1000, vm.KeyboardPauseBreakMs);

            // every one of these differs from the default, or the write would coalesce away and
            // the fan-out would go untested
            vm.KeyboardFontSize = 56;
            vm.KeyboardLingerMs = 500;
            vm.KeyboardPauseBreakMs = 2000;

            Assert.All(KeyboardItems(session), k =>
            {
                Assert.Equal(56, k.FontSize);
                Assert.Equal(500, k.LingerMs);
                Assert.Equal(2000, k.PauseBreakMs);
            });
        }

        [Fact]
        public void KeyboardColorWells_WriteTheWholeRow()
        {
            var (session, vm) = NewInspector(out _);
            var keys = session.AddKeyboardTrack();
            session.Select(keys.Id);

            Assert.Equal(SelectedItemViewModel.DefaultKeyboardTextColorHex, vm.KeyboardTextColorHex);
            Assert.Equal(SelectedItemViewModel.DefaultKeyboardBackColorHex, vm.KeyboardBackColorHex);

            vm.KeyboardTextColorHex = "#FF11AA33";
            vm.KeyboardBackColorHex = "#402244FF";

            Assert.All(KeyboardItems(session), k =>
            {
                Assert.Equal(0xFF11AA33u, k.TextColor);
                Assert.Equal(0x402244FFu, k.BackgroundColor);
            });

            // a #RRGGBB literal is opaque; half-typed values stay in the box unwritten
            vm.KeyboardTextColorHex = "#00FF00";
            Assert.All(KeyboardItems(session), k => Assert.Equal(0xFF00FF00u, k.TextColor));

            vm.KeyboardTextColorHex = "#00FF0";
            Assert.Equal("#00FF0", vm.KeyboardTextColorHex);
            Assert.All(KeyboardItems(session), k => Assert.Equal(0xFF00FF00u, k.TextColor));
        }

        [Fact]
        public void KeyboardSpinners_ClampToTheModelsRanges()
        {
            var (session, vm) = NewInspector(out _);
            var keys = session.AddKeyboardTrack();
            session.Select(keys.Id);

            vm.KeyboardFontSize = 5000;
            vm.KeyboardLingerMs = 99_999;
            vm.KeyboardPauseBreakMs = Double.NaN;

            Assert.Equal(SelectedItemViewModel.MaxKeyboardFontSize, vm.KeyboardFontSize);
            Assert.Equal(SelectedItemViewModel.MaxKeyboardMs, vm.KeyboardLingerMs);
            Assert.Equal(0, vm.KeyboardPauseBreakMs);

            var content = (KeyboardContent)Live(session, keys.Id).Content;
            Assert.Equal(SelectedItemViewModel.MaxKeyboardFontSize, content.FontSize);
            Assert.Equal((int)SelectedItemViewModel.MaxKeyboardMs, content.LingerMs);
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
                content.PauseBreakMs = 333;
                content.TextColor = 0xFF010203;
                content.BackgroundColor = 0x11223344;
            }, "test", structural: false, origin: new object());

            Assert.Equal(64, vm.KeyboardFontSize);
            Assert.Equal(111, vm.KeyboardLingerMs);
            Assert.Equal(333, vm.KeyboardPauseBreakMs);
            Assert.Equal("#FF010203", vm.KeyboardTextColorHex);
            Assert.Equal("#11223344", vm.KeyboardBackColorHex);
        }

        /// <summary>The animation section stays on for a keystroke overlay — it is what animates
        /// each row — and the factory's defaults are the ones the user sees.</summary>
        [Fact]
        public void KeyboardRows_ArriveWithTheFactorysRowAnimation()
        {
            var (session, vm) = NewInspector(out _);
            var keys = session.AddKeyboardTrack();
            session.Select(keys.Id);

            Assert.True(vm.ShowTransitions);
            Assert.Equal(TransitionKind.SlideUp, vm.EntryKind);
            Assert.Equal(TransitionKind.Fade, vm.ExitKind);
            Assert.Equal(300, vm.EntryDurationMs);
            Assert.Equal(300, vm.ExitDurationMs);

            // transitions are per item, not per overlay row — the same rule every other item
            // kind's animation follows, and the only keystroke editor that does not fan out
            vm.EntryKind = TransitionKind.Fade;
            Assert.Equal(TransitionKind.Fade, Live(session, keys.Id).Entry.Kind);
        }

        // ------------------------------------------------------------------------------- options

        [Fact]
        public void OptionLists_MirrorTheModelsOwnValues()
        {
            Assert.Equal(CursorContent.Styles.ToArray(),
                SelectedItemViewModel.CursorStyleOptions.Select(o => o.Value).ToArray());
            Assert.Equal(CursorContent.ClickAnimations.ToArray(),
                SelectedItemViewModel.ClickAnimationOptions.Select(o => o.Value).ToArray());

            // the tile pickers select by reference, so the defaults must be list members
            Assert.Contains(SelectedItemViewModel.DefaultCursorStyleOption, SelectedItemViewModel.CursorStyleOptions);
            Assert.Contains(SelectedItemViewModel.DefaultClickAnimationOption, SelectedItemViewModel.ClickAnimationOptions);
            Assert.Equal("vision", SelectedItemViewModel.DefaultCursorStyleOption.Value);
            Assert.Equal("none", SelectedItemViewModel.DefaultClickAnimationOption.Value);
            Assert.Equal("Vision", SelectedItemViewModel.DefaultCursorStyleOption.Label);
            Assert.Equal("Native", SelectedItemViewModel.CursorStyleOptions[0].Label);

            // every style tile but native draws real artwork; native's tile is the outline
            // stand-in, so it is the one — and only one — the asset table has nothing for
            Assert.All(SelectedItemViewModel.CursorStyleOptions, o =>
                Assert.Equal(o.Value == SelectedItemViewModel.NativeCursorStyle,
                    CursorAssets.TryGet(o.Value, CursorAssets.KindArrow) == null));

            // the highlight tiles animate off ClickHighlight: "none" is the one that never does
            Assert.Equal(new[] { "None", "Ripple", "Pulse" },
                SelectedItemViewModel.ClickAnimationOptions.Select(o => o.Label).ToArray());
            Assert.All(SelectedItemViewModel.ClickAnimationOptions, o =>
                Assert.Equal(o.Value != "none", ClickHighlight.TryParse(o.Value, out _)));
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

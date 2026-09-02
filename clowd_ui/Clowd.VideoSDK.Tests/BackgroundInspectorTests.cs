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
    /// The inspector over a wallpaper item: which sections it lights, and the two-level style then
    /// theme picker — where the second level comes from, when it is there at all, and what happens
    /// to a stored theme when the style above it changes. Framework-free like
    /// <see cref="InputOverlayInspectorTests"/>: the view model is a plain object, so none of this
    /// needs Avalonia.
    /// </summary>
    public class BackgroundInspectorTests
    {
        private static long Ms(long ms) => ms * TimeSpan.TicksPerMillisecond;

        /// <summary>A one-item project holding a wallpaper on its own row, selected — the state the
        /// panel is in the moment the tool-strip button has been pressed.</summary>
        private static (EditorSession Session, SelectedItemViewModel Vm, Guid ItemId) NewInspector(
            string style = null, string theme = null)
        {
            var track = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Background", Order = 0 };
            var item = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = track.Id,
                TimelineStartTicks = 0,
                DurationTicks = Ms(5_000),
                Content = style == null && theme == null
                    ? new BackgroundContent()
                    : new BackgroundContent { Style = style, Theme = theme },
            };
            var project = new Project
            {
                Output = new OutputSettings { WidthPx = 1920, HeightPx = 1080, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
                Tracks = { track },
                Items = { item },
            };

            var session = new EditorSession(project, null, null);
            var vm = new SelectedItemViewModel { Session = session };
            session.Select(item.Id);
            return (session, vm, item.Id);
        }

        private static Item LiveItem(EditorSession session, Guid id)
            => session.Project.Items.First(i => i.Id == id);

        private static BackgroundContent Live(EditorSession session, Guid id)
            => (BackgroundContent)LiveItem(session, id).Content;

        private static SelectedItemViewModel.NamedOption Style(string id)
            => SelectedItemViewModel.BackgroundStyleOptions.First(o => o.Value == id);

        // ------------------------------------------------------------------ sections

        [Fact]
        public void ABackgroundItem_LightsItsOwnSection_AndEveryPictureSection()
        {
            var (_, vm, _) = NewInspector();

            Assert.True(vm.ShowBackground);
            Assert.Equal("Background", vm.SubjectKind);

            // placed, sized, rotated and faded like any picture
            Assert.True(vm.ShowTransform);
            Assert.True(vm.ShowScale);
            Assert.True(vm.ShowRotation);
            // picture-shaped where the composer agrees: a mask and a surround, both of which
            // FrameComposer.DrawBackground really applies
            Assert.True(vm.ShowMask);
            Assert.True(vm.ShowSurround);
            // but NOT an aspect preset, a fit mode or a crop: a wallpaper has no ratio of its own
            // (DrawBackground boxes it by Scale/ScaleY against the canvas and cover-fits the art
            // into that box, reading neither Transform.Aspect nor Transform.Crop), so none of
            // those are choices for it. ShowCrop gates the panel's whole ASPECT RATIO + fit mode
            // + CROP block; see the free-resize tests below for what the item has instead.
            Assert.False(vm.ShowCrop);
            // and a backdrop that fades in is a real edit
            Assert.True(vm.ShowTransitions);
            Assert.True(vm.ShowTrackHidden);
        }

        // ------------------------------------------------------------------ free resize

        /// <summary>The panel's ASPECT RATIO section is the only route to the "Unlocked" tile, and
        /// Unlocked is what puts the four edge handles on the preview gizmo (the window forwards
        /// <c>AspectUnlocked</c> to <c>TransformGizmoControl.FreeResize</c>). A wallpaper gets no
        /// section, so it has to report the tile without one: it is Unlocked permanently, which is
        /// what lets the user squash and stretch it from all eight handles.</summary>
        [Fact]
        public void ABackgroundItem_ReadsAsUnlocked_SoTheGizmoOffersEdgeHandles()
        {
            var (session, vm, id) = NewInspector();

            Assert.True(vm.AspectUnlocked);
            Assert.False(vm.AspectOriginal);
            Assert.False(vm.AspectSelected);

            // the state a picture would be in after the Unlocked tile: an explicit height, no
            // ratio held, so the ratio-bearing tiles are all off and stay off
            Assert.Null(LiveItem(session, id).Transform?.Aspect);
            Assert.False(vm.Aspect169 || vm.Aspect11 || vm.Aspect45 || vm.Aspect32 || vm.Aspect43
                         || vm.AspectCustom);
        }

        /// <summary>Selection changes are what the window listens to: moving from a ratio-locked
        /// picture onto a wallpaper must raise <c>AspectUnlocked</c>, or the gizmo keeps the
        /// picture's four corners.</summary>
        [Fact]
        public void SelectingABackground_AfterALockedPicture_RaisesAspectUnlocked()
        {
            var (session, vm, id) = NewInspector();
            // a text card: ratio-locked (Original) like any freshly added picture
            var text = session.AddText(0, Ms(5_000));
            session.Select(text.Id);
            Assert.True(vm.AspectOriginal);
            Assert.False(vm.AspectUnlocked);

            var raised = new System.Collections.Generic.List<string>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            session.Select(id);

            Assert.True(vm.AspectUnlocked);
            Assert.Contains(nameof(vm.AspectUnlocked), raised);
        }

        /// <summary>Neither the ASPECT RATIO section (with its tiles) nor the Fill/Stretch pair
        /// under it is a choice for a wallpaper: the art always fills the item box, whatever ratio
        /// the handles drag it to. The section and the fit-mode grid inside it are gated on
        /// <c>ShowCrop</c>, and the fit mode is additionally inert (<c>AspectSelected</c> false,
        /// which disables the pair) and never reads as Stretch.</summary>
        [Fact]
        public void ABackgroundItem_OffersNeitherTheAspectSection_NorAFitMode()
        {
            var (session, vm, id) = NewInspector();

            Assert.False(vm.ShowCrop);
            Assert.False(vm.ShowCustomAspect);
            Assert.False(vm.AspectSelected);
            Assert.False(vm.AspectStretch);
            Assert.True(vm.AspectFill);
            Assert.False(vm.CropModeActive);

            // and the model agrees: nothing ratio-shaped is stored for it
            var transform = LiveItem(session, id).Transform;
            Assert.Null(transform?.Aspect);
            Assert.False(transform?.AspectStretch ?? false);
        }

        /// <summary>A wallpaper added the way the tool-strip adds one carries the explicit height
        /// that spells Unlocked in the model (see <c>EditorSession.AddBackground</c>), so the
        /// PLACEMENT section shows both axes from the start, exactly as it does for a picture the
        /// moment the Unlocked tile is picked.</summary>
        [Fact]
        public void ABackgroundAddedByTheSession_ShowsWidthAndHeightRows()
        {
            var (session, vm, _) = NewInspector();
            var added = session.AddBackground(0, Ms(5_000));
            session.Select(added.Id);

            Assert.True(vm.AspectUnlocked);
            Assert.True(vm.ShowScaleHeight);
            Assert.Equal("Width", vm.ScaleLabel);
            Assert.Equal(1.0, vm.Scale);
            Assert.Equal(1.0, vm.ScaleHeight);
        }

        /// <summary>A wallpaper without the explicit height still reads as Unlocked. A project
        /// that reaches a session no longer has one of these (<c>Project.Normalize</c> fills the
        /// height when the session opens it, see <see cref="BackgroundFreeResizeTests"/>), so the
        /// live item is stripped of it by hand here: this pins the panel's own content rule, the
        /// belt to that migration's braces, for a model that never went through it. The free
        /// resize is not a state a wallpaper can be out of, so the panel does not wait for the
        /// model to say so.</summary>
        [Fact]
        public void ABackgroundWithoutAnExplicitHeight_StillReadsAsUnlocked()
        {
            var (session, vm, id) = NewInspector();
            LiveItem(session, id).Transform.ScaleY = null;
            session.Select(Guid.Empty);
            session.Select(id);
            Assert.Null(LiveItem(session, id).Transform?.ScaleY);

            Assert.True(vm.AspectUnlocked);
            Assert.False(vm.ShowCrop);

            // and it is offered Height % anyway, showing the height the composer falls back to
            // for a missing ScaleY (Scale, i.e. the canvas height at Scale 1) …
            Assert.True(vm.ShowScaleHeight);
            Assert.Equal("Width", vm.ScaleLabel);
            Assert.Equal(1.0, vm.ScaleHeight);

            // … and the first edit of it is what writes the explicit height into the model
            vm.ScaleHeight = 0.5;
            Assert.Equal(0.5, LiveItem(session, id).Transform.ScaleY);
        }

        /// <summary>The two size fields are percentages OF THE OUTPUT CANVAS, which is the model's
        /// own storage (<c>Transform.Scale</c>/<c>ScaleY</c> are canvas fractions the composer
        /// multiplies by the canvas size), not pixels the panel converts: 100% x 100% is the
        /// canvas at any resolution, 120% x 80% is a squashed backdrop, and a resolution change
        /// rewrites nothing, so both keep reading the same two numbers afterwards.</summary>
        [Fact]
        public void WidthAndHeight_AreCanvasPercentages_AndSurviveAResolutionChange()
        {
            var (session, vm, _) = NewInspector();
            var added = session.AddBackground(0, Ms(5_000));
            session.Select(added.Id);

            // the insertion default: exactly the output canvas
            Assert.Equal(1.0, vm.Scale);
            Assert.Equal(1.0, vm.ScaleHeight);

            // independent axes: the aspect is unlocked, so one does not drag the other along
            vm.Scale = 1.2;
            vm.ScaleHeight = 0.8;
            var transform = LiveItem(session, added.Id).Transform;
            Assert.Equal(1.2, transform.Scale);
            Assert.Equal(0.8, transform.ScaleY);

            // the resolution picker's edit: the item is untouched and the panel still shows the
            // same percentages, now of the new canvas
            Assert.True(session.SetOutputSize(1280, 720));
            transform = LiveItem(session, added.Id).Transform;
            Assert.Equal(1.2, transform.Scale);
            Assert.Equal(0.8, transform.ScaleY);
            Assert.Equal(1.2, vm.Scale);
            Assert.Equal(0.8, vm.ScaleHeight);
            Assert.True(vm.ShowScaleHeight);
        }

        [Fact]
        public void ABackgroundItem_LeavesTheSectionsThatCannotApply_Alone()
        {
            var (_, vm, _) = NewInspector();

            // blur / background removal read a decoded stream; a wallpaper has none
            Assert.False(vm.ShowEffect);
            Assert.False(vm.ShowSpeed);
            Assert.False(vm.ShowSpeedEffect);
            Assert.False(vm.ShowZoomEffect);
            Assert.False(vm.ShowRamp);
            Assert.False(vm.ShowText);
            Assert.False(vm.ShowAudio);
            Assert.False(vm.ShowCursorTrack);
            Assert.False(vm.ShowKeyboardTrack);
        }

        [Fact]
        public void NothingSelected_HidesTheSection()
        {
            var (session, vm, _) = NewInspector();
            session.Select(Guid.Empty);

            Assert.False(vm.ShowBackground);
        }

        // ------------------------------------------------------------------ level 1

        [Fact]
        public void TheStyleTiles_AreTheCatalogsStyles_InTheCatalogsOrder()
        {
            Assert.Equal(
                BackgroundCatalog.Styles.Select(s => s.Id).ToArray(),
                SelectedItemViewModel.BackgroundStyleOptions.Select(o => o.Value).ToArray());
            Assert.Equal(
                BackgroundCatalog.Styles.Select(s => s.Label).ToArray(),
                SelectedItemViewModel.BackgroundStyleOptions.Select(o => o.Label).ToArray());
        }

        /// <summary>A tile reads as picked by reference equality, so the static list must be the
        /// only place these instances come from.</summary>
        [Fact]
        public void TheSelectedStyleTile_IsTheListsOwnInstance()
        {
            var (_, vm, _) = NewInspector();

            Assert.Same(Style(BackgroundCatalog.DefaultStyle), vm.BackgroundStyle);
        }

        [Fact]
        public void AFreshBackground_ReadsAsBigSurOnItsDefaultTheme()
        {
            var (_, vm, _) = NewInspector();

            Assert.Equal("big-sur", vm.BackgroundStyle.Value);
            Assert.True(vm.BackgroundThemesVisible);
            Assert.Equal(new[] { "default", "teal", "violet", "amber" },
                vm.BackgroundThemeOptions.Select(o => o.Value).ToArray());
            Assert.Equal("default", vm.BackgroundTheme.Value);
        }

        [Fact]
        public void PickingAStyle_WritesItToTheModel()
        {
            var (session, vm, id) = NewInspector();

            vm.BackgroundStyle = Style("moving-blob");

            Assert.Equal("moving-blob", Live(session, id).Style);
            Assert.Equal("moving-blob", vm.BackgroundStyle.Value);
        }

        [Fact]
        public void AStyleThisBuildDoesNotKnow_ReadsAsTheStyleThatIsDrawn()
        {
            // a project from a newer editor: the composer draws the default style for it, and the
            // panel has to agree with the picture rather than show an empty row
            var (_, vm, _) = NewInspector(style: "holographic-nebula");

            Assert.Equal(BackgroundCatalog.DefaultStyle, vm.BackgroundStyle.Value);
            Assert.True(vm.BackgroundThemesVisible);
        }

        // ------------------------------------------------------------------ level 2

        [Fact]
        public void AStyleWithOneLook_ShowsNoThemeRowAtAll()
        {
            var (_, vm, _) = NewInspector();

            vm.BackgroundStyle = Style("explode");

            Assert.False(vm.BackgroundThemesVisible);
            Assert.Empty(vm.BackgroundThemeOptions);
            Assert.Null(vm.BackgroundTheme);
        }

        [Fact]
        public void EveryStyleWithMoreThanOneTheme_ShowsThatManyTiles()
        {
            var (_, vm, _) = NewInspector();

            foreach (var style in BackgroundCatalog.Styles)
            {
                vm.BackgroundStyle = Style(style.Id);

                Assert.Equal(style.Themes.Count >= 2, vm.BackgroundThemesVisible);
                Assert.Equal(style.Themes.Select(t => t.Id).ToArray(),
                    vm.BackgroundThemeOptions.Select(o => o.Value).ToArray());
                if (style.Themes.Count >= 2)
                    Assert.Same(vm.BackgroundThemeOptions[0], vm.BackgroundTheme);
            }
        }

        [Fact]
        public void PickingATheme_WritesItToTheModel()
        {
            var (session, vm, id) = NewInspector();

            vm.BackgroundTheme = vm.BackgroundThemeOptions.First(o => o.Value == "violet");

            Assert.Equal("violet", Live(session, id).Theme);
            Assert.Equal("violet", vm.BackgroundTheme.Value);
        }

        /// <summary>The crux of the two-level picker: changing the style re-populates the theme row
        /// and re-resolves which of its tiles reads as picked, and does it WITHOUT writing the
        /// model — so a theme survives a trip through a style that does not offer it.</summary>
        [Fact]
        public void ChangingTheStyle_RepopulatesTheThemeRow_AndNeverWritesTheTheme()
        {
            var (session, vm, id) = NewInspector();

            vm.BackgroundStyle = Style("moving-blob");
            vm.BackgroundTheme = vm.BackgroundThemeOptions.First(o => o.Value == "ember");
            Assert.Equal("ember", Live(session, id).Theme);

            // a sibling generative style: same palette namespace, so the pick is still the pick
            vm.BackgroundStyle = Style("stacked-waves");
            Assert.Equal("ember", vm.BackgroundTheme.Value);
            Assert.Equal("ember", Live(session, id).Theme);

            // a style that has never heard of "ember": it READS as that style's first theme, and
            // that is exactly what the composer draws, but nothing is written
            vm.BackgroundStyle = Style("gradient");
            Assert.Equal("sunrise", vm.BackgroundTheme.Value);
            Assert.Equal("ember", Live(session, id).Theme);
            Assert.Equal("sunrise", BackgroundCatalog.ResolveTheme("gradient", "ember"));

            // and coming back restores it without the user picking it again
            vm.BackgroundStyle = Style("moving-blob");
            Assert.Equal("ember", vm.BackgroundTheme.Value);
        }

        [Fact]
        public void TheThemeRowVanishesAndReturns_WithTheStyleAboveIt()
        {
            var (_, vm, _) = NewInspector();

            Assert.True(vm.BackgroundThemesVisible);
            vm.BackgroundStyle = Style("explode");
            Assert.False(vm.BackgroundThemesVisible);
            vm.BackgroundStyle = Style("monterey");
            Assert.True(vm.BackgroundThemesVisible);
            Assert.Equal(new[] { "light", "dark" },
                vm.BackgroundThemeOptions.Select(o => o.Value).ToArray());
        }

        /// <summary>Nothing in this view model raises dependents on its own, so the style setter
        /// has to do it by hand; a missed one leaves the theme row stale until the next selection
        /// change, which is exactly the bug this catches.</summary>
        [Fact]
        public void TheStyleSetter_RaisesEveryDependentOfTheThemeRow()
        {
            var (_, vm, _) = NewInspector();
            var raised = new System.Collections.Generic.List<string>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            vm.BackgroundStyle = Style("explode");

            Assert.Contains(nameof(vm.BackgroundStyle), raised);
            Assert.Contains(nameof(vm.BackgroundThemeOptions), raised);
            Assert.Contains(nameof(vm.BackgroundThemesVisible), raised);
            Assert.Contains(nameof(vm.BackgroundTheme), raised);
        }

        // ------------------------------------------------------------------ round trip and undo

        [Fact]
        public void AModelEdit_ReadsBackIntoBothLevels()
        {
            var (session, vm, id) = NewInspector();

            // somebody else's write (a timeline paste, an undo, another panel): origin is not the
            // view model, so it re-syncs rather than skipping its own echo
            session.EditItem(id, i => ((BackgroundContent)i.Content).Style = "monterey", "test", false, null);
            session.EditItem(id, i => ((BackgroundContent)i.Content).Theme = "dark", "test2", false, null);

            Assert.Equal("monterey", vm.BackgroundStyle.Value);
            Assert.Equal("dark", vm.BackgroundTheme.Value);
            Assert.True(vm.BackgroundThemesVisible);
        }

        [Fact]
        public void EachPick_IsOneUndoEntry()
        {
            var (session, vm, id) = NewInspector();
            Assert.False(session.CanUndo); // selecting is not an edit

            vm.BackgroundStyle = Style("gradient");
            vm.BackgroundTheme = vm.BackgroundThemeOptions.First(o => o.Value == "abyss");

            session.Undo();
            Assert.Equal("gradient", Live(session, id).Style);
            Assert.Null(Live(session, id).Theme);

            session.Undo();
            Assert.Equal("big-sur", Live(session, id).Style);
            Assert.False(session.CanUndo);
        }

        [Fact]
        public void UndoOfAStylePick_ShowsUpInThePanel()
        {
            var (session, vm, _) = NewInspector();

            vm.BackgroundStyle = Style("layered-steps");
            session.Undo();

            Assert.Equal("big-sur", vm.BackgroundStyle.Value);
            Assert.Equal(new[] { "default", "teal", "violet", "amber" },
                vm.BackgroundThemeOptions.Select(o => o.Value).ToArray());
        }

        // ---------------------------------------------------------------- the solid style

        /// <summary>The solid style puts a color well where a wallpaper's theme row goes: it
        /// offers no themes, so the two are never both up, and the well writes the item.</summary>
        [Fact]
        public void PickingTheSolidStyle_SwapsTheThemeRowForAColorWell()
        {
            var (session, vm, id) = NewInspector();
            Assert.False(vm.ShowBackgroundColor);
            Assert.True(vm.BackgroundThemesVisible);

            vm.BackgroundStyle = Style(BackgroundCatalog.SolidStyle);
            Assert.True(vm.ShowBackgroundColor);
            Assert.False(vm.BackgroundThemesVisible);
            Assert.Empty(vm.BackgroundThemeOptions);
            // a fresh background carries Clowd blue, so the well opens on what is drawn
            Assert.Equal(BackgroundContent.DefaultColor, vm.BackgroundColorHex);

            vm.BackgroundColorHex = "#FF112233";
            Assert.Equal("#FF112233", Live(session, id).Color);
            Assert.Equal(BackgroundCatalog.SolidStyle, Live(session, id).Style);

            // and it goes again when a wallpaper is picked
            vm.BackgroundStyle = Style("monterey");
            Assert.False(vm.ShowBackgroundColor);
            Assert.True(vm.BackgroundThemesVisible);
        }

        /// <summary>A half-typed color stays in the box without reaching the model, and the color
        /// survives a trip through a wallpaper style — the stickiness the theme keeps.</summary>
        [Fact]
        public void AHalfTypedColorIsNotWritten_AndThePickSurvivesAnotherStyle()
        {
            var (session, vm, id) = NewInspector(BackgroundCatalog.SolidStyle);
            vm.BackgroundColorHex = "#FF00FF00";
            Assert.Equal("#FF00FF00", Live(session, id).Color);

            vm.BackgroundColorHex = "#FF00F";
            Assert.Equal("#FF00F", vm.BackgroundColorHex);
            Assert.Equal("#FF00FF00", Live(session, id).Color);

            vm.BackgroundColorHex = "#FF00FF00";
            vm.BackgroundStyle = Style("gradient");
            Assert.Equal("#FF00FF00", Live(session, id).Color);
            vm.BackgroundStyle = Style(BackgroundCatalog.SolidStyle);
            Assert.Equal("#FF00FF00", vm.BackgroundColorHex);
        }

        /// <summary>Every one of the panel's pairs has to be drawable, or a tile is a black box: the
        /// tile hands exactly these two ids to the same renderer the composer calls. The solid
        /// style is the one tile with no scene behind it — it draws the color instead, and the
        /// tile passes the panel's own down.</summary>
        [Fact]
        public void EveryTileThePickerCanShow_HasArtworkBehindIt()
        {
            foreach (var option in SelectedItemViewModel.BackgroundStyleOptions)
            {
                var style = BackgroundCatalog.Find(option.Value);
                if (style.IsSolid)
                {
                    Assert.Null(BackgroundRenderer.GetScene(option.Value, null));
                    Assert.Empty(style.Themes);
                    continue;
                }

                Assert.NotNull(BackgroundRenderer.GetScene(option.Value, null));
                foreach (var theme in style.Themes)
                    Assert.NotNull(BackgroundRenderer.GetScene(option.Value, theme.Id));
            }
        }
    }
}

using System;
using System.Linq;
using System.Text.RegularExpressions;
using Clowd.UI.VideoEditor;
using Clowd.UI.VideoEditor.Inspector;
using Clowd.UI.VideoEditor.Timeline;
using Clowd.VideoSDK.Editing;
using Clowd.VideoSDK.Model;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// <see cref="ObscureContent"/> outside the pixels (those are <see cref="ObscureComposeTests"/>):
    /// the file format, validation, the timeline operations every item goes through, the session's
    /// add, and the editor's reading of it — the inspector's sections, the free resize and the
    /// timeline row. Framework-free like <see cref="BackgroundFreeResizeTests"/>.
    /// </summary>
    [Collection("EditorSession clipboard")] // the copy/paste tests share its one static clipboard
    public class ObscureContentTests
    {
        private static long Ms(long ms) => ms * TimeSpan.TicksPerMillisecond;

        private static Project EmptyProject() => new Project
        {
            Output = new OutputSettings { WidthPx = 1920, HeightPx = 1080, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
        };

        private static (Project Project, Track Track, Item Item) OneBox(ObscureContent content = null)
        {
            var project = EmptyProject();
            var track = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Overlay", Order = 0 };
            var item = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = track.Id,
                TimelineStartTicks = 0,
                DurationTicks = Ms(5_000),
                Content = content ?? new ObscureContent(),
                Transform = new Transform { Scale = 0.25, ScaleY = 0.25 },
            };
            project.Tracks.Add(track);
            project.Items.Add(item);
            return (project, track, item);
        }

        private static Transition Ramp(long ms) =>
            new Transition { Kind = TransitionKind.Ramp, DurationTicks = Ms(ms) };

        // ----------------------------------------------------------------------- file format

        [Fact]
        public void Round_trips_through_the_project_file()
        {
            var (project, _, item) = OneBox(new ObscureContent { Kind = ObscureKind.Blur, Strength = 12.5 });
            item.Entry = Ramp(300);
            item.Exit = Ramp(450);

            var json = project.ToJson();
            Assert.Contains("\"$type\": \"obscure\"", json);
            Assert.Contains("\"Blur\"", json); // enums as strings: the names are wire contract

            var back = Project.FromJson(json).Items.Single();
            var content = Assert.IsType<ObscureContent>(back.Content);
            Assert.Equal(ObscureKind.Blur, content.Kind);
            Assert.Equal(12.5, content.Strength);
            Assert.Equal(TransitionKind.Ramp, back.Entry.Kind);
            Assert.Equal(Ms(300), back.Entry.DurationTicks);
            Assert.Equal(Ms(450), back.Exit.DurationTicks);
            Assert.Equal(0.25, back.Transform.ScaleY);
        }

        [Fact]
        public void A_bare_obscure_entry_reads_with_the_image_editor_defaults()
        {
            var (project, _, _) = OneBox();
            var json = Regex.Replace(project.ToJson(), @",\s*""(Kind|Strength)"": (""Mosaic""|8)", "");
            Assert.DoesNotContain("\"Strength\"", json);
            Assert.Contains("\"$type\": \"obscure\"", json);
            var content = Assert.IsType<ObscureContent>(Project.FromJson(json).Items.Single().Content);

            Assert.Equal(ObscureKind.Mosaic, content.Kind);
            Assert.Equal(ObscureContent.DefaultStrength, content.Strength);
        }

        [Fact]
        public void Clone_is_deep_and_equal()
        {
            var original = new ObscureContent { Kind = ObscureKind.Blackout, Strength = 20 };
            var copy = Assert.IsType<ObscureContent>(original.Clone());

            Assert.NotSame(original, copy);
            Assert.Equal(original.Kind, copy.Kind);
            Assert.Equal(original.Strength, copy.Strength);
        }

        [Fact]
        public void Normalize_writes_out_the_free_height()
        {
            var (project, _, item) = OneBox();
            item.Transform = new Transform { Scale = 0.4 };

            project.Normalize();

            Assert.Equal(0.4, item.Transform.ScaleY);
        }

        // ------------------------------------------------------------------------ validation

        [Fact]
        public void A_valid_box_validates()
        {
            var (project, _, item) = OneBox();
            item.Entry = Ramp(300);
            item.Exit = Ramp(300);

            Assert.Empty(project.Validate());
        }

        [Theory]
        [InlineData(0.5)]
        [InlineData(201)]
        [InlineData(double.NaN)]
        public void Strength_outside_its_range_is_rejected(double strength)
        {
            var (project, _, _) = OneBox(new ObscureContent { Strength = strength });

            Assert.Contains(project.Validate(), e => e.Contains("obscure strength"));
        }

        [Fact]
        public void Only_ramps_animate_a_box()
        {
            // a fade-in is fine as a ramp; a slide or a wipe would expose what the box hides
            var (project, _, item) = OneBox();
            item.Entry = new Transition { Kind = TransitionKind.SlideLeft, DurationTicks = Ms(300) };
            item.Exit = new Transition { Kind = TransitionKind.Fade, DurationTicks = Ms(300) };

            var errors = project.Validate();
            Assert.Contains(errors, e => e.Contains("non-ramp entry"));
            Assert.Contains(errors, e => e.Contains("non-ramp exit"));
        }

        [Theory]
        [InlineData(TrackKind.Audio)]
        [InlineData(TrackKind.Effect)]
        public void A_box_belongs_on_a_video_row(TrackKind kind)
        {
            var (project, track, _) = OneBox();
            track.Kind = kind;

            Assert.Contains(project.Validate(), e => e.Contains("ObscureContent on"));
        }

        // ------------------------------------------------------------------- timeline ops

        [Fact]
        public void Split_halves_are_independent_boxes_keeping_one_ramp_each()
        {
            var (project, _, item) = OneBox(new ObscureContent { Kind = ObscureKind.Blur, Strength = 9 });
            item.Entry = Ramp(300);
            item.Exit = Ramp(400);

            Assert.True(TimelineOps.Split(project, item.Id, Ms(2_000)));

            var halves = project.Items.OrderBy(i => i.TimelineStartTicks).ToList();
            Assert.Equal(2, halves.Count);
            var left = Assert.IsType<ObscureContent>(halves[0].Content);
            var right = Assert.IsType<ObscureContent>(halves[1].Content);
            Assert.NotSame(left, right);
            Assert.Equal((ObscureKind.Blur, 9.0), (right.Kind, right.Strength));
            Assert.NotNull(halves[0].Entry);
            Assert.Null(halves[0].Exit);
            Assert.Null(halves[1].Entry);
            Assert.NotNull(halves[1].Exit);
            Assert.Empty(project.Validate());
        }

        [Fact]
        public void Copies_and_pastes_into_a_gap()
        {
            var (project, track, item) = OneBox(new ObscureContent { Kind = ObscureKind.Blackout });
            project.Items.Add(new Item
            {
                Id = Guid.NewGuid(),
                TrackId = track.Id,
                TimelineStartTicks = Ms(9_000),
                DurationTicks = Ms(1_000),
                Content = new ObscureContent(),
                Transform = new Transform { ScaleY = 1 },
            });
            var session = new EditorSession(project, null, null);

            Assert.True(session.CanCopyItem(item.Id));
            Assert.True(session.CopyItem(item.Id));

            var gap = session.FindGap(track.Id, Ms(7_000));
            Assert.NotNull(gap);
            Assert.True(session.CanPasteIntoGap(gap.Value));
            var pasted = session.PasteIntoGap(gap.Value);

            Assert.NotNull(pasted);
            var copy = session.Project.Items.Single(i => i.Id == pasted);
            Assert.Equal(ObscureKind.Blackout, Assert.IsType<ObscureContent>(copy.Content).Kind);
            Assert.Equal(Ms(5_000), copy.TimelineStartTicks);
        }

        // --------------------------------------------------------------------------- session

        [Fact]
        public void AddObscure_puts_a_free_sized_box_on_the_front_row_and_undoes()
        {
            var session = new EditorSession(EmptyProject(), null, null);
            var text = session.AddText(0, Ms(5_000)); // the row the box must not share

            var added = session.AddObscure(Ms(1_000), Ms(5_000));

            Assert.NotNull(added);
            var content = Assert.IsType<ObscureContent>(added.Content);
            Assert.Equal(ObscureKind.Mosaic, content.Kind);
            Assert.Equal(ObscureContent.DefaultStrength, content.Strength);
            Assert.Equal(EditorSession.DefaultObscureExtent, added.Transform.Scale);
            Assert.Equal(EditorSession.DefaultObscureExtent, added.Transform.ScaleY);
            Assert.Equal((0.5, 0.5), (added.Transform.X, added.Transform.Y));

            // a fresh row in front of the text, since the text's row is taken over that span
            var tracks = session.Project.Tracks;
            var boxRow = tracks.Single(t => t.Id == added.TrackId);
            var textRow = tracks.Single(t => t.Id == text.TrackId);
            Assert.NotEqual(textRow.Id, boxRow.Id);
            Assert.True(boxRow.Order > textRow.Order);

            session.Undo();
            Assert.DoesNotContain(session.Project.Items, i => i.Content is ObscureContent);
        }

        // ---------------------------------------------------------------------------- editor

        [Fact]
        public void Inspector_offers_placement_obscure_and_ramps_only()
        {
            var session = new EditorSession(EmptyProject(), null, null);
            var vm = new SelectedItemViewModel { Session = session };
            var added = session.AddObscure(0, Ms(5_000));
            session.Select(added.Id);

            Assert.Equal("Obscure", vm.SubjectKind);
            Assert.True(vm.ShowObscure);
            Assert.True(vm.ShowTransform);
            Assert.True(vm.ShowScale);
            Assert.True(vm.ShowScaleHeight);
            Assert.True(vm.ShowRotation);
            Assert.True(vm.AspectUnlocked);
            Assert.True(vm.ShowRamp);
            Assert.False(vm.ShowRampEasing);
            Assert.False(vm.ShowTransitions);
            Assert.False(vm.ShowMask);
            Assert.False(vm.ShowCrop);
            Assert.False(vm.ShowSurround);
            Assert.False(vm.ShowEffect);
            Assert.False(vm.ShowText);
            Assert.False(vm.ShowAudio);
            Assert.False(vm.ShowSpeed);
            Assert.True(vm.ShowObscureStrength);
        }

        [Fact]
        public void Inspector_writes_mode_and_strength()
        {
            var session = new EditorSession(EmptyProject(), null, null);
            var vm = new SelectedItemViewModel { Session = session };
            var added = session.AddObscure(0, Ms(5_000));
            session.Select(added.Id);

            vm.ObscureKind = ObscureKind.Blackout;
            Assert.False(vm.ShowObscureStrength); // a blackout has nothing to measure
            vm.ObscureStrength = 1_000; // clamped to the validated range

            var content = (ObscureContent)session.Project.Items.Single(i => i.Id == added.Id).Content;
            Assert.Equal(ObscureKind.Blackout, content.Kind);
            Assert.Equal(ObscureContent.MaxStrength, content.Strength);
            Assert.Empty(session.Project.Validate());
        }

        [Fact]
        public void Inspector_ramps_are_linear_whatever_the_last_easing_was()
        {
            // a zoom item leaves a non-linear easing in the panel's sticky ramp fields
            var project = EmptyProject();
            var zoomRow = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Effect, Name = "Zoom", Order = 0 };
            var zoom = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = zoomRow.Id,
                DurationTicks = Ms(5_000),
                Content = new ZoomContent(),
            };
            project.Tracks.Add(zoomRow);
            project.Items.Add(zoom);

            var session = new EditorSession(project, null, null);
            var vm = new SelectedItemViewModel { Session = session };
            session.Select(zoom.Id);
            vm.RampEntryEnabled = true;
            vm.RampEntryEasing = TransitionEasing.CubicInOut;
            Assert.True(vm.ShowRampEntryEasing);

            var added = session.AddObscure(0, Ms(5_000));
            session.Select(added.Id);
            Assert.False(vm.RampEntryEnabled);
            vm.RampEntryEnabled = true;
            vm.RampExitEnabled = true;
            vm.RampExitMs = 750;
            Assert.False(vm.ShowRampEntryEasing);

            var item = session.Project.Items.Single(i => i.Id == added.Id);
            Assert.Equal(TransitionKind.Ramp, item.Entry.Kind);
            Assert.Equal(TransitionEasing.Linear, item.Entry.Easing);
            Assert.Equal(TransitionEasing.Linear, item.Exit.Easing);
            Assert.Equal(Ms(750), item.Exit.DurationTicks);
            Assert.Empty(session.Project.Validate());
        }

        [Fact]
        public void Gizmo_and_timeline_treat_it_as_a_free_box()
        {
            var (project, track, item) = OneBox();

            Assert.True(GizmoMath.IsFreeByContent(item));
            Assert.Equal(CornerResizeMode.Free, GizmoMath.CornerMode(false, item));
            // the canvas ratio, like a solid: its natural size is the frame
            Assert.Equal(1080.0 / 1920.0, ItemPlacement.ContentAspect(project, item, 1920, 1080));
            Assert.Equal(TimelineRowKind.Obscure, TimelineRowLayout.KindOf(track, project.Items));
        }
    }
}

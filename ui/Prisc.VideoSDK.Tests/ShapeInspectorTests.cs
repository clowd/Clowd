using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Prisc.UI.VideoEditor;
using Prisc.UI.VideoEditor.Inspector;
using Prisc.UI.VideoEditor.Timeline;
using Prisc.VideoSDK.Composition;
using Prisc.VideoSDK.Editing;
using Prisc.VideoSDK.Model;
using Xunit;

namespace Prisc.VideoSDK.Tests
{
    /// <summary>
    /// The editor's reading of a <see cref="ShapeContent"/>: the inspector's SHAPE section and the
    /// rows a kind shows or hides, the writes behind them (a kind pick goes through
    /// <see cref="ShapeLineLayout.ConvertKind"/> and re-reads the panel), the gizmo's free box and
    /// corner rule, and the timeline row. Framework-free like <see cref="ObscureContentTests"/>.
    /// </summary>
    public class ShapeInspectorTests
    {
        private static long Ms(long ms) => ms * TimeSpan.TicksPerMillisecond;

        private static Project EmptyProject() => new Project
        {
            Output = new OutputSettings { WidthPx = 1920, HeightPx = 1080, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
        };

        /// <summary>A fresh shape added the way the toolbar adds one, selected, with the panel on
        /// it.</summary>
        private static (EditorSession Session, SelectedItemViewModel Vm, Guid Id) NewInspector(
            ShapeKind kind = ShapeKind.Rectangle)
        {
            var session = new EditorSession(EmptyProject(), null, null);
            var vm = new SelectedItemViewModel { Session = session };
            var added = session.AddShape(0, Ms(5_000), kind);
            session.Select(added.Id);
            return (session, vm, added.Id);
        }

        private static Item Live(EditorSession session, Guid id) => session.Project.Items.Single(i => i.Id == id);

        private static ShapeContent LiveShape(EditorSession session, Guid id) => (ShapeContent)Live(session, id).Content;

        private static SelectedItemViewModel.ShapeKindOption Option(ShapeKind kind) =>
            SelectedItemViewModel.ShapeKindOptions.Single(o => o.Value == kind);

        // -------------------------------------------------------------------------- sections

        [Fact]
        public void A_shape_lights_its_own_section_and_the_picture_placement()
        {
            var (_, vm, _) = NewInspector();

            Assert.True(vm.ShowShape);
            Assert.Equal("Shape", vm.SubjectKind);
            Assert.True(vm.ShowTransform);
            Assert.True(vm.ShowScale);
            Assert.True(vm.ShowScaleHeight);
            Assert.True(vm.ShowRotation);
            Assert.True(vm.AspectUnlocked);
            // the standard entry/exit, the same kinds a text card or an image is offered
            Assert.True(vm.ShowTransitions);
            Assert.False(vm.ShowRamp);
            // a picture's surround, at the picture's numbers
            Assert.True(vm.ShowSurround);
            // but nothing that needs pixels of its own
            Assert.False(vm.ShowMask);
            Assert.False(vm.ShowCrop);
            Assert.False(vm.ShowEffect);
            Assert.False(vm.ShowObscure);
            Assert.False(vm.ShowText);
        }

        [Fact]
        public void Surround_defaults_are_the_picture_numbers()
        {
            var (session, vm, id) = NewInspector();

            vm.SurroundShadow = true;

            var surround = Live(session, id).Surround;
            var defaults = Surround.DefaultsFor(SurroundKind.Shadow, cursor: false);
            Assert.Equal(SurroundKind.Shadow, surround.Kind);
            Assert.Equal(defaults.Size, surround.Size);
            Assert.Equal(defaults.Distance, surround.Distance);
            Assert.Equal(defaults.Color, surround.Color);
        }

        [Fact]
        public void The_subject_is_Shape_for_every_kind()
        {
            foreach (var kind in Enum.GetValues<ShapeKind>())
            {
                var (_, vm, _) = NewInspector(kind);
                Assert.Equal("Shape", vm.SubjectKind);
            }
        }

        [Theory]
        [InlineData(ShapeKind.Rectangle, false, false, true)]
        [InlineData(ShapeKind.Ellipse, false, false, false)]
        [InlineData(ShapeKind.Triangle, false, false, true)]
        [InlineData(ShapeKind.Diamond, false, false, true)]
        [InlineData(ShapeKind.Polygon, true, false, true)]
        [InlineData(ShapeKind.Star, false, true, true)]
        [InlineData(ShapeKind.Heart, false, false, false)]
        [InlineData(ShapeKind.Bubble, false, false, true)]
        [InlineData(ShapeKind.Line, false, false, false)]
        [InlineData(ShapeKind.Arrow, false, false, false)]
        [InlineData(ShapeKind.DoubleArrow, false, false, false)]
        public void Each_kind_shows_its_own_dials(ShapeKind kind, bool sides, bool star, bool corners)
        {
            var (_, vm, _) = NewInspector(kind);
            var line = ShapeContent.IsLineKind(kind);

            Assert.Same(Option(kind), vm.ShapeKindChoice);
            Assert.Equal(sides, vm.ShowShapeSides);
            Assert.Equal(star, vm.ShowShapeStar);
            Assert.Equal(corners, vm.ShowShapeCorners);
            Assert.Equal(!line, vm.ShowShapeFill);
            Assert.Equal(line ? "LINE" : "STROKE", vm.ShapeStrokeHeader);
            Assert.Equal(line ? 1 : 0, vm.ShapeStrokeMin);
            // a fresh shape is unfilled, so the fill's color well waits for the box
            Assert.False(vm.ShowShapeFillColor);
            Assert.False(vm.ShowShapeInvisibleHint);
        }

        [Theory]
        [InlineData(ShapeKind.Line)]
        [InlineData(ShapeKind.Arrow)]
        [InlineData(ShapeKind.DoubleArrow)]
        public void A_line_kind_hides_rotation_and_size_but_keeps_center_and_opacity(ShapeKind kind)
        {
            var (_, vm, _) = NewInspector(kind);

            Assert.True(vm.ShowTransform); // Center X/Y and Opacity live in it
            Assert.False(vm.ShowRotation);
            Assert.False(vm.ShowScale);
            Assert.False(vm.ShowScaleHeight);
            Assert.Equal(SelectedItemViewModel.MinLinePosition, vm.PositionMin);
            Assert.Equal(SelectedItemViewModel.MaxLinePosition, vm.PositionMax);
        }

        [Fact]
        public void A_closed_kind_keeps_the_ordinary_center_range()
        {
            var (_, vm, _) = NewInspector();

            Assert.Equal(0, vm.PositionMin);
            Assert.Equal(1, vm.PositionMax);
        }

        [Fact]
        public void A_line_kind_may_be_centered_off_the_canvas()
        {
            var (session, vm, id) = NewInspector(ShapeKind.Arrow);

            vm.PositionX = -0.4;
            vm.PositionY = 1.7;
            Assert.Equal(-0.4, Live(session, id).Transform.X, 12);
            Assert.Equal(1.7, Live(session, id).Transform.Y, 12);

            // still bounded, by the range the body drag keeps the endpoints in
            vm.PositionX = -5;
            Assert.Equal(SelectedItemViewModel.MinLinePosition, Live(session, id).Transform.X);
            Assert.Empty(session.Project.Validate());

            // and a closed shape is not
            var rect = session.AddShape(0, Ms(5_000));
            session.Select(rect.Id);
            vm.PositionX = -0.4;
            Assert.Equal(0, Live(session, rect.Id).Transform.X);
        }

        [Fact]
        public void A_line_off_the_canvas_turned_closed_moves_from_where_it_shows()
        {
            var (session, vm, id) = NewInspector(ShapeKind.Arrow);
            vm.PositionX = -0.3;

            vm.ShapeKindChoice = Option(ShapeKind.Rectangle);

            // the center comes inside the closed range, so the first nudge moves it by the nudge
            Assert.Equal(0, vm.PositionX);
            Assert.Equal(0, Live(session, id).Transform.X);
            vm.PositionX = 0.01;
            Assert.Equal(0.01, Live(session, id).Transform.X, 12);
        }

        [Fact]
        public void Leaving_a_line_kind_gives_the_center_rows_their_range_back()
        {
            var (session, vm, _) = NewInspector(ShapeKind.Arrow);
            vm.PositionX = -0.3; // so the text's center is a change the panel must raise
            var raised = new System.Collections.Generic.List<string>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            var text = session.AddText(0, Ms(5_000));
            session.Select(text.Id);

            Assert.False(vm.ShowShape);
            Assert.Equal(0, vm.PositionMin);
            Assert.Equal(1, vm.PositionMax);
            Assert.Contains(nameof(SelectedItemViewModel.PositionMin), raised);
            Assert.Contains(nameof(SelectedItemViewModel.PositionMax), raised);
            // the range lands before the value it bounds
            Assert.True(raised.IndexOf(nameof(SelectedItemViewModel.PositionMin))
                        < raised.LastIndexOf(nameof(SelectedItemViewModel.PositionX)));
        }

        // ---------------------------------------------------------------------------- kinds

        [Fact]
        public void A_kind_pick_converts_through_the_SDK_in_one_undo_entry()
        {
            var (session, vm, id) = NewInspector();
            var before = Live(session, id).Transform.Clone();

            vm.ShapeKindChoice = Option(ShapeKind.Arrow);

            var item = Live(session, id);
            var shape = (ShapeContent)item.Content;
            Assert.Equal(ShapeKind.Arrow, shape.Kind);
            // closed to line keeps the box and puts the arrow on its inset diagonal
            Assert.Equal(before.X, item.Transform.X);
            Assert.Equal(before.Y, item.Transform.Y);
            Assert.Equal(before.Scale, item.Transform.Scale);
            Assert.Equal(before.ScaleY, item.Transform.ScaleY);
            Assert.InRange(shape.StartX, 0.01, 0.25);
            Assert.InRange(shape.StartY, 0.75, 0.99);
            Assert.Equal(1 - shape.StartX, shape.EndX, 12);
            Assert.Equal(1 - shape.StartY, shape.EndY, 12);
            Assert.Equal(0, shape.Bend);
            Assert.Empty(session.Project.Validate());

            // one entry: the first undo gives the rectangle back, the second removes the shape
            session.Undo();
            Assert.Equal(ShapeKind.Rectangle, LiveShape(session, id).Kind);
            Assert.Same(Option(ShapeKind.Rectangle), vm.ShapeKindChoice);
            session.Undo();
            Assert.DoesNotContain(session.Project.Items, i => i.Id == id);
        }

        [Fact]
        public void Picking_a_line_kind_re_reads_the_panel_without_any_other_prompt()
        {
            var (_, vm, _) = NewInspector();
            Assert.True(vm.ShowRotation);

            vm.ShapeKindChoice = Option(ShapeKind.Arrow);

            Assert.Same(Option(ShapeKind.Arrow), vm.ShapeKindChoice);
            Assert.Equal(1, vm.ShapeStrokeMin);
            Assert.Equal("LINE", vm.ShapeStrokeHeader);
            Assert.False(vm.ShowRotation);
            Assert.False(vm.ShowScale);
            Assert.False(vm.ShowShapeFill);
            Assert.False(vm.ShowShapeCorners);
            Assert.Equal(SelectedItemViewModel.MinLinePosition, vm.PositionMin);

            vm.ShapeKindChoice = Option(ShapeKind.Star);

            Assert.True(vm.ShowRotation);
            Assert.True(vm.ShowScale);
            Assert.True(vm.ShowShapeStar);
            Assert.Equal("STROKE", vm.ShapeStrokeHeader);
            Assert.Equal(0, vm.PositionMin);
        }

        [Fact]
        public void A_closed_shape_at_width_zero_turned_into_a_line_reads_back_width_one()
        {
            var (session, vm, id) = NewInspector();
            vm.ShapeStrokeWidth = 0;
            Assert.Equal(0, LiveShape(session, id).StrokeWidth);

            vm.ShapeKindChoice = Option(ShapeKind.Line);

            Assert.Equal(ShapeContent.MinLineStrokeWidth, LiveShape(session, id).StrokeWidth);
            Assert.Equal(ShapeContent.MinLineStrokeWidth, vm.ShapeStrokeWidth);
        }

        [Fact]
        public void A_line_width_write_refits_the_box_and_the_placement_rows_follow()
        {
            var (session, vm, id) = NewInspector(ShapeKind.Arrow);
            var before = Live(session, id).Transform.Clone();

            vm.ShapeStrokeWidth = 30;

            var item = Live(session, id);
            Assert.Equal(30, ((ShapeContent)item.Content).StrokeWidth);
            // a wider arrow reaches further, so its padded box grew
            Assert.True(item.Transform.Scale > before.Scale);
            Assert.True(item.Transform.ScaleY > before.ScaleY);
            // and the panel read it back without being told
            Assert.Equal(item.Transform.Scale, vm.Scale);
            Assert.Equal(item.Transform.ScaleY.Value, vm.ScaleHeight);
            Assert.Equal(item.Transform.X, vm.PositionX);
            Assert.Equal(item.Transform.Y, vm.PositionY);
            Assert.Empty(session.Project.Validate());
        }

        // ----------------------------------------------------------------------- the dials

        [Fact]
        public void Stroke_width_clamps_to_the_model_range_and_a_line_kind_floor()
        {
            var (session, vm, id) = NewInspector();

            vm.ShapeStrokeWidth = 5_000;
            Assert.Equal(ShapeContent.MaxStrokeWidth, LiveShape(session, id).StrokeWidth);
            vm.ShapeStrokeWidth = -3;
            Assert.Equal(0, LiveShape(session, id).StrokeWidth);
            vm.ShapeStrokeWidth = double.NaN;
            Assert.Equal(0, LiveShape(session, id).StrokeWidth);

            vm.ShapeKindChoice = Option(ShapeKind.Arrow);
            vm.ShapeStrokeWidth = 0;
            Assert.Equal(ShapeContent.MinLineStrokeWidth, LiveShape(session, id).StrokeWidth);
            Assert.Empty(session.Project.Validate());
        }

        [Fact]
        public void The_kind_dials_clamp_and_round_to_what_the_model_accepts()
        {
            var (session, vm, id) = NewInspector(ShapeKind.Polygon);

            vm.ShapePolygonSides = 7.4;
            Assert.Equal(7, LiveShape(session, id).PolygonSides);
            Assert.Equal(7, vm.ShapePolygonSides);
            vm.ShapePolygonSides = 40;
            Assert.Equal(ShapeContent.MaxPolygonSides, LiveShape(session, id).PolygonSides);

            vm.ShapeStarPoints = 1;
            Assert.Equal(ShapeContent.MinStarPoints, LiveShape(session, id).StarPoints);
            vm.ShapeStarInnerRatio = 2;
            Assert.Equal(ShapeContent.MaxStarInnerRatio, LiveShape(session, id).StarInnerRatio);
            vm.ShapeCornerRadius = -1;
            Assert.Equal(0, LiveShape(session, id).CornerRadius);
            vm.ShapeCornerRadius = 36;
            Assert.Equal(36, LiveShape(session, id).CornerRadius);
            Assert.Empty(session.Project.Validate());
        }

        [Fact]
        public void Turning_the_fill_off_and_on_keeps_its_color()
        {
            var (session, vm, id) = NewInspector();

            vm.ShapeFill = true;
            Assert.True(vm.ShowShapeFillColor);
            vm.ShapeFillColorHex = "#8000FF00";
            vm.ShapeFill = false;
            Assert.False(vm.ShowShapeFillColor);
            Assert.Equal("#8000FF00", LiveShape(session, id).FillColor);

            vm.ShapeFill = true;
            Assert.True(LiveShape(session, id).Fill);
            Assert.Equal("#8000FF00", vm.ShapeFillColorHex);
            Assert.Equal("#8000FF00", LiveShape(session, id).FillColor);
        }

        [Fact]
        public void Hex_writes_round_trip_and_half_typed_ones_wait()
        {
            var (session, vm, id) = NewInspector();

            vm.ShapeStrokeColorHex = "#FF112233";
            vm.ShapeFillColorHex = "#40AABBCC";
            Assert.Equal("#FF112233", LiveShape(session, id).StrokeColor);
            Assert.Equal("#40AABBCC", LiveShape(session, id).FillColor);

            vm.ShapeStrokeColorHex = "#12";
            Assert.Equal("#FF112233", LiveShape(session, id).StrokeColor);

            // a re-read from elsewhere (undo) puts the model's value back in the box
            session.EditItem(id, i => ((ShapeContent)i.Content).StrokeColor = "#FF445566");
            Assert.Equal("#FF445566", vm.ShapeStrokeColorHex);
        }

        [Fact]
        public void Dash_writes_through()
        {
            var (session, vm, id) = NewInspector();

            vm.ShapeDash = ShapeDash.Dotted;

            Assert.Equal(ShapeDash.Dotted, LiveShape(session, id).Dash);
        }

        [Fact]
        public void The_invisible_hint_shows_only_for_a_closed_shape_that_draws_nothing()
        {
            var (_, vm, _) = NewInspector();

            vm.ShapeStrokeWidth = 0;
            Assert.True(vm.ShowShapeInvisibleHint);

            vm.ShapeFill = true; // the default fill is a visible 35% red
            Assert.False(vm.ShowShapeInvisibleHint);

            vm.ShapeFillColorHex = "#00FF0000";
            Assert.True(vm.ShowShapeInvisibleHint);

            vm.ShapeStrokeWidth = 2;
            Assert.False(vm.ShowShapeInvisibleHint);

            // a line cannot get there: its width never goes under 1
            vm.ShapeKindChoice = Option(ShapeKind.Line);
            vm.ShapeStrokeWidth = 0;
            Assert.False(vm.ShowShapeInvisibleHint);
        }

        [Fact]
        public void A_shape_section_write_never_reaches_another_item()
        {
            var (session, vm, _) = NewInspector();
            var text = session.AddText(0, Ms(5_000));
            session.Select(text.Id);

            // the shape section's bindings stay live while it is hidden
            vm.ShapeStrokeWidth = 50;
            vm.ShapeKindChoice = Option(ShapeKind.Star);

            Assert.IsType<TextContent>(Live(session, text.Id).Content);
            Assert.Empty(session.Project.Validate());
        }

        [Fact]
        public void Every_kind_has_a_tile_in_enum_order_with_its_name()
        {
            var options = SelectedItemViewModel.ShapeKindOptions;
            Assert.Equal(Enum.GetValues<ShapeKind>(), options.Select(o => o.Value));
            for (int i = 0; i < options.Count; i++)
                Assert.Equal(i, (int)options[i].Value);

            Assert.Equal("Double arrow", Option(ShapeKind.DoubleArrow).Label);
            Assert.Equal("Bubble", Option(ShapeKind.Bubble).Label);
            Assert.Equal("Speech bubble", Option(ShapeKind.Bubble).Tip);
            Assert.All(options, o => Assert.False(string.IsNullOrEmpty(o.Label)));
        }

        // ------------------------------------------------------------- gizmo and timeline

        [Fact]
        public void The_gizmo_boxes_every_shape_freely_on_the_canvas_ratio()
        {
            var (session, _, id) = NewInspector();
            var item = Live(session, id);

            Assert.True(GizmoMath.IsFreeByContent(item));
            Assert.Equal(1080.0 / 1920.0, ItemPlacement.ContentAspect(session.Project, item, 1920, 1080));
        }

        [Theory]
        [InlineData(ShapeKind.Rectangle, true)]
        [InlineData(ShapeKind.Bubble, true)]
        [InlineData(ShapeKind.Ellipse, false)]
        [InlineData(ShapeKind.Star, false)]
        [InlineData(ShapeKind.Polygon, false)]
        [InlineData(ShapeKind.Heart, false)]
        [InlineData(ShapeKind.Triangle, false)]
        [InlineData(ShapeKind.Diamond, false)]
        public void Corners_size_freely_only_on_the_annotation_boxes(ShapeKind kind, bool free)
        {
            var (session, _, id) = NewInspector(kind);
            var item = Live(session, id);
            var expected = free ? CornerResizeMode.Free : CornerResizeMode.BoxAspect;

            // the inspector's Unlocked flag plays no part either way
            Assert.Equal(expected, GizmoMath.CornerMode(freeResizeTile: true, item));
            Assert.Equal(expected, GizmoMath.CornerMode(freeResizeTile: false, item));
        }

        [Fact]
        public void The_timeline_gives_a_shape_its_own_card_row()
        {
            var (session, _, id) = NewInspector(ShapeKind.Star);
            var track = session.Project.Tracks.Single(t => t.Id == Live(session, id).TrackId);

            Assert.Equal(TimelineRowKind.Shape, TimelineRowLayout.KindOf(track, session.Project.Items));
            Assert.Equal(TimelineRowLayout.ShapeRowHeight, TimelineRowLayout.HeightOf(TimelineRowKind.Shape));
            Assert.Equal(TimelineRowBlock.Video, TimelineRowLayout.BlockOf(TimelineRowKind.Shape));
            Assert.False(TimelineRowLayout.IsInputOverlay(TimelineRowKind.Shape));
        }

        [Fact]
        public void Every_kind_names_a_label_and_a_glyph()
        {
            foreach (var kind in Enum.GetValues<ShapeKind>())
            {
                Assert.False(string.IsNullOrEmpty(ShapeKinds.Label(kind)));
                Assert.StartsWith("Icon", ShapeKinds.IconKey(kind));
            }

            Assert.Equal("IconToolArrow", ShapeKinds.IconKey(ShapeKind.DoubleArrow));
            Assert.Equal("IconShapes", ShapeKinds.IconKey(ShapeKind.Heart));
        }

        // ---------------------------------------------------------------------- transitions

        [Fact]
        public void A_shape_is_offered_draw_and_everything_else_is_not()
        {
            var (session, vm, _) = NewInspector();

            Assert.Same(SelectedItemViewModel.ShapeTransitionKinds, vm.TransitionKindOptions);
            Assert.Contains(TransitionKind.Draw, vm.TransitionKindOptions);
            Assert.DoesNotContain(TransitionKind.Ramp, vm.TransitionKindOptions);

            var text = session.AddText(0, Ms(5_000));
            session.Select(text.Id);
            Assert.Same(SelectedItemViewModel.PictureTransitionKinds, vm.TransitionKindOptions);
            Assert.DoesNotContain(TransitionKind.Draw, vm.TransitionKindOptions);
            // a file that carries a ramp still shows it
            Assert.Contains(TransitionKind.Ramp, vm.TransitionKindOptions);

            session.ClearSelection();
            Assert.Same(SelectedItemViewModel.PictureTransitionKinds, vm.TransitionKindOptions);
        }

        [Fact]
        public void Every_offered_transition_validates_on_the_item_it_is_offered_for()
        {
            foreach (var kind in SelectedItemViewModel.ShapeTransitionKinds)
            {
                var (session, vm, _) = NewInspector(ShapeKind.Arrow);
                vm.EntryKind = kind;
                vm.ExitKind = kind;
                Assert.Empty(session.Project.Validate());
            }

            foreach (var kind in SelectedItemViewModel.PictureTransitionKinds)
            {
                var (session, vm, _) = NewInspector();
                var text = session.AddText(0, Ms(5_000));
                session.Select(text.Id);
                vm.EntryKind = kind;
                vm.ExitKind = kind;
                Assert.Equal(kind == TransitionKind.None ? (TransitionKind?)null : kind, Live(session, text.Id).Entry?.Kind);
                Assert.Empty(session.Project.Validate());
            }
        }

        [Fact]
        public void Switching_between_a_drawn_shape_and_a_faded_text_card_writes_nothing()
        {
            var (session, vm, id) = NewInspector();
            var text = session.AddText(0, Ms(5_000));
            session.Select(text.Id);
            vm.EntryKind = TransitionKind.Fade;
            session.Select(id);
            vm.EntryKind = TransitionKind.Draw;
            vm.ExitKind = TransitionKind.Draw;
            var before = JsonSerializer.Serialize(session.Project, ProjectJsonContext.Default.Project);

            // the list swaps ahead of the kinds, while the panel is still reading the model
            var raised = new List<string>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            session.Select(text.Id);
            Assert.True(raised.IndexOf(nameof(vm.TransitionKindOptions)) >= 0);
            Assert.True(raised.IndexOf(nameof(vm.TransitionKindOptions)) < raised.IndexOf(nameof(vm.EntryKind)));
            Assert.Equal(TransitionKind.Fade, vm.EntryKind);

            raised.Clear();
            session.Select(id);
            Assert.True(raised.IndexOf(nameof(vm.TransitionKindOptions)) < raised.IndexOf(nameof(vm.EntryKind)));
            Assert.Equal(TransitionKind.Draw, vm.EntryKind);
            Assert.Equal(TransitionKind.Draw, vm.ExitKind);

            Assert.Equal(before, JsonSerializer.Serialize(session.Project, ProjectJsonContext.Default.Project));
            // nothing was pushed since: one undo takes back the last real edit, the shape's exit
            session.Undo();
            Assert.Null(Live(session, id).Exit);
            Assert.Equal(TransitionKind.Draw, Live(session, id).Entry.Kind);
            Assert.Equal(TransitionKind.Fade, Live(session, text.Id).Entry.Kind);
        }
    }
}

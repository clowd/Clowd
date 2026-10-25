using System;
using System.Linq;
using System.Text.Json;
using Clowd.Geometry2D;
using Clowd.UI.VideoEditor;
using Clowd.UI.VideoEditor.Inspector;
using Clowd.VideoSDK.Composition;
using Clowd.VideoSDK.Editing;
using Clowd.VideoSDK.Model;
using Xunit;
using ModelTransform = Clowd.VideoSDK.Model.Transform;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The preview's line handles (<see cref="ShapeHandleMath"/>, <see cref="ShapeHandleDrag"/>):
    /// which part a press grabs, the ink hit test the preview's click-to-select shares, and what
    /// each drag writes. All in canvas pixels of a 1920x1080 project previewed at half size, the
    /// coordinates <see cref="ShapeHandlesControl"/> works in once the letterbox offset is taken
    /// off. Framework-free like <see cref="ShapeInspectorTests"/>.
    /// </summary>
    public class ShapeHandleMathTests
    {
        private const double W = 960, H = 540;

        /// <summary>Canvas pixels per output pixel: the 540-high preview of a 1080-high output.</summary>
        private const double PxScale = 0.5;

        private static long Ms(long ms) => ms * TimeSpan.TicksPerMillisecond;

        private static OutputSettings Output() =>
            new OutputSettings { WidthPx = 1920, HeightPx = 1080, FpsNum = 30, FpsDen = 1, SampleRate = 48000 };

        /// <summary>A line item whose line is <paramref name="start"/> to <paramref name="end"/>
        /// on the canvas, written the way a handle drag writes one.</summary>
        private static Item LineItem(ShapeKind kind, Vec2 start, Vec2 end, double bend = 0, double skew = 0,
            double width = 6)
        {
            var shape = new ShapeContent { Kind = kind, StrokeWidth = width };
            var item = new Item
            {
                Id = Guid.NewGuid(),
                TimelineStartTicks = 0,
                DurationTicks = Ms(5_000),
                Content = shape,
                Transform = new ModelTransform(),
            };
            ShapeLineLayout.Write(item.Transform, shape, new ShapeLine(start, end, bend, skew), W, H, PxScale);
            return item;
        }

        /// <summary>A project holding the items on one video row each, bottom to top.</summary>
        private static Project ProjectWith(params Item[] bottomToTop)
        {
            var project = new Project { Output = Output() };
            for (int i = 0; i < bottomToTop.Length; i++)
            {
                var track = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = $"Row {i}", Order = i };
                project.Tracks.Add(track);
                bottomToTop[i].TrackId = track.Id;
                project.Items.Add(bottomToTop[i]);
            }

            project.Normalize();
            Assert.Empty(project.Validate());
            return project;
        }

        private static string Json(Item item) => JsonSerializer.Serialize(item, ProjectJsonContext.Default.Item);

        private static string Json(ItemContent content) =>
            JsonSerializer.Serialize(content, ProjectJsonContext.Default.ItemContent);

        private static ShapeLine Resolve(Item item)
        {
            Assert.True(ShapeLineLayout.TryResolve(item.Transform, (ShapeContent)item.Content, W, H, out var line));
            return line;
        }

        private static ShapeHandleDrag Begin(Item item, ShapeHandlePart part, Vec2 pointer)
        {
            var drag = ShapeHandleDrag.Begin(item, part, pointer, W, H, PxScale);
            Assert.NotNull(drag);
            return drag;
        }

        private static void AssertNear(Vec2 expected, Vec2 actual, double tolerance = 1e-6)
        {
            Assert.True(QuadraticCurve.Distance(expected, actual) <= tolerance,
                $"expected {expected}, got {actual}");
        }

        private static double AngleDegrees(Vec2 from, Vec2 to) =>
            Math.Atan2(to.Y - from.Y, to.X - from.X) * 180 / Math.PI;

        // -------------------------------------------------------------------------- hit parts

        [Fact]
        public void The_nearest_handle_wins_on_a_short_arrow()
        {
            // a 12 px arrow: start, bend and end handles all within one hit radius of each other
            var shape = new ShapeContent { Kind = ShapeKind.Arrow };
            var line = new ShapeLine(new Vec2(400, 300), new Vec2(412, 300), 0, 0);
            Assert.Equal(new Vec2(406, 300), line.Handle);

            Assert.Equal(ShapeHandlePart.End, ShapeHandleMath.HitPart(shape, line, new Vec2(410.5, 300), PxScale));
            Assert.Equal(ShapeHandlePart.Start, ShapeHandleMath.HitPart(shape, line, new Vec2(401, 301), PxScale));
            Assert.Equal(ShapeHandlePart.Bend, ShapeHandleMath.HitPart(shape, line, new Vec2(406, 300.5), PxScale));

            // exactly between the bend and end handles: the bend handle takes it
            Assert.Equal(ShapeHandlePart.Bend, ShapeHandleMath.HitPart(shape, line, new Vec2(409, 300), PxScale));
        }

        [Fact]
        public void The_spine_is_the_body_and_bare_canvas_is_nothing()
        {
            var shape = new ShapeContent { Kind = ShapeKind.Arrow, StrokeWidth = 1 };
            var line = new ShapeLine(new Vec2(100, 400), new Vec2(700, 100), 0, 0);
            var normal = new Vec2(300, 600) / new Vec2(300, 600).Length; // left normal of the chord
            var quarter = new Vec2(250, 325);

            // a 1 px line is still a target a few pixels either side (the minimum corridor)
            Assert.Equal(ShapeHandlePart.Body, ShapeHandleMath.HitPart(shape, line, quarter + normal * 4, PxScale));
            Assert.Equal(ShapeHandlePart.Body, ShapeHandleMath.HitPart(shape, line, quarter - normal * 4, PxScale));
            Assert.Equal(ShapeHandlePart.None, ShapeHandleMath.HitPart(shape, line, quarter + normal * 40, PxScale));
            Assert.Equal(ShapeHandlePart.None, ShapeHandleMath.HitPart(shape, line, new Vec2(100, 100), PxScale));
        }

        // -------------------------------------------------------------------------- ink hit test

        [Fact]
        public void A_head_wing_is_ink_well_off_the_spine()
        {
            var shape = new ShapeContent { Kind = ShapeKind.Arrow, StrokeWidth = 6 };
            var line = new ShapeLine(new Vec2(100, 300), new Vec2(700, 300), 0, 0);

            // the head as the renderer builds it, in output pixels
            var parts = ArrowGeometry.Build(line.Start / PxScale, line.End / PxScale, null, 6, dashed: false);
            var centroid = (parts.Tip + parts.HeadLeft + parts.HeadNotch + parts.HeadRight) / 4;
            var wing = parts.HeadLeft + (centroid - parts.HeadLeft) * 0.2;
            double offSpine = Math.Abs(wing.Y - 600);
            Assert.InRange(offSpine, 14, 16); // about 15 output px, past the 12 px corridor at this scale

            Assert.True(ShapeHandleMath.HitsShape(shape, line, wing * PxScale, PxScale));

            // the same distance off the spine along the shaft is bare canvas
            var shaft = new Vec2(400, 300 - offSpine * PxScale);
            Assert.False(ShapeHandleMath.HitsShape(shape, line, shaft, PxScale));
        }

        [Fact]
        public void A_bowed_line_is_hit_on_its_bow_not_its_chord()
        {
            var shape = new ShapeContent { Kind = ShapeKind.Arrow, StrokeWidth = 6 };
            var line = new ShapeLine(new Vec2(200, 400), new Vec2(700, 400), 0.4, 0);

            Assert.True(ShapeHandleMath.HitsShape(shape, line, line.Handle, PxScale));
            Assert.False(ShapeHandleMath.HitsShape(shape, line, new Vec2(450, 400), PxScale));
        }

        [Fact]
        public void Click_to_select_skips_a_lines_empty_box_corner_and_takes_its_head()
        {
            var arrow = LineItem(ShapeKind.Arrow, new Vec2(200, 450), new Vec2(700, 100));
            var project = ProjectWith(arrow);
            var transform = arrow.Transform;
            double left = (transform.X - transform.Scale / 2) * W;
            double top = (transform.Y - transform.ScaleY.Value / 2) * H;

            // inside the box, far from the ink: nothing there to select
            Assert.Null(ItemPlacement.HitTest(project, 0, left + 6, top + 6, W, H));

            // a head wing
            var line = Resolve(arrow);
            var parts = ArrowGeometry.Build(line.Start / PxScale, line.End / PxScale, null, 6, dashed: false);
            var wing = (parts.HeadLeft + (parts.Tip + parts.HeadNotch) / 2) / 2 * PxScale;
            Assert.Equal(arrow.Id, ItemPlacement.HitTest(project, 0, wing.X, wing.Y, W, H)?.Id);
        }

        [Fact]
        public void A_head_stretched_past_the_box_is_still_selectable()
        {
            var arrow = LineItem(ShapeKind.Arrow, new Vec2(200, 300), new Vec2(700, 300));
            // the inspector squeezes the box to a quarter of its height: the endpoints come with it
            // (they are box fractions), but the head is rebuilt at full size and pokes out
            arrow.Transform.ScaleY /= 4;
            var project = ProjectWith(arrow);

            var line = Resolve(arrow);
            var parts = ArrowGeometry.Build(line.Start / PxScale, line.End / PxScale, null, 6, dashed: false);
            var wing = (parts.HeadLeft + (parts.Tip + parts.HeadNotch) / 2) / 2 * PxScale;
            double boxTop = (arrow.Transform.Y - arrow.Transform.ScaleY.Value / 2) * H;
            double boxBottom = (arrow.Transform.Y + arrow.Transform.ScaleY.Value / 2) * H;
            Assert.True(wing.Y < boxTop || wing.Y > boxBottom, "the wing should lie outside the box");

            Assert.Equal(arrow.Id, ItemPlacement.HitTest(project, 0, wing.X, wing.Y, W, H)?.Id);
        }

        [Fact]
        public void A_rotated_line_is_hit_where_it_is_drawn()
        {
            var arrow = LineItem(ShapeKind.Line, new Vec2(300, 270), new Vec2(660, 270), width: 4);
            arrow.Transform.Rotation = 90; // now a vertical line through the box center
            var project = ProjectWith(arrow);

            Assert.Equal(arrow.Id, ItemPlacement.HitTest(project, 0, 480, 150, W, H)?.Id);
            Assert.Null(ItemPlacement.HitTest(project, 0, 350, 270, W, H));
        }

        [Fact]
        public void A_body_press_under_an_item_above_is_declined_but_its_handle_is_taken()
        {
            var arrow = LineItem(ShapeKind.Arrow, new Vec2(200, 300), new Vec2(700, 300));
            var text = new Item
            {
                Id = Guid.NewGuid(),
                TimelineStartTicks = 0,
                DurationTicks = Ms(5_000),
                Content = new TextContent { Text = "Above", Size = 120, Color = "#FFFFFFFF" },
                Transform = new ModelTransform { X = 230 / W, Y = 300 / H },
            };
            var project = ProjectWith(arrow, text);
            var line = Resolve(arrow);

            // on the spine where the text card covers it: the press belongs to the text
            var underText = new Vec2(250, 300);
            Assert.Equal(text.Id, ItemPlacement.HitTest(project, 0, underText.X, underText.Y, W, H)?.Id);
            Assert.Equal(ShapeHandlePart.None,
                ShapeHandleMath.ResolvePress(project, arrow, 0, underText, W, H));

            // the start handle in the same card: handles always win
            Assert.Equal(ShapeHandlePart.Start,
                ShapeHandleMath.ResolvePress(project, arrow, 0, line.Start + new Vec2(2, 1), W, H));

            // clear of the card, the spine is the arrow's body
            Assert.Equal(ShapeHandlePart.Body,
                ShapeHandleMath.ResolvePress(project, arrow, 0, new Vec2(600, 300), W, H));
        }

        // -------------------------------------------------------------------------- drags

        [Fact]
        public void A_start_drag_with_Shift_lands_on_a_multiple_of_15_degrees()
        {
            var arrow = LineItem(ShapeKind.Arrow, new Vec2(200, 400), new Vec2(700, 150));
            var press = Resolve(arrow);
            var drag = Begin(arrow, ShapeHandlePart.Start, press.Start + new Vec2(3, -2));

            drag.Apply(arrow, new Vec2(123, 377), snap: true);

            var line = Resolve(arrow);
            AssertNear(press.End, line.End);
            double angle = AngleDegrees(line.End, line.Start);
            double steps = angle / AngleSnap.StepDegrees;
            Assert.True(Math.Abs(steps - Math.Round(steps)) < 1e-6, $"angle {angle} is not a 15 degree step");

            // without Shift the start goes exactly where it was dragged (less the grab offset)
            drag.Apply(arrow, new Vec2(123, 377), snap: false);
            AssertNear(new Vec2(120, 379), Resolve(arrow).Start);
        }

        [Fact]
        public void An_endpoint_drag_keeps_the_bows_height_in_pixels()
        {
            var arrow = LineItem(ShapeKind.Arrow, new Vec2(200, 300), new Vec2(400, 300), bend: 0.3, skew: 0.1);
            var press = Resolve(arrow);
            double bowPx = press.Bend * press.Chord;
            Assert.Equal(60, bowPx, 6);

            var drag = Begin(arrow, ShapeHandlePart.End, press.End);
            var line = drag.LineAt(new Vec2(600, 300), snap: false);

            Assert.Equal(400, line.Chord, 9);
            Assert.Equal(bowPx, line.Bend * line.Chord, 9);
            Assert.Equal(press.Bend / 2, line.Bend, 9);
            Assert.Equal(press.BendSkew, line.BendSkew); // the lean stays chord-relative

            drag.Apply(arrow, new Vec2(600, 300), snap: false);
            var written = Resolve(arrow);
            Assert.Equal(bowPx, written.Bend * written.Chord, 6);
        }

        [Fact]
        public void A_bend_drag_puts_the_handle_under_the_pointer_without_a_jump()
        {
            var arrow = LineItem(ShapeKind.Arrow, new Vec2(200, 300), new Vec2(600, 300), bend: 0.2);
            var press = Resolve(arrow);
            var grab = new Vec2(2, -1.5);
            var drag = Begin(arrow, ShapeHandlePart.Bend, press.Handle + grab);

            // a press with no movement leaves the bow where it was
            var still = drag.LineAt(press.Handle + grab, snap: false);
            AssertNear(press.Handle, still.Handle, 1e-9);
            Assert.Equal(press.Bend, still.Bend, 9);

            var pointer = new Vec2(430, 180);
            var moved = drag.LineAt(pointer, snap: false);
            AssertNear(pointer - grab, moved.Handle, 1e-9);

            // Shift steps the bow by tenths of the chord and drops the lean
            var snapped = drag.LineAt(pointer, snap: true);
            Assert.Equal(Math.Round(snapped.Bend * 10), snapped.Bend * 10, 9);
            Assert.Equal(0, snapped.BendSkew);

            // within the snap distance of the chord it straightens exactly
            var straight = drag.LineAt(new Vec2(400, 301) + grab, snap: false);
            Assert.Equal(0, straight.Bend);
            Assert.Null(straight.Control);
        }

        [Fact]
        public void A_body_move_changes_only_the_center()
        {
            var arrow = LineItem(ShapeKind.Arrow, new Vec2(200, 300), new Vec2(600, 200), bend: 0.25);
            var before = (ShapeContent)arrow.Content.Clone();
            var t0 = arrow.Transform.Clone();
            var drag = Begin(arrow, ShapeHandlePart.Body, new Vec2(400, 250));

            drag.Apply(arrow, new Vec2(430, 230), snap: true);

            var t = arrow.Transform;
            Assert.Equal(t0.X + 30 / W, t.X, 12);
            Assert.Equal(t0.Y - 20 / H, t.Y, 12);
            Assert.Equal(t0.Scale, t.Scale);
            Assert.Equal(t0.ScaleY, t.ScaleY);
            Assert.Equal(t0.Rotation, t.Rotation);
            Assert.Equal(Json(before), Json(arrow.Content));
        }

        [Fact]
        public void A_still_body_drag_of_an_off_canvas_line_changes_nothing()
        {
            // a 1 px horizontal line above and left of the frame: its box center is off the canvas
            var line = LineItem(ShapeKind.Line, new Vec2(-400, -60), new Vec2(-150, -60), width: 1);
            Assert.True(line.Transform.X < 0 && line.Transform.Y < 0);
            var before = Json(line);
            var drag = Begin(line, ShapeHandlePart.Body, new Vec2(-275, -60));

            drag.Apply(line, new Vec2(-275, -60), snap: false);

            Assert.Equal(before, Json(line));
        }

        [Fact]
        public void A_body_drag_stops_the_endpoints_at_minus_one_and_two_canvases()
        {
            var arrow = LineItem(ShapeKind.Arrow, new Vec2(200, 300), new Vec2(600, 200));
            var drag = Begin(arrow, ShapeHandlePart.Body, new Vec2(400, 250));

            drag.Apply(arrow, new Vec2(400 - 10 * W, 250 + 10 * H), snap: false);

            var line = Resolve(arrow);
            Assert.Equal(-W, Math.Min(line.Start.X, line.End.X), 6);
            Assert.Equal(2 * H, Math.Max(line.Start.Y, line.End.Y), 6);

            // and back the other way
            drag.Apply(arrow, new Vec2(400 + 10 * W, 250 - 10 * H), snap: false);
            line = Resolve(arrow);
            Assert.Equal(2 * W, Math.Max(line.Start.X, line.End.X), 6);
            Assert.Equal(-H, Math.Min(line.Start.Y, line.End.Y), 6);
        }

        [Fact]
        public void An_endpoint_drag_stays_within_minus_one_and_two_canvases()
        {
            var arrow = LineItem(ShapeKind.Arrow, new Vec2(200, 300), new Vec2(600, 200));
            var drag = Begin(arrow, ShapeHandlePart.End, Resolve(arrow).End);

            drag.Apply(arrow, new Vec2(50 * W, -50 * H), snap: false);

            AssertNear(new Vec2(2 * W, -H), Resolve(arrow).End);
        }

        [Fact]
        public void A_rotated_items_handle_drag_bakes_the_rotation()
        {
            var arrow = LineItem(ShapeKind.Arrow, new Vec2(300, 300), new Vec2(650, 220), bend: 0.2);
            arrow.Transform.Rotation = 30;
            var press = Resolve(arrow);
            var drag = Begin(arrow, ShapeHandlePart.End, press.End);

            // a press and release in place: the same drawing, with the rotation in the points
            drag.Apply(arrow, press.End, snap: false);
            Assert.Equal(0, arrow.Transform.Rotation);
            var baked = Resolve(arrow);
            AssertNear(press.Start, baked.Start);
            AssertNear(press.End, baked.End);
            AssertNear(press.Handle, baked.Handle);

            // and the dragged end then follows the pointer on screen, not in the item's old frame
            drag.Apply(arrow, press.End + new Vec2(25, 10), snap: false);
            var moved = Resolve(arrow);
            AssertNear(press.Start, moved.Start);
            AssertNear(press.End + new Vec2(25, 10), moved.End);
        }

        [Fact]
        public void Drags_are_labelled_by_part_and_kind()
        {
            var arrow = LineItem(ShapeKind.DoubleArrow, new Vec2(200, 300), new Vec2(600, 200));
            var line = LineItem(ShapeKind.Line, new Vec2(200, 300), new Vec2(600, 200));

            Assert.Equal("Move arrow end", Begin(arrow, ShapeHandlePart.End, default).Label);
            Assert.Equal("Move arrow start", Begin(arrow, ShapeHandlePart.Start, default).Label);
            Assert.Equal("Bend line", Begin(line, ShapeHandlePart.Bend, default).Label);
            Assert.Equal("Move line", Begin(line, ShapeHandlePart.Body, default).Label);

            // a closed kind has no line to drag
            var box = new Item { Content = new ShapeContent(), Transform = new ModelTransform() };
            Assert.Null(ShapeHandleDrag.Begin(box, ShapeHandlePart.End, default, W, H, PxScale));
        }

        // -------------------------------------------------------------------------- session

        [Fact]
        public void A_handle_drag_is_one_undo_entry_and_the_inspector_follows_it()
        {
            var session = new EditorSession(new Project { Output = Output() }, null, null);
            var added = session.AddShape(0, Ms(5_000), ShapeKind.Arrow);
            session.Select(added.Id);
            var vm = new SelectedItemViewModel { Session = session };
            var addedJson = Json(session.Project.Items.Single(i => i.Id == added.Id));

            var origin = new object(); // the handles control
            var live = session.Project.Items.Single(i => i.Id == added.Id);
            var press = Resolve(live);
            var drag = Begin(live, ShapeHandlePart.End, press.End);
            var gesture = session.BeginGesture(drag.Label, origin);
            foreach (var step in new[] { 10.0, 40, 90 })
            {
                var pointer = press.End + new Vec2(step, step / 2);
                session.EditItem(added.Id, i => drag.Apply(i, pointer, snap: false),
                    $"handles:shape:{added.Id}", structural: false, origin: origin);
            }

            gesture.Commit();

            live = session.Project.Items.Single(i => i.Id == added.Id);
            AssertNear(press.End + new Vec2(90, 45), Resolve(live).End);
            Assert.Equal(live.Transform.X, vm.PositionX, 9);
            Assert.Equal(live.Transform.Y, vm.PositionY, 9);

            // the whole drag undoes in one step, back to the arrow as added
            session.Undo();
            Assert.Equal(addedJson, Json(session.Project.Items.Single(i => i.Id == added.Id)));
            Assert.Equal(session.Project.Items.Single(i => i.Id == added.Id).Transform.X, vm.PositionX, 9);
        }

        [Fact]
        public void A_cancelled_drag_restores_the_line()
        {
            var session = new EditorSession(new Project { Output = Output() }, null, null);
            var added = session.AddShape(0, Ms(5_000), ShapeKind.Arrow);
            var before = session.Project.ToJson();

            var live = session.Project.Items.Single(i => i.Id == added.Id);
            var drag = Begin(live, ShapeHandlePart.Bend, Resolve(live).Handle);
            var gesture = session.BeginGesture(drag.Label, this);
            session.EditItem(added.Id, i => drag.Apply(i, new Vec2(100, 100), snap: false),
                $"handles:shape:{added.Id}", structural: false, origin: this);
            Assert.NotEqual(before, session.Project.ToJson());

            gesture.Cancel();

            Assert.Equal(before, session.Project.ToJson());
        }
    }
}

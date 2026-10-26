using Avalonia;
using Clowd.UI.Controls.Tray;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The tray grip's press/drag/release machine, pinned rule by rule. <see cref="TrayDragGesture"/>
    /// is free of Avalonia controls, pointers and windows for the same reason the placement maths are:
    /// the three rules it encodes (strict threshold, latched drag, deltas measured from the press
    /// point) all fail SILENTLY in the real app — a strip that snaps back to where the drag began
    /// when the user meant to move it, or one that drifts away from the pointer a few pixels per
    /// event — and none of them throws.
    /// <para>
    /// The threshold arrives already DPI-scaled from the grip, so these tests use a plain 5 px and
    /// never touch <c>RenderScaling</c>.
    /// </para>
    /// </summary>
    public class TrayDragGestureTests
    {
        private const double Threshold = 5;

        private static TrayDragGesture Pressed(int x = 100, int y = 100)
        {
            var g = new TrayDragGesture(Threshold);
            g.Press(new PixelPoint(x, y));
            return g;
        }

        [Fact]
        public void A_fresh_gesture_is_neither_pressed_nor_dragging()
        {
            var g = new TrayDragGesture(Threshold);
            Assert.False(g.IsPressed);
            Assert.False(g.IsDragging);

            // a move with no press behind it is ignored rather than starting a phantom drag
            Assert.False(g.Move(new PixelPoint(900, 900), out var delta));
            Assert.Equal(default, delta);
        }

        [Fact]
        public void Press_arms_the_gesture_without_starting_a_drag()
        {
            var g = Pressed();
            Assert.True(g.IsPressed);
            Assert.False(g.IsDragging);
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(3, 0)]
        [InlineData(0, -4)]
        [InlineData(5, 5)] // exactly the threshold is still a click: the test is a strict >
        [InlineData(-5, 0)]
        [InlineData(0, 5)]
        public void Moves_up_to_and_including_the_threshold_are_not_a_drag(int dx, int dy)
        {
            var g = Pressed();
            Assert.False(g.Move(new PixelPoint(100 + dx, 100 + dy), out var delta));
            Assert.False(g.IsDragging);
            Assert.Equal(new PixelPoint(dx, dy), delta); // the delta is reported even before the latch
        }

        [Theory]
        [InlineData(6, 0)]
        [InlineData(-6, 0)]
        [InlineData(0, 6)]
        [InlineData(0, -6)]
        [InlineData(6, 1)] // either axis on its own is enough
        [InlineData(1, -6)]
        public void The_first_move_past_the_threshold_on_either_axis_is_a_drag(int dx, int dy)
        {
            var g = Pressed();
            Assert.True(g.Move(new PixelPoint(100 + dx, 100 + dy), out var delta));
            Assert.True(g.IsDragging);
            Assert.Equal(new PixelPoint(dx, dy), delta);
        }

        [Fact]
        public void The_drag_latch_survives_the_pointer_coming_back_inside_the_threshold()
        {
            var g = Pressed();
            Assert.True(g.Move(new PixelPoint(140, 100), out _));

            // back to within 1 px of the origin: a slow wandering drag must not decay into a click,
            // or the window would snap back to where the drag began
            Assert.True(g.Move(new PixelPoint(101, 100), out var delta));
            Assert.True(g.IsDragging);
            Assert.Equal(new PixelPoint(1, 0), delta);

            // and exactly back on the press point
            Assert.True(g.Move(new PixelPoint(100, 100), out delta));
            Assert.True(g.IsDragging);
            Assert.Equal(new PixelPoint(0, 0), delta);
        }

        [Fact]
        public void Deltas_are_totals_from_the_press_point_and_are_never_accumulated()
        {
            var g = Pressed(200, 300);

            g.Move(new PixelPoint(220, 300), out var d1);
            Assert.Equal(new PixelPoint(20, 0), d1);

            g.Move(new PixelPoint(230, 290), out var d2);
            Assert.Equal(new PixelPoint(30, -10), d2); // 30, not 20 + 30

            g.Move(new PixelPoint(180, 340), out var d3);
            Assert.Equal(new PixelPoint(-20, 40), d3); // crossing the origin stays absolute
        }

        [Fact]
        public void Release_reports_the_drag_and_resets()
        {
            var g = Pressed();
            g.Move(new PixelPoint(160, 160), out _);

            Assert.True(g.Release());
            Assert.False(g.IsPressed);
            Assert.False(g.IsDragging);

            // a move after the release belongs to nothing: no press, no drag, no delta
            Assert.False(g.Move(new PixelPoint(400, 400), out var delta));
            Assert.Equal(default, delta);

            // and a second release cannot re-report the drag that was already consumed
            Assert.False(g.Release());
        }

        [Fact]
        public void Release_without_crossing_the_threshold_reports_a_click()
        {
            var g = Pressed();
            g.Move(new PixelPoint(103, 102), out _);

            Assert.False(g.Release()); // the grip ignores this: a click on the dots has no action
            Assert.False(g.IsPressed);
        }

        [Fact]
        public void Release_without_a_press_reports_nothing()
        {
            Assert.False(new TrayDragGesture(Threshold).Release());
        }

        [Fact]
        public void Cancel_while_pressed_but_not_dragging_is_a_no_op_for_the_owner()
        {
            var g = Pressed();
            g.Move(new PixelPoint(102, 102), out _);

            g.Cancel(); // nothing for the owner to end: there was no drag

            Assert.False(g.IsPressed);
            Assert.False(g.IsDragging);
            Assert.False(g.Move(new PixelPoint(400, 400), out _));
            Assert.False(g.Release());
        }

        [Fact]
        public void Cancel_while_dragging_drops_the_drag_so_the_strip_cannot_stay_glued_to_the_pointer()
        {
            var g = Pressed();
            Assert.True(g.Move(new PixelPoint(300, 300), out _));

            g.Cancel();

            Assert.False(g.IsDragging);
            Assert.False(g.IsPressed);
            Assert.False(g.Move(new PixelPoint(500, 500), out _));
        }

        [Fact]
        public void A_new_press_starts_a_clean_gesture()
        {
            var g = Pressed();
            g.Move(new PixelPoint(300, 300), out _);

            g.Press(new PixelPoint(50, 50));
            Assert.True(g.IsPressed);
            Assert.False(g.IsDragging); // the previous drag does not leak in

            Assert.False(g.Move(new PixelPoint(52, 53), out var delta));
            Assert.Equal(new PixelPoint(2, 3), delta); // measured from the NEW press point
        }

        [Fact]
        public void A_scaled_threshold_is_honoured_exactly_as_given()
        {
            // 5 px at 200 % scaling: the grip does the multiplication, the gesture only compares
            var g = new TrayDragGesture(10);
            g.Press(new PixelPoint(0, 0));

            Assert.False(g.Move(new PixelPoint(10, 10), out _));
            Assert.True(g.Move(new PixelPoint(11, 0), out _));
        }
    }
}

using System.Collections.Generic;
using Clowd.PlatformUtil;
using Clowd.UI;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The drag math behind a live share-region resize, pinned rule by rule.
    /// <see cref="ShareRegionGeometry"/> is deliberately free of Avalonia, windows, dispatchers and
    /// timers for exactly the reason <see cref="ShareRegionProtocol"/> is: a resize is the one place
    /// in this feature where the code can silently produce a rectangle that is merely WRONG rather
    /// than obviously broken — off by the rounding, pinned to the wrong edge, or inside-out — and a
    /// wrong rectangle here moves the pixels a meeting is watching. None of those failures throws,
    /// so this file is the only place they are ever caught.
    ///
    /// <para>
    /// Two rules carry most of the weight and both are counter-intuitive:
    /// </para>
    /// <para>
    /// 1. The clamp pushes back the MOVING edge, so the edge the user is not dragging stays exactly
    /// where it was. A naive implementation writes the moving edge straight into <c>X</c> and the
    /// rectangle jumps sideways the moment a drag hits the 64 px floor.
    /// </para>
    /// <para>
    /// 2. When a drag crosses the opposite edge, the two edge VALUES and the which-edge-is-moving
    /// FLAGS have to be swapped together. Swapping only the values — the bug every candidate design
    /// for this feature shipped — leaves the pin on the edge that is no longer anchored, and the
    /// rectangle lands on the wrong side of the pointer.
    /// </para>
    ///
    /// <para>
    /// The clamp itself mirrors the helper's <c>normalize_region</c> (clowd_share_region
    /// <c>mirror.rs</c>): each side floored at 64 and THEN rounded DOWN to even. It is a copy of a
    /// rule that lives in another language in another repository, so these tests are also the
    /// tripwire for the two drifting apart: if the helper's rule changes and this one does not, the
    /// resize preview quietly lies about the rectangle that will actually be applied.
    /// </para>
    /// </summary>
    public class ShareRegionGeometryTests
    {
        /// <summary>Every non-body handle, in the gizmo's numbering. The body is excluded because it
        /// translates rather than resizing and so obeys none of the resize rules.</summary>
        private static readonly int[] ResizeHandles =
        {
            ShareRegionGeometry.HandleTopLeft,
            ShareRegionGeometry.HandleTopRight,
            ShareRegionGeometry.HandleBottomLeft,
            ShareRegionGeometry.HandleBottomRight,
            ShareRegionGeometry.HandleLeft,
            ShareRegionGeometry.HandleTop,
            ShareRegionGeometry.HandleRight,
            ShareRegionGeometry.HandleBottom,
        };

        // ------------------------------------------------------------------ Clamp: the helper's rule, mirrored

        /// <summary>Pins the floor: the helper refuses anything under 64 px a side, so a drag that
        /// squeezes the region smaller has to stop at 64 in the preview too — otherwise the frame
        /// snaps outwards the instant the move is acked.</summary>
        [Fact]
        public void Clamp_floors_each_side_at_64()
        {
            Assert.Equal(new ScreenRect(0, 0, 64, 64), ShareRegionGeometry.Clamp(new ScreenRect(0, 0, 10, 10)));
            Assert.Equal(64, ShareRegionGeometry.MinSide);
        }

        /// <summary>Pins the even rounding, and its direction: DOWN. The helper's encoder needs even
        /// dimensions and it gets there by shrinking, never by growing — a rule that rounded up
        /// would hand back a region one pixel larger than the one the user let go of.</summary>
        [Fact]
        public void Clamp_rounds_the_size_down_to_even()
        {
            var clamped = ShareRegionGeometry.Clamp(new ScreenRect(0, 0, 801, 201));

            Assert.Equal(800, clamped.Width);
            Assert.Equal(200, clamped.Height);
        }

        /// <summary>Pins the ORDER of the two steps, which is where a re-derivation goes wrong: the
        /// floor runs first and the even rounding second. 63 floors up to 64, which is already even
        /// and survives the rounding; 65 clears the floor untouched and then rounds down to 64. An
        /// implementation that rounded first would take 65 to 64 and then leave it — the same answer
        /// — but it would take 63 to 62 and floor it back to 64 only because this floor happens to
        /// be even, which is a coincidence and not a rule.</summary>
        [Fact]
        public void Clamp_applies_the_floor_before_the_even_rounding()
        {
            var clamped = ShareRegionGeometry.Clamp(new ScreenRect(0, 0, 63, 65));

            Assert.Equal(64, clamped.Width);
            Assert.Equal(64, clamped.Height);
        }

        /// <summary>Pins that the clamp is a SIZE rule and nothing more. The helper never clamps the
        /// origin and never clips to the desktop, so neither may this: capture space is the whole
        /// virtual desktop, and a perfectly legal region on a left-hand or upper monitor has a
        /// negative X and Y. Clipping either to zero would drag the region onto the primary
        /// monitor.</summary>
        [Fact]
        public void Clamp_never_moves_the_origin()
        {
            Assert.Equal(new ScreenRect(-1920, -1080, 64, 64), ShareRegionGeometry.Clamp(new ScreenRect(-1920, -1080, 10, 10)));

            // …and a rect that is entirely outside every monitor comes back completely untouched.
            var offDesktop = new ScreenRect(-5000, -5000, 3000, 3000);
            Assert.Equal(offDesktop, ShareRegionGeometry.Clamp(offDesktop));
        }

        /// <summary>Pins idempotence, which is what makes it safe to run an ALREADY-applied region
        /// (one that came back from the helper on a <c>region_changed</c> ack) through the same
        /// clamp again on the next drag. A rule that moved a clamped rect a second time would make
        /// every gesture after the first start from a slightly different rectangle than the one on
        /// screen.</summary>
        [Fact]
        public void Clamp_is_idempotent()
        {
            var sides = new[] { 1, 63, 64, 65, 800, 801 };

            foreach (var width in sides)
            {
                foreach (var height in sides)
                {
                    var once = ShareRegionGeometry.Clamp(new ScreenRect(-7, 13, width, height));
                    var twice = ShareRegionGeometry.Clamp(once);

                    Assert.Equal(once, twice);

                    // and the fixed point it settles on is a region the helper would accept.
                    Assert.True(once.Width >= ShareRegionGeometry.MinSide && once.Width % 2 == 0,
                        $"clamping a width of {width} produced {once.Width}");
                    Assert.True(once.Height >= ShareRegionGeometry.MinSide && once.Height % 2 == 0,
                        $"clamping a height of {height} produced {once.Height}");
                }
            }
        }

        // ------------------------------------------------------------------ ApplyDrag: the body

        /// <summary>Pins that dragging the middle of the region MOVES it and does nothing else. The
        /// size is already legal by construction — it came either from a clamp or from an ack — so a
        /// translation cannot make it illegal, and re-deriving it would only be a chance to lose a
        /// pixel on every step of a long drag.</summary>
        [Fact]
        public void Body_drag_translates_without_resizing()
        {
            var anchor = new ScreenRect(100, 100, 200, 200);

            Assert.Equal(new ScreenRect(137, 113, 200, 200),
                ShareRegionGeometry.ApplyDrag(anchor, ShareRegionGeometry.HandleBody, 37, 13));

            // negative deltas are ordinary: capture space runs left of and above the primary monitor.
            Assert.Equal(new ScreenRect(-4900, -60, 200, 200),
                ShareRegionGeometry.ApplyDrag(anchor, ShareRegionGeometry.HandleBody, -5000, -160));
        }

        // ------------------------------------------------------------------ ApplyDrag: the pinning rule

        /// <summary>Pins the anchored edge for a left-edge drag: the right edge does not move, at
        /// all, ever. That is the whole reason the handles feel attached to the rectangle instead of
        /// sliding it around.</summary>
        [Fact]
        public void Dragging_the_left_edge_pins_the_right_edge()
        {
            var result = ShareRegionGeometry.ApplyDrag(new ScreenRect(100, 100, 200, 200), ShareRegionGeometry.HandleLeft, 10, 0);

            Assert.Equal(new ScreenRect(110, 100, 190, 200), result);
            Assert.Equal(300, result.Right);
        }

        /// <summary>The same rule on the other axis: a top-edge drag pins the bottom.</summary>
        [Fact]
        public void Dragging_the_top_edge_pins_the_bottom_edge()
        {
            var result = ShareRegionGeometry.ApplyDrag(new ScreenRect(100, 100, 200, 200), ShareRegionGeometry.HandleTop, 0, 10);

            Assert.Equal(new ScreenRect(100, 110, 200, 190), result);
            Assert.Equal(300, result.Bottom);
        }

        /// <summary>…and from the far side: a right-edge drag leaves the origin alone entirely. This
        /// is the easy direction — the one a naive implementation already gets right — and it is
        /// here so that a fix aimed at the hard direction cannot quietly break it.</summary>
        [Fact]
        public void Dragging_the_right_edge_pins_the_left_edge()
        {
            var result = ShareRegionGeometry.ApplyDrag(new ScreenRect(100, 100, 200, 200), ShareRegionGeometry.HandleRight, 50, 0);

            Assert.Equal(new ScreenRect(100, 100, 250, 200), result);
            Assert.Equal(100, result.Left);
        }

        /// <summary>Pins that a corner owns exactly two edges: the opposite corner is the anchor and
        /// stays put, so the rectangle grows out of that corner rather than swinging around its
        /// centre.</summary>
        [Fact]
        public void A_corner_drag_moves_exactly_two_edges()
        {
            var anchor = new ScreenRect(100, 100, 200, 200);
            var result = ShareRegionGeometry.ApplyDrag(anchor, ShareRegionGeometry.HandleTopLeft, 20, 30);

            Assert.Equal(anchor.Right, result.Right);
            Assert.Equal(anchor.Bottom, result.Bottom);
            Assert.Equal(new ScreenRect(120, 130, 180, 170), result);
        }

        /// <summary>Pins that an edge handle ignores the other axis completely. The pointer wanders
        /// off the edge it grabbed constantly — a mid-drag <c>dy</c> of tens of pixels is normal —
        /// and none of it may reach the rectangle's height.</summary>
        [Fact]
        public void An_edge_handle_moves_only_its_own_axis()
        {
            var anchor = new ScreenRect(100, 100, 200, 200);
            var result = ShareRegionGeometry.ApplyDrag(anchor, ShareRegionGeometry.HandleLeft, 10, -75);

            Assert.Equal(anchor.Top, result.Top);
            Assert.Equal(anchor.Height, result.Height);
            Assert.Equal(new ScreenRect(110, 100, 190, 200), result);
        }

        // ------------------------------------------------------------------ ApplyDrag: the clamp, still anchored

        /// <summary>
        /// The single most valuable test in the file. Squeezing the left edge 180 px into a 200 px
        /// region asks for a width of 20; the floor makes it 64, and the extra 44 px have to come
        /// off the MOVING edge so that the right edge stays where the user never touched it.
        /// <para>The naive implementation writes the dragged left edge straight into X and produces
        /// X == 180 with a width of 64, which puts the right edge at 244 — 44 px past a boundary
        /// nobody dragged. On screen the rectangle lurches sideways at the exact moment the drag
        /// stops responding, which reads as a broken drag rather than as a size floor.</para>
        /// </summary>
        [Fact]
        public void Shrinking_past_the_minimum_pins_the_anchored_edge_not_the_origin()
        {
            var result = ShareRegionGeometry.ApplyDrag(new ScreenRect(0, 0, 200, 200), ShareRegionGeometry.HandleLeft, 180, 0);

            Assert.Equal(136, result.X);                             // NOT 180
            Assert.Equal(ShareRegionGeometry.MinSide, result.Width);
            Assert.Equal(200, result.Right);                         // the untouched edge, unmoved
        }

        /// <summary>The same rule on Y, because the two axes are written out separately and a fix
        /// applied to one of them is a fix applied to one of them.</summary>
        [Fact]
        public void Shrinking_the_top_past_the_minimum_pins_the_bottom()
        {
            var result = ShareRegionGeometry.ApplyDrag(new ScreenRect(0, 0, 200, 200), ShareRegionGeometry.HandleTop, 0, 180);

            Assert.Equal(136, result.Y);
            Assert.Equal(ShareRegionGeometry.MinSide, result.Height);
            Assert.Equal(200, result.Bottom);
        }

        /// <summary>
        /// Pins where the odd pixel goes when the even rounding bites: onto the moving edge, never
        /// onto the anchored one. Growing the right edge by 1 asks for 201 and rounds back to 200,
        /// so the drag has simply not moved yet and the region is unchanged; growing the left edge
        /// leftwards by 1 does the same thing from the other side. Shrinking the left edge by 1 asks
        /// for 199, which rounds DOWN to 198, and the whole two-pixel step is taken off the left
        /// edge while the right edge stays at 300.
        /// <para>The visible consequence is that edge drags advance in 2 px steps and the anchored
        /// edge never quivers, which is what stops a slow drag from walking the far edge across the
        /// screen one rounding at a time.</para>
        /// </summary>
        [Fact]
        public void An_odd_result_rounds_down_on_the_moving_edge()
        {
            var anchor = new ScreenRect(100, 100, 200, 200);

            // right edge, +1: the requested 201 rounds back to 200, so nothing moves at all.
            Assert.Equal(anchor, ShareRegionGeometry.ApplyDrag(anchor, ShareRegionGeometry.HandleRight, 1, 0));

            // left edge, -1 (growing leftwards): the requested 201 rounds back the same way.
            Assert.Equal(anchor, ShareRegionGeometry.ApplyDrag(anchor, ShareRegionGeometry.HandleLeft, -1, 0));

            // left edge, +1 (shrinking): the requested 199 rounds down to 198, and the left edge —
            // the one being dragged — absorbs the whole step. The right edge does not move.
            var shrunk = ShareRegionGeometry.ApplyDrag(anchor, ShareRegionGeometry.HandleLeft, 1, 0);
            Assert.Equal(new ScreenRect(102, 100, 198, 200), shrunk);
            Assert.Equal(anchor.Right, shrunk.Right);
        }

        // ------------------------------------------------------------------ ApplyDrag: crossing an edge

        /// <summary>
        /// The bug every candidate design for this feature shipped. Dragging the right edge 1000 px
        /// leftwards on a 200 px region takes it 700 px past the left edge, and at that moment the
        /// two edges exchange roles: the edge the user grabbed is now the LEFT edge of the
        /// rectangle, and the edge that was the left one is now the anchor on the right.
        /// <para>Swapping the values alone and then pinning by the ORIGINAL handle id leaves X at
        /// 100 — the rectangle stays where it was and grows to the right, away from the pointer,
        /// which is exactly backwards. Swapping the moving-edge flags together with the values is
        /// the fix.</para>
        /// </summary>
        [Fact]
        public void Dragging_the_right_edge_past_the_left_flips_which_edge_is_anchored()
        {
            var result = ShareRegionGeometry.ApplyDrag(new ScreenRect(100, 0, 200, 200), ShareRegionGeometry.HandleRight, -1000, 0);

            Assert.Equal(100, result.Right);    // the old LEFT edge is the anchor now
            Assert.Equal(-700, result.X);       // NOT 100
            Assert.Equal(800, result.Width);
            Assert.Equal(new ScreenRect(-700, 0, 800, 200), result);
        }

        /// <summary>The mirror image, on both members of the left/top pair: dragging a leading edge
        /// past its trailing one leaves the trailing edge anchored exactly where it was.</summary>
        [Fact]
        public void Dragging_the_left_edge_past_the_right_flips_which_edge_is_anchored()
        {
            var horizontal = ShareRegionGeometry.ApplyDrag(new ScreenRect(100, 0, 200, 200), ShareRegionGeometry.HandleLeft, 1000, 0);

            Assert.Equal(300, horizontal.Left);     // the old RIGHT edge is the anchor now
            Assert.Equal(1100, horizontal.Right);
            Assert.Equal(new ScreenRect(300, 0, 800, 200), horizontal);

            var vertical = ShareRegionGeometry.ApplyDrag(new ScreenRect(0, 100, 200, 200), ShareRegionGeometry.HandleTop, 0, 1000);

            Assert.Equal(300, vertical.Top);        // the old BOTTOM edge is the anchor now
            Assert.Equal(1100, vertical.Bottom);
            Assert.Equal(new ScreenRect(0, 300, 200, 800), vertical);
        }

        /// <summary>
        /// Pins that a corner dragged clean past the opposite corner — both axes crossing at once —
        /// still comes out as an ordinary, positively-sized rectangle.
        /// <para>It has to be asserted on the extents rather than on <c>IsEmpty()</c>:
        /// <see cref="ScreenRect.IsEmpty"/> only tests <c>Width == 0 &amp;&amp; Height == 0</c> and
        /// <see cref="ScreenRect.FromLTRB"/> normalizes nothing, so an inside-out rectangle with a
        /// negative width passes every guard downstream and is not noticed until the region reaches
        /// the helper.</para>
        /// </summary>
        [Fact]
        public void Dragging_a_corner_past_the_opposite_corner_stays_normalized()
        {
            var result = ShareRegionGeometry.ApplyDrag(new ScreenRect(0, 0, 200, 200), ShareRegionGeometry.HandleTopLeft, 1000, 1000);

            Assert.True(result.Width > 0, $"the width came out {result.Width}");
            Assert.True(result.Height > 0, $"the height came out {result.Height}");
            Assert.True(result.Right > result.Left, $"right {result.Right} is not past left {result.Left}");
            Assert.True(result.Bottom > result.Top, $"bottom {result.Bottom} is not past top {result.Top}");
            Assert.False(result.IsEmpty());
            Assert.Equal(new ScreenRect(200, 200, 800, 800), result);
        }

        /// <summary>Pins the whole normalization story as a property rather than as a handful of
        /// cases: over every handle and a spread of deltas that cross, overshoot and collapse the
        /// rectangle, the result is ALWAYS a region the helper would accept. Anything that escapes
        /// this — a negative extent, an odd side, a side under the floor — is a rectangle that
        /// reaches <c>move</c> and comes back as a <c>command_error</c>, which the UI can only
        /// report as a failed resize.</summary>
        [Fact]
        public void A_crossing_drag_never_produces_a_negative_extent()
        {
            var anchor = new ScreenRect(100, 100, 200, 200);
            var deltas = new[] { -5000, -201, -1, 0, 1, 201, 5000 };

            foreach (var handle in ResizeHandles)
            {
                foreach (var dx in deltas)
                {
                    foreach (var dy in deltas)
                    {
                        var result = ShareRegionGeometry.ApplyDrag(anchor, handle, dx, dy);
                        var context = $"handle {handle}, delta ({dx},{dy}) produced {result.Width}x{result.Height}";

                        Assert.True(result.Width >= ShareRegionGeometry.MinSide, context);
                        Assert.True(result.Height >= ShareRegionGeometry.MinSide, context);
                        Assert.True(result.Width % 2 == 0, context);
                        Assert.True(result.Height % 2 == 0, context);
                    }
                }
            }
        }

        // ------------------------------------------------------------------ anchoring

        /// <summary>
        /// Pins the property that makes the gesture survive a hostile stream of pointer events: the
        /// result depends on the press-time anchor and the current delta and on NOTHING ELSE — not
        /// on the previous step, not on how many steps there have been, not on the order they
        /// arrived in.
        /// <para>That is what a static method with no state gives for free and what an accumulating
        /// implementation gives up. Accumulated deltas drift whenever a pointer-move event is
        /// dropped, and they drift on every single step of a drag on this overlay, because the
        /// window is repositioned under the pointer as the preview follows the rectangle. This test
        /// is the type-level version of that guarantee.</para>
        /// </summary>
        [Fact]
        public void Drag_is_a_pure_function_of_the_press_time_anchor()
        {
            var anchor = new ScreenRect(100, 100, 200, 200);
            var noise = new[] { -900, -13, 0, 7, 640 };
            var handles = new List<int>(ResizeHandles) { ShareRegionGeometry.HandleBody };

            foreach (var handle in handles)
            {
                var first = ShareRegionGeometry.ApplyDrag(anchor, handle, 30, 40);

                // an arbitrary history of other drags on the same anchor…
                foreach (var d in noise)
                    ShareRegionGeometry.ApplyDrag(anchor, handle, d, -d);

                var again = ShareRegionGeometry.ApplyDrag(anchor, handle, 30, 40);

                Assert.Equal(first, again);

                // …and none of it touched the anchor. ScreenRect is a record with init-only members
                // today, so this cannot fail now — it is here because a future refactor to a mutable
                // carrier would otherwise break the anchoring rule silently.
                Assert.Equal(new ScreenRect(100, 100, 200, 200), anchor);
            }
        }
    }
}

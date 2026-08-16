using System;
using Clowd.UI.VideoEditor.Timeline;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    // TimelineViewMath / TimelineViewport are the pure zoom-scroll math behind the multi-track
    // timeline (Clowd.Ui exposes its internals to this project via InternalsVisibleTo). No Avalonia
    // runtime is needed: neither class touches a UI type.
    public class TimelineViewMathTests
    {
        private const long OneSecond = TimeSpan.TicksPerSecond;
        private const long TenMinutes = TimeSpan.TicksPerMinute * 10;

        // ---------------------------------------------------------------- tick <-> pixel mapping

        [Theory]
        [InlineData(0L, 0L, 100_000d, 0d)]                       // origin, no scroll
        [InlineData(OneSecond, 0L, 100_000d, 100d)]              // 1s at 100_000 ticks/px = 100 px
        [InlineData(OneSecond, OneSecond, 100_000d, 0d)]         // scrolled to the tick itself
        [InlineData(0L, OneSecond, 100_000d, -100d)]             // before the viewport is negative
        public void TickToX_maps_linearly(long ticks, long scroll, double tpp, double expected)
        {
            Assert.Equal(expected, TimelineViewMath.TickToX(ticks, scroll, tpp), 6);
        }

        [Fact]
        public void TickToX_degenerate_zoom_collapses_to_origin()
        {
            Assert.Equal(0, TimelineViewMath.TickToX(OneSecond, 0, 0));
            Assert.Equal(0, TimelineViewMath.TickToX(OneSecond, 0, -5));
        }

        [Fact]
        public void XToTicks_is_unclamped_and_scroll_relative()
        {
            Assert.Equal(OneSecond, TimelineViewMath.XToTicks(100, 0, 100_000));
            Assert.Equal(-OneSecond, TimelineViewMath.XToTicks(-100, 0, 100_000));
            Assert.Equal(3 * OneSecond, TimelineViewMath.XToTicks(100, 2 * OneSecond, 100_000));
        }

        [Fact]
        public void XToTicksClamped_clamps_to_the_project()
        {
            Assert.Equal(0, TimelineViewMath.XToTicksClamped(-500, 0, 100_000, 5 * OneSecond));
            Assert.Equal(5 * OneSecond, TimelineViewMath.XToTicksClamped(50_000, 0, 100_000, 5 * OneSecond));
            Assert.Equal(0, TimelineViewMath.XToTicksClamped(100, 0, 100_000, 0));
        }

        [Fact]
        public void Roundtrip_tick_to_x_to_tick_is_stable_under_zoom_and_scroll()
        {
            foreach (var tpp in new double[] { 1_000, 12_345, 100_000, 3_000_000 })
            {
                var scroll = (long)(tpp * 137);
                for (long ticks = scroll; ticks < scroll + (long)(tpp * 900); ticks += (long)(tpp * 37))
                {
                    var x = TimelineViewMath.TickToX(ticks, scroll, tpp);
                    var back = TimelineViewMath.XToTicks(x, scroll, tpp);
                    Assert.InRange(back, ticks - 1, ticks + 1); // sub-tick rounding only
                }
            }
        }

        // ------------------------------------------------------------------------- anchored zoom

        [Fact]
        public void ScrollForAnchoredZoom_keeps_the_anchor_tick_under_the_pointer()
        {
            const long scroll = 3 * TimeSpan.TicksPerMinute;
            const double anchorX = 420;
            var tpp = 100_000d;
            var anchorTicks = scroll + (long)(anchorX * tpp);

            var currentScroll = scroll;
            for (var step = 0; step < 8; step++)
            {
                var next = tpp * 0.7;
                currentScroll = TimelineViewMath.ScrollForAnchoredZoom(currentScroll, tpp, next, anchorX);
                tpp = next;

                Assert.Equal(anchorX, TimelineViewMath.TickToX(anchorTicks, currentScroll, tpp), 3);
            }
        }

        [Fact]
        public void ScrollForAnchoredZoom_degenerate_zoom_keeps_the_scroll()
        {
            Assert.Equal(500, TimelineViewMath.ScrollForAnchoredZoom(500, 0, 100_000, 10));
            Assert.Equal(500, TimelineViewMath.ScrollForAnchoredZoom(500, 100_000, 0, 10));
        }

        // ------------------------------------------------------------------------ zoom clamping

        [Fact]
        public void ClampZoom_floor_is_a_tenth_of_a_millisecond_per_pixel()
        {
            var clamped = TimelineViewMath.ClampZoom(1, TenMinutes, 1000);
            Assert.Equal(TimelineViewMath.MinTicksPerPixel, clamped);
            Assert.Equal(TimeSpan.TicksPerMillisecond / 10.0, clamped);
        }

        [Fact]
        public void ClampZoom_ceiling_fits_the_whole_duration_in_the_viewport()
        {
            // zooming out past this would leave dead space after the end of the project.
            const long duration = 60 * OneSecond;
            var fit = TimelineViewMath.ClampZoom(Double.MaxValue, duration, 800);

            Assert.Equal(duration / 800.0, fit, 6);
            // and at that zoom the project spans exactly the viewport
            Assert.Equal(800, TimelineViewMath.TickToX(duration, 0, fit), 6);
        }

        [Fact]
        public void ClampZoom_nonpositive_resolves_to_the_default_zoom()
        {
            const long duration = 60 * TimeSpan.TicksPerMinute; // far too long to fit at the default
            Assert.Equal(TimelineViewMath.DefaultTicksPerPixel, TimelineViewMath.ClampZoom(0, duration, 800));
            Assert.Equal(TimelineViewMath.DefaultTicksPerPixel, TimelineViewMath.ClampZoom(-1, duration, 800));
            Assert.Equal(TimelineViewMath.DefaultTicksPerPixel, TimelineViewMath.ClampZoom(Double.NaN, duration, 800));
        }

        [Fact]
        public void The_default_zoom_is_fifty_pixels_per_second_and_still_yields_to_fit()
        {
            Assert.Equal(50, TimeSpan.TicksPerSecond / TimelineViewMath.DefaultTicksPerPixel, 6);

            // a two-second project across 800 px: the default scale would show it in 100 px and
            // trail 700 px of nothing, so fit wins.
            const long duration = 2 * OneSecond;
            Assert.Equal(duration / 800.0,
                TimelineViewMath.ClampZoom(TimelineViewMath.DefaultTicksPerPixel, duration, 800), 6);
        }

        [Fact]
        public void FitTicksPerPixel_stays_inside_the_zoom_range()
        {
            // no duration or no width yet: the absolute backstop, not a fit nobody can compute
            Assert.Equal(TimelineViewMath.MaxTicksPerPixel, TimelineViewMath.FitTicksPerPixel(0, 800));
            Assert.Equal(TimelineViewMath.MaxTicksPerPixel, TimelineViewMath.FitTicksPerPixel(TenMinutes, 0));

            const long duration = 60 * OneSecond;
            Assert.Equal(duration / 800.0, TimelineViewMath.FitTicksPerPixel(duration, 800), 6);

            // a 1 ms project across 800 px would "fit" at 12.5 ticks/px — past the zoom floor.
            Assert.Equal(TimelineViewMath.MinTicksPerPixel,
                TimelineViewMath.FitTicksPerPixel(TimeSpan.TicksPerMillisecond, 800));
        }

        // --------------------------------------------------------------------- pixel alignment

        [Fact]
        public void SnapToPixel_puts_even_strokes_and_fill_edges_on_the_pixel_boundary()
        {
            // 100% scaling: a 2 px stroke centred at a whole coordinate covers two whole pixels.
            Assert.Equal(412, TimelineViewMath.SnapToPixel(411.6, 1, 2));
            Assert.Equal(412, TimelineViewMath.SnapToPixel(411.6, 1));

            // 150% scaling: whole DEVICE pixels, so the result can be a fraction of a DIP.
            Assert.Equal(411 + 2 / 3.0, TimelineViewMath.SnapToPixel(411.6, 1.5, 2), 9);
        }

        [Fact]
        public void SnapToPixel_straddles_the_pixel_centre_for_odd_strokes()
        {
            // a 1 px hairline centred on a boundary would light two half pixels
            Assert.Equal(411.5, TimelineViewMath.SnapToPixel(411.6, 1, 1));
            Assert.Equal(411.5, TimelineViewMath.SnapToPixel(411.2, 1, 1));
        }

        [Fact]
        public void SnapToPixel_treats_a_missing_scaling_as_one_to_one()
        {
            Assert.Equal(412, TimelineViewMath.SnapToPixel(411.6, 0, 2));
            Assert.Equal(412, TimelineViewMath.SnapToPixel(411.6, Double.NaN, 2));
        }

        // ---------------------------------------------------------------------- scroll clamping

        [Fact]
        public void ClampScroll_floors_at_the_origin()
        {
            Assert.Equal(0, TimelineViewMath.ClampScroll(-1, 100_000, TenMinutes, 1000));
        }

        [Fact]
        public void ClampScroll_stops_a_viewport_short_of_the_end_plus_overscroll()
        {
            const double tpp = 100_000;
            const double width = 1000;
            var max = TimelineViewMath.MaxScrollTicks(tpp, TenMinutes, width);

            Assert.Equal((long)(TenMinutes - width * tpp + TimelineViewMath.OverscrollPx * tpp), max);
            Assert.Equal(max, TimelineViewMath.ClampScroll(Int64.MaxValue, tpp, TenMinutes, width));

            // the overscroll is small: a fraction of the viewport, not another screenful
            Assert.True(TimelineViewMath.OverscrollPx * tpp < width * tpp * 0.1);
        }

        [Fact]
        public void ClampScroll_pins_at_zero_when_everything_fits()
        {
            // fit zoom: the whole project is on screen, so there is nothing to scroll to.
            var fit = TimelineViewMath.FitTicksPerPixel(TenMinutes, 1000);
            Assert.Equal(0, TimelineViewMath.MaxScrollTicks(fit, TenMinutes, 1000));
            Assert.Equal(0, TimelineViewMath.ClampScroll(5_000, fit, TenMinutes, 1000));
        }

        // ------------------------------------------------------------------------ ruler steps

        [Theory]
        [InlineData(1_000d, TimeSpan.TicksPerMillisecond * 100)]   // 0.1 ms/px -> 100 ms steps
        [InlineData(100_000d, TimeSpan.TicksPerSecond)]            // 1 s per 100 px -> 1 s steps
        [InlineData(500_000d, TimeSpan.TicksPerSecond * 5)]        // 5 s steps
        [InlineData(2_000_000d, TimeSpan.TicksPerSecond * 30)]     // 30 s steps
        [InlineData(6_000_000d, TimeSpan.TicksPerMinute)]          // 1 min steps
        public void PickTickStepTicks_uses_the_ladder(double ticksPerPixel, long expected)
        {
            Assert.Equal(expected, TimelineViewMath.PickTickStepTicks(ticksPerPixel));
        }

        [Fact]
        public void PickTickStepTicks_sub_second_steps_appear_when_zoomed_in()
        {
            // 25 ms across 70 px needs the 250 ms step; 45 ms needs 500 ms.
            Assert.Equal(TimeSpan.TicksPerMillisecond * 250, TimelineViewMath.PickTickStepTicks(25_000));
            Assert.Equal(TimeSpan.TicksPerMillisecond * 500, TimelineViewMath.PickTickStepTicks(45_000));
        }

        [Fact]
        public void PickTickStepTicks_keeps_the_spacing_invariant_across_six_decades_of_zoom()
        {
            var tpp = TimelineViewMath.MinTicksPerPixel;
            for (var decade = 0; decade < 7; decade++)
            {
                foreach (var multiplier in new double[] { 1, 2.3, 5.7 })
                {
                    var zoom = tpp * multiplier;
                    var step = TimelineViewMath.PickTickStepTicks(zoom);

                    Assert.True(step > 0);
                    var spacing = step / zoom;
                    Assert.True(spacing >= TimelineViewMath.DefaultTickSpacingPx,
                        $"step {step} at {zoom} ticks/px is only {spacing:F1} px apart");

                    // and never absurdly sparse: whenever a smaller ladder step existed it was
                    // rejected for crowding, so the spacing stays within the largest ladder ratio
                    // (1s -> 5s) of the minimum. The 100 ms floor is exempt: at maximum zoom there
                    // is nothing finer to fall back to.
                    if (step > TimeSpan.TicksPerMillisecond * 100)
                    {
                        Assert.True(spacing < TimelineViewMath.DefaultTickSpacingPx * 5.5,
                            $"step {step} at {zoom} ticks/px is {spacing:F1} px apart");
                    }
                }

                tpp *= 10;
            }
        }

        [Fact]
        public void PickTickStepTicks_doubles_past_a_minute()
        {
            var step = TimelineViewMath.PickTickStepTicks(TimeSpan.TicksPerMinute); // 1 min/px
            Assert.True(step > TimeSpan.TicksPerMinute);
            Assert.Equal(0, step % TimeSpan.TicksPerMinute);
        }

        [Fact]
        public void PickTickStepTicks_degenerate_input_returns_zero()
        {
            Assert.Equal(0, TimelineViewMath.PickTickStepTicks(0));
            Assert.Equal(0, TimelineViewMath.PickTickStepTicks(-1));
            Assert.Equal(0, TimelineViewMath.PickTickStepTicks(100_000, 0));
        }

        [Theory]
        [InlineData(0L, "0:00")]
        [InlineData(65 * TimeSpan.TicksPerSecond, "1:05")]
        [InlineData(TimeSpan.TicksPerHour, "1:00:00")]
        public void FormatTick_formats_mmss_and_hours(long ticks, string expected)
        {
            Assert.Equal(expected, TimelineViewMath.FormatTick(ticks));
        }

        [Fact]
        public void FormatTick_shows_tenths_for_sub_second_steps()
        {
            var step = TimeSpan.TicksPerMillisecond * 250;
            Assert.Equal("0:01.5", TimelineViewMath.FormatTick(15_000_000, step));
            Assert.Equal("1:00:00.0", TimelineViewMath.FormatTick(TimeSpan.TicksPerHour, step));
        }

        // ------------------------------------------------------------------------------ snapping

        [Fact]
        public void Snap_returns_the_nearest_target_inside_the_tolerance()
        {
            var targets = new long[] { 0, 1_000, 5_000, 9_000 };
            Assert.Equal(5_000, TimelineViewMath.Snap(5_040, targets, 100));
            Assert.Equal(9_000, TimelineViewMath.Snap(8_950, targets, 100));
        }

        [Fact]
        public void Snap_returns_null_outside_the_tolerance()
        {
            var targets = new long[] { 0, 1_000, 5_000 };
            Assert.Null(TimelineViewMath.Snap(3_000, targets, 100));
            Assert.Null(TimelineViewMath.Snap(5_101, targets, 100));
            // boundary is inclusive
            Assert.Equal(5_000, TimelineViewMath.Snap(5_100, targets, 100));
        }

        [Fact]
        public void Snap_ties_go_to_the_earlier_target()
        {
            var targets = new long[] { 100, 120 };
            Assert.Equal(100, TimelineViewMath.Snap(110, targets, 10));
        }

        [Fact]
        public void Snap_without_targets_is_null()
        {
            Assert.Null(TimelineViewMath.Snap(50, null, 100));
            Assert.Null(TimelineViewMath.Snap(50, Array.Empty<long>(), 100));
        }

        [Fact]
        public void ToleranceTicks_scales_the_pointer_slop_with_zoom()
        {
            Assert.Equal((long)(TimelineViewMath.HitTolerance * 100_000), TimelineViewMath.ToleranceTicks(100_000));
            Assert.Equal(0, TimelineViewMath.ToleranceTicks(0));
        }

        // ------------------------------------------------------------------------------ viewport

        private static TimelineViewport MakeViewport(long duration = TenMinutes, double width = 1000)
        {
            var viewport = new TimelineViewport();
            viewport.SetViewportWidth(width);
            viewport.SetDuration(duration);
            return viewport;
        }

        [Fact]
        public void Viewport_anchored_zoom_keeps_the_anchor_tick_stationary()
        {
            var viewport = MakeViewport();
            viewport.ScrollToTicks(3 * TimeSpan.TicksPerMinute);

            const double anchorX = 420;
            var anchorTicks = viewport.XToTicks(anchorX);

            for (var step = 0; step < 6; step++)
            {
                viewport.SetZoomAnchored(viewport.TicksPerPixel * 0.6, anchorX);
                Assert.Equal(anchorX, viewport.TickToX(anchorTicks), 3);
            }

            for (var step = 0; step < 6; step++)
            {
                viewport.SetZoomAnchored(viewport.TicksPerPixel * 1.4, anchorX);
                Assert.Equal(anchorX, viewport.TickToX(anchorTicks), 3);
            }
        }

        [Fact]
        public void Viewport_anchored_zoom_clamps_out_of_range_anchors_to_the_viewport_edges()
        {
            // a Ctrl+wheel over the track-header column arrives with a negative surface x; zooming
            // around that off-screen tick would pan the view toward the origin on every notch. The
            // viewport anchors at the left edge instead, so the scroll stays put.
            var viewport = MakeViewport();
            viewport.SetZoomAnchored(100_000, 0);
            viewport.ScrollToTicks(3 * TimeSpan.TicksPerMinute);
            var scroll = viewport.ScrollTicks;

            viewport.SetZoomAnchored(viewport.TicksPerPixel * 0.5, -80);
            Assert.Equal(scroll, viewport.ScrollTicks);

            // and past the right edge (the vertical scroll bar) behaves as the right border
            var rightTick = viewport.XToTicks(1000);
            viewport.SetZoomAnchored(viewport.TicksPerPixel * 0.5, 1400);
            Assert.Equal(1000, viewport.TickToX(rightTick), 3);
        }

        [Fact]
        public void Viewport_clamps_zoom_to_fit_all_and_to_the_floor()
        {
            var viewport = MakeViewport();

            viewport.SetZoomAnchored(Double.MaxValue, 0);
            Assert.Equal(TenMinutes / 1000.0, viewport.TicksPerPixel, 6);

            viewport.SetZoomAnchored(1, 0);
            Assert.Equal(TimelineViewMath.MinTicksPerPixel, viewport.TicksPerPixel);
        }

        [Fact]
        public void Viewport_resizing_keeps_a_zoom_that_still_fits()
        {
            // a resize is not a rescale: while the view is tighter than fit, one second stays the
            // same number of pixels wide and only the visible span changes.
            var viewport = MakeViewport();
            viewport.SetZoomAnchored(100_000, 0); // 10 ms/px, far tighter than fit
            var zoom = viewport.TicksPerPixel;

            viewport.SetViewportWidth(2400);
            Assert.Equal(zoom, viewport.TicksPerPixel);
            Assert.Equal(2400 * zoom, viewport.VisibleTicks, 0);

            viewport.SetViewportWidth(300);
            Assert.Equal(zoom, viewport.TicksPerPixel);
        }

        [Fact]
        public void Viewport_widening_pulls_a_fitted_zoom_in_so_the_project_still_fills_it()
        {
            // zoomed all the way out, the project exactly fills the viewport. Widening the window
            // must not leave dead space after the end of it, so the zoom follows the new fit.
            var viewport = MakeViewport();
            viewport.SetZoomAnchored(Double.MaxValue, 0);
            Assert.Equal(1000, viewport.TickToX(TenMinutes), 6);

            viewport.SetViewportWidth(2000);

            Assert.Equal(TenMinutes / 2000.0, viewport.TicksPerPixel, 6);
            Assert.Equal(2000, viewport.TickToX(TenMinutes), 6);
        }

        [Fact]
        public void Viewport_reset_zoom_returns_to_the_default_scale()
        {
            var viewport = MakeViewport();
            viewport.SetZoomAnchored(TimelineViewMath.MinTicksPerPixel, 0);
            viewport.ScrollToTicks(TimeSpan.TicksPerMinute);

            viewport.ResetZoom();

            Assert.Equal(TimelineViewMath.DefaultTicksPerPixel, viewport.TicksPerPixel);
        }

        [Fact]
        public void Viewport_reset_zoom_keeps_the_anchor_tick_under_the_pointer()
        {
            var viewport = MakeViewport();
            viewport.SetZoomAnchored(TimelineViewMath.MinTicksPerPixel, 0);
            viewport.ScrollToTicks(TimeSpan.TicksPerMinute);

            const double anchorX = 400;
            var anchorTicks = viewport.XToTicks(anchorX);

            viewport.ResetZoom(anchorX);

            Assert.Equal(anchorX, viewport.TickToX(anchorTicks), 3);
        }

        [Fact]
        public void Viewport_zoom_to_fit_shows_the_whole_project_from_the_origin()
        {
            var viewport = MakeViewport();
            viewport.SetZoomAnchored(TimelineViewMath.MinTicksPerPixel, 0);
            viewport.ScrollToTicks(TimeSpan.TicksPerMinute);

            viewport.ZoomToFit();

            Assert.Equal(0, viewport.ScrollTicks);
            Assert.Equal(1000, viewport.TickToX(TenMinutes), 6);
        }

        [Fact]
        public void Viewport_scroll_is_clamped_at_both_ends()
        {
            var viewport = MakeViewport();
            viewport.SetZoomAnchored(100_000, 0);

            viewport.ScrollBy(-TimeSpan.TicksPerHour);
            Assert.Equal(0, viewport.ScrollTicks);

            viewport.ScrollBy(TimeSpan.TicksPerHour);
            Assert.Equal(TimelineViewMath.MaxScrollTicks(100_000, TenMinutes, 1000), viewport.ScrollTicks);
        }

        [Fact]
        public void Viewport_shrinking_the_duration_reclamps_zoom_and_scroll()
        {
            var viewport = MakeViewport();
            viewport.SetZoomAnchored(100_000, 0);
            viewport.ScrollToTicks(5 * TimeSpan.TicksPerMinute);

            // the recording was trimmed down to five seconds: the old zoom is now further out than
            // fit-all and the old scroll is way past the end.
            viewport.SetDuration(5 * OneSecond);

            Assert.Equal(5 * OneSecond / 1000.0, viewport.TicksPerPixel, 6);
            Assert.Equal(0, viewport.ScrollTicks);
        }

        [Fact]
        public void Viewport_ensure_visible_scrolls_the_minimum_and_leaves_a_margin()
        {
            var viewport = MakeViewport();
            viewport.SetZoomAnchored(100_000, 0); // 10 s visible across 1000 px
            var span = viewport.VisibleTicks;

            // already comfortably inside: no movement
            viewport.EnsureVisible(span / 2);
            Assert.Equal(0, viewport.ScrollTicks);

            // just past the right edge: scrolls so the playhead sits a margin short of it
            var target = span + OneSecond;
            viewport.EnsureVisible(target);
            Assert.InRange(viewport.TickToX(target), 0, 1000);
            Assert.True(viewport.TickToX(target) < 1000 - 10);

            // behind the left edge: scrolls back
            viewport.EnsureVisible(0);
            Assert.Equal(0, viewport.ScrollTicks);
        }

        [Fact]
        public void Viewport_raises_changed_once_per_effective_change()
        {
            var viewport = MakeViewport();
            var changes = 0;
            viewport.Changed += (_, _) => changes++;

            viewport.ScrollToTicks(TimeSpan.TicksPerMinute);
            Assert.Equal(1, changes);

            viewport.ScrollToTicks(TimeSpan.TicksPerMinute); // same value
            Assert.Equal(1, changes);

            viewport.SetViewportWidth(1000); // same width
            Assert.Equal(1, changes);

            viewport.SetZoomAnchored(viewport.TicksPerPixel, 100); // same zoom
            Assert.Equal(1, changes);

            viewport.SetZoomAnchored(viewport.TicksPerPixel / 2, 100);
            Assert.Equal(2, changes);
        }
    }
}

using System;
using Clowd.VideoSDK.Composition;
using Clowd.VideoSDK.Model;
using SkiaSharp;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The zoom compositing math: per-item scale-about-focus matrices with edge clamping, ramp
    /// easing of the zoom factor, and the accumulated per-track matrix (order scoping, hidden
    /// tracks, multiplicative stacking topmost-outermost).
    /// </summary>
    public class ZoomMathTests
    {
        private const long Sec = 10_000_000; // 100ns ticks
        private const int W = 1920;
        private const int H = 1080;

        private static Item ZoomItem(Guid trackId, long start, long duration,
            double zoom = 2.0, double focusX = 0.5, double focusY = 0.5) => new Item
        {
            Id = Guid.NewGuid(),
            TrackId = trackId,
            TimelineStartTicks = start,
            DurationTicks = duration,
            Content = new ZoomContent { Zoom = zoom, FocusX = focusX, FocusY = focusY },
        };

        private static Project ProjectWith(params (Track Track, Item[] Items)[] rows)
        {
            var project = new Project
            {
                Output = new OutputSettings { WidthPx = W, HeightPx = H, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
            };
            foreach (var (track, items) in rows)
            {
                project.Tracks.Add(track);
                foreach (var item in items)
                    project.Items.Add(item);
            }
            return project;
        }

        private static Track VideoTrack(int order) =>
            new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Video", Order = order };

        private static Track ZoomTrack(int order, bool hidden = false) =>
            new Track { Id = Guid.NewGuid(), Kind = TrackKind.Effect, Name = "Zoom", Order = order, Hidden = hidden };

        // ------------------------------------------------------------------------- item matrix

        [Fact]
        public void Item_matrix_is_identity_at_factor_one_or_below()
        {
            Assert.True(ZoomMath.ItemMatrix(1.0, 0.5, 0.5, W, H).IsIdentity);
            Assert.True(ZoomMath.ItemMatrix(0.5, 0.5, 0.5, W, H).IsIdentity);
        }

        [Theory]
        [InlineData(2.0, 0.5, 0.5)]
        [InlineData(1.5, 0.4, 0.6)]
        [InlineData(3.0, 0.5, 0.25)]
        public void Focal_point_maps_to_itself_when_clamp_is_not_binding(double z, double fx, double fy)
        {
            var m = ZoomMath.ItemMatrix(z, fx, fy, W, H);
            var p = m.MapPoint((float)(fx * W), (float)(fy * H));
            Assert.Equal(fx * W, p.X, 2);
            Assert.Equal(fy * H, p.Y, 2);
        }

        [Fact]
        public void Corner_focus_pins_that_canvas_edge()
        {
            // focus top-left: no translation — (0,0) stays put
            var m = ZoomMath.ItemMatrix(2.0, 0, 0, W, H);
            Assert.Equal(0, m.TransX);
            Assert.Equal(0, m.TransY);

            // focus bottom-right: the far corner stays put
            m = ZoomMath.ItemMatrix(2.0, 1, 1, W, H);
            var p = m.MapPoint(W, H);
            Assert.Equal(W, p.X, 2);
            Assert.Equal(H, p.Y, 2);
        }

        [Theory]
        [InlineData(1.25, 0.0, 1.0)]
        [InlineData(2.0, 0.1, 0.9)]
        [InlineData(5.0, 0.5, 0.5)]
        [InlineData(2.0, -0.5, 1.5)] // out-of-range focus clamps rather than exposing edges
        public void Scaled_canvas_always_covers_the_viewport(double z, double fx, double fy)
        {
            var m = ZoomMath.ItemMatrix(z, fx, fy, W, H);
            var topLeft = m.MapPoint(0, 0);
            var bottomRight = m.MapPoint(W, H);

            Assert.True(topLeft.X <= 0.001);
            Assert.True(topLeft.Y <= 0.001);
            Assert.True(bottomRight.X >= W - 0.001);
            Assert.True(bottomRight.Y >= H - 0.001);
        }

        // ------------------------------------------------------------------------- ramp factor

        [Fact]
        public void Factor_is_one_outside_the_item_span()
        {
            var item = ZoomItem(Guid.NewGuid(), 2 * Sec, 6 * Sec, zoom: 3.0);
            Assert.Equal(1, ZoomMath.FactorAt(item, 0));
            Assert.Equal(1, ZoomMath.FactorAt(item, 2 * Sec - 1));
            Assert.Equal(1, ZoomMath.FactorAt(item, 8 * Sec)); // end tick is exclusive
            Assert.Equal(1, ZoomMath.FactorAt(item, 9 * Sec));
        }

        [Fact]
        public void Factor_holds_the_target_zoom_without_ramps()
        {
            var item = ZoomItem(Guid.NewGuid(), 2 * Sec, 6 * Sec, zoom: 3.0);
            Assert.Equal(3.0, ZoomMath.FactorAt(item, 2 * Sec));
            Assert.Equal(3.0, ZoomMath.FactorAt(item, 5 * Sec));
            Assert.Equal(3.0, ZoomMath.FactorAt(item, 8 * Sec - 1));
        }

        [Fact]
        public void Entry_ramp_eases_one_to_zoom()
        {
            var item = ZoomItem(Guid.NewGuid(), 2 * Sec, 6 * Sec, zoom: 3.0);
            item.Entry = new Transition { Kind = TransitionKind.Ramp, DurationTicks = 1 * Sec, Easing = TransitionEasing.Linear };

            Assert.Equal(1.0, ZoomMath.FactorAt(item, 2 * Sec));                    // ramp start
            Assert.Equal(2.0, ZoomMath.FactorAt(item, 2 * Sec + Sec / 2), 10);      // linear mid
            Assert.Equal(3.0, ZoomMath.FactorAt(item, 3 * Sec));                    // ramp end
            Assert.Equal(3.0, ZoomMath.FactorAt(item, 6 * Sec));                    // middle
        }

        [Fact]
        public void Exit_ramp_eases_zoom_back_to_one()
        {
            var item = ZoomItem(Guid.NewGuid(), 2 * Sec, 6 * Sec, zoom: 3.0);
            item.Exit = new Transition { Kind = TransitionKind.Ramp, DurationTicks = 1 * Sec, Easing = TransitionEasing.Linear };

            Assert.Equal(3.0, ZoomMath.FactorAt(item, 7 * Sec));                    // ramp start
            Assert.Equal(2.0, ZoomMath.FactorAt(item, 7 * Sec + Sec / 2), 10);      // linear mid
            Assert.Equal(1.0, ZoomMath.FactorAt(item, 8 * Sec - 1), 5);             // last composed tick
        }

        [Fact]
        public void Ramp_progress_uses_the_transition_easing()
        {
            var item = ZoomItem(Guid.NewGuid(), 0, 10 * Sec, zoom: 3.0);
            item.Entry = new Transition { Kind = TransitionKind.Ramp, DurationTicks = 2 * Sec, Easing = TransitionEasing.CubicIn };

            // raw 0.5 → eased 0.125 → 1 + 2·0.125
            Assert.Equal(1.25, ZoomMath.FactorAt(item, 1 * Sec), 10);
        }

        [Fact]
        public void Overlapping_ramps_clamp_to_the_smaller_progress()
        {
            var item = ZoomItem(Guid.NewGuid(), 0, 2 * Sec, zoom: 3.0);
            item.Entry = new Transition { Kind = TransitionKind.Ramp, DurationTicks = 2 * Sec, Easing = TransitionEasing.Linear };
            item.Exit = new Transition { Kind = TransitionKind.Ramp, DurationTicks = 2 * Sec, Easing = TransitionEasing.Linear };

            // at the midpoint both ramps read 0.5; approaching the end the exit wins
            Assert.Equal(2.0, ZoomMath.FactorAt(item, 1 * Sec), 10);
            Assert.Equal(1.5, ZoomMath.FactorAt(item, Sec / 2), 10);      // entry 0.25
            Assert.Equal(1.5, ZoomMath.FactorAt(item, 3 * Sec / 2), 10);  // exit 0.25
        }

        // -------------------------------------------------------------------- effective matrix

        [Fact]
        public void No_zoom_tracks_yields_identity()
        {
            var project = ProjectWith((VideoTrack(0), Array.Empty<Item>()));
            Assert.True(ZoomMath.EffectiveMatrix(project, 0, 0, W, H).IsIdentity);
        }

        [Fact]
        public void Inactive_zoom_item_yields_identity()
        {
            var track = ZoomTrack(1);
            var project = ProjectWith(
                (VideoTrack(0), Array.Empty<Item>()),
                (track, new[] { ZoomItem(track.Id, 5 * Sec, 2 * Sec) }));

            Assert.True(ZoomMath.EffectiveMatrix(project, 0, 0, W, H).IsIdentity);
            Assert.True(ZoomMath.EffectiveMatrix(project, 7 * Sec, 0, W, H).IsIdentity);
            Assert.False(ZoomMath.EffectiveMatrix(project, 6 * Sec, 0, W, H).IsIdentity);
        }

        [Fact]
        public void Hidden_zoom_track_is_ignored()
        {
            var track = ZoomTrack(1, hidden: true);
            var project = ProjectWith(
                (VideoTrack(0), Array.Empty<Item>()),
                (track, new[] { ZoomItem(track.Id, 0, 10 * Sec) }));

            Assert.True(ZoomMath.EffectiveMatrix(project, 5 * Sec, 0, W, H).IsIdentity);
        }

        [Fact]
        public void Zoom_applies_only_to_tracks_beneath_its_row()
        {
            var below = VideoTrack(0);
            var zoom = ZoomTrack(1);
            var above = VideoTrack(2);
            var project = ProjectWith(
                (below, Array.Empty<Item>()),
                (zoom, new[] { ZoomItem(zoom.Id, 0, 10 * Sec) }),
                (above, Array.Empty<Item>()));

            Assert.False(ZoomMath.EffectiveMatrix(project, 5 * Sec, below.Order, W, H).IsIdentity);
            Assert.True(ZoomMath.EffectiveMatrix(project, 5 * Sec, zoom.Order, W, H).IsIdentity);
            Assert.True(ZoomMath.EffectiveMatrix(project, 5 * Sec, above.Order, W, H).IsIdentity);
        }

        [Fact]
        public void Speed_items_on_effect_tracks_contribute_no_zoom()
        {
            var speedTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Effect, Name = "Speed", Order = 5 };
            var speedItem = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = speedTrack.Id,
                TimelineStartTicks = 0,
                DurationTicks = 10 * Sec,
                Content = new SpeedContent { Factor = 2.0 },
            };
            var project = ProjectWith(
                (VideoTrack(0), Array.Empty<Item>()),
                (speedTrack, new[] { speedItem }));

            Assert.True(ZoomMath.EffectiveMatrix(project, 5 * Sec, 0, W, H).IsIdentity);
        }

        [Fact]
        public void Stacked_zooms_multiply()
        {
            var lower = ZoomTrack(1);
            var upper = ZoomTrack(2);
            var project = ProjectWith(
                (VideoTrack(0), Array.Empty<Item>()),
                (lower, new[] { ZoomItem(lower.Id, 0, 10 * Sec, zoom: 2.0, focusX: 0.3, focusY: 0.7) }),
                (upper, new[] { ZoomItem(upper.Id, 0, 10 * Sec, zoom: 1.5, focusX: 0.6, focusY: 0.4) }));

            var m = ZoomMath.EffectiveMatrix(project, 5 * Sec, 0, W, H);
            Assert.Equal(3.0, m.ScaleX, 4);
            Assert.Equal(3.0, m.ScaleY, 4);
        }

        [Fact]
        public void Stacking_applies_the_topmost_zoom_outermost()
        {
            var lower = ZoomTrack(1);
            var upper = ZoomTrack(2);
            var project = ProjectWith(
                (VideoTrack(0), Array.Empty<Item>()),
                (lower, new[] { ZoomItem(lower.Id, 0, 10 * Sec, zoom: 2.0, focusX: 0.2, focusY: 0.8) }),
                (upper, new[] { ZoomItem(upper.Id, 0, 10 * Sec, zoom: 1.5, focusX: 0.9, focusY: 0.1) }));

            var lowerM = ZoomMath.ItemMatrix(2.0, 0.2, 0.8, W, H);
            var upperM = ZoomMath.ItemMatrix(1.5, 0.9, 0.1, W, H);
            var total = ZoomMath.EffectiveMatrix(project, 5 * Sec, 0, W, H);

            var p = new SKPoint(700, 300);
            var expected = upperM.MapPoint(lowerM.MapPoint(p));
            var actual = total.MapPoint(p);
            Assert.Equal(expected.X, actual.X, 2);
            Assert.Equal(expected.Y, actual.Y, 2);
        }

        [Fact]
        public void Stacked_matrix_still_covers_the_viewport()
        {
            var lower = ZoomTrack(1);
            var upper = ZoomTrack(2);
            var project = ProjectWith(
                (VideoTrack(0), Array.Empty<Item>()),
                (lower, new[] { ZoomItem(lower.Id, 0, 10 * Sec, zoom: 2.0, focusX: 0.0, focusY: 1.0) }),
                (upper, new[] { ZoomItem(upper.Id, 0, 10 * Sec, zoom: 1.5, focusX: 1.0, focusY: 0.0) }));

            var m = ZoomMath.EffectiveMatrix(project, 5 * Sec, 0, W, H);
            var topLeft = m.MapPoint(0, 0);
            var bottomRight = m.MapPoint(W, H);

            Assert.True(topLeft.X <= 0.001);
            Assert.True(topLeft.Y <= 0.001);
            Assert.True(bottomRight.X >= W - 0.001);
            Assert.True(bottomRight.Y >= H - 0.001);
        }

        // ----------------------------------------------------------------- ramp visual inertness

        [Fact]
        public void Ramp_transitions_are_visually_inert()
        {
            var item = new Item
            {
                Id = Guid.NewGuid(),
                TimelineStartTicks = 0,
                DurationTicks = 10 * Sec,
                Entry = new Transition { Kind = TransitionKind.Ramp, DurationTicks = 2 * Sec, Easing = TransitionEasing.Linear },
                Exit = new Transition { Kind = TransitionKind.Ramp, DurationTicks = 2 * Sec, Easing = TransitionEasing.Linear },
            };

            var fx = TransitionMath.Evaluate(item, 1 * Sec); // mid-entry-ramp
            Assert.Equal(1, fx.Opacity);
            Assert.Equal(0, fx.OffsetXFrac);
            Assert.Equal(0, fx.OffsetYFrac);
            Assert.False(fx.HasWipe);

            fx = TransitionMath.Evaluate(item, 9 * Sec); // mid-exit-ramp
            Assert.Equal(1, fx.Opacity);
            Assert.Equal(0, fx.OffsetXFrac);
            Assert.Equal(0, fx.OffsetYFrac);
            Assert.False(fx.HasWipe);
        }
    }
}

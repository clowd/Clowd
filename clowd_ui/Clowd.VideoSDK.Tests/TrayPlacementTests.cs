using System;
using Avalonia;
using Avalonia.Layout;
using Avalonia.Media;
using Clowd.PlatformUtil;
using Clowd.UI.Controls.Tray;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The placement cascades behind every floating tray, pinned rung by rung.
    /// <see cref="TrayPlacement"/> is deliberately free of Avalonia controls, windows, screens and
    /// dispatchers for the same reason <c>ShareRegionGeometry</c> is: a wrong rectangle here does not
    /// throw. It parks a toolbar inside the rectangle a capture is about to photograph — a failure that
    /// is invisible in the app and permanent in the file — or it pushes the strip under the taskbar,
    /// where the user cannot reach the button that stops the capture.
    ///
    /// <para>
    /// Three rules carry most of the weight and all three are counter-intuitive:
    /// </para>
    /// <para>
    /// 1. Every fits-here test is measured on the WINDOW, not the tray: the shadow reserve is part of
    /// the window rect, every transparent pixel of it eats clicks, and a rung that only checked the
    /// tray's own extent would tuck that dead fringe inside the region.
    /// </para>
    /// <para>
    /// 2. The below/right rungs pull the strip back TOWARD the region rather than overflowing the
    /// working area (<c>Math.Min(workArea.Bottom, …)</c>), so a selection near the taskbar gets a
    /// smaller gap instead of a strip half under the shell.
    /// </para>
    /// <para>
    /// 3. The last rung places the strip INSIDE the region and is never withheld — a region with no
    /// room on any side covers the monitor, so refusing to place would cost the user the stop button
    /// without keeping the strip out of the picture.
    /// </para>
    ///
    /// <para>
    /// The numbers below are the real ones: a 1920×1080 monitor with a 40 px bottom taskbar
    /// (working area 1920×1040), the contact-sheet tray sizes 336×40 and 42×292, the compact shadow
    /// reserve (10, 7, 10, 13), and the old strip's 2 px minimum / 15 px preferred distances.
    /// </para>
    /// </summary>
    public class TrayPlacementTests
    {
        // the reference monitor: 1920×1080 with a 40 px taskbar along the bottom.
        private static readonly ScreenRect Screen = new ScreenRect(0, 0, 1920, 1080);
        private static readonly ScreenRect Work = new ScreenRect(0, 0, 1920, 1040);

        private const int MinDistance = 2;    // ceil(2 × 1.0), the old strip's minimum clearance
        private const int MaxDistance = 15;   // ceil(15 × 1.0), the old strip's preferred gap

        // the contact-sheet recording strip: 336×40 horizontal, 42×292 once rotated.
        private const int TrayLong = 336, TrayShort = 40;
        private const int RotatedLong = 292, RotatedShort = 42;

        private static TrayPlacementResult Near(ScreenRect region, bool preferAbove = false,
            int trayLong = TrayLong, int trayShort = TrayShort, TrayInsets reserve = default,
            ScreenRect screen = null, ScreenRect work = null)
            => TrayPlacement.Near(region, screen ?? Screen, work ?? Work, trayLong, trayShort, reserve,
                MinDistance, MaxDistance, preferAbove);

        // ------------------------------------------------------------------ Near: rung 1, below

        /// <summary>The overwhelmingly common case: room under the selection, so the strip sits
        /// horizontally 15 px below it and centred on it. Both coordinates are pinned because the
        /// centring is what makes the strip feel attached to the selection rather than to the screen.</summary>
        [Fact]
        public void Near_RoomBelow_SitsFifteenPxUnderTheSelectionAndCentred()
        {
            var region = new ScreenRect(700, 300, 400, 300);   // bottom 600, plenty of room under it

            var r = Near(region);

            Assert.Equal(Orientation.Horizontal, r.Orientation);
            Assert.Equal(region.Bottom + MaxDistance, r.Y);
            Assert.Equal(region.Left + region.Width / 2 - TrayLong / 2, r.X);
        }

        /// <summary>The rung is chosen on the WINDOW's short extent, not the tray's: a gap that fits a
        /// 40 px tray but not a 40 px tray plus its 20 px of vertical shadow reserve must fall through
        /// to a side rung, or the shadow's dead fringe ends up inside the region.</summary>
        [Fact]
        public void Near_GapFitsTheTrayButNotItsReserve_FallsThroughToASideRung()
        {
            // 55 px of working area below the selection: 55 − 2 ≥ 40 (the tray) but < 60 (the window).
            var region = new ScreenRect(700, 400, 400, 585);   // bottom 985, work bottom 1040

            Assert.Equal(Orientation.Horizontal, Near(region).Orientation);
            Assert.Equal(Orientation.Vertical, Near(region, reserve: Compact()).Orientation);
        }

        /// <summary>The pull-back rule: when the preferred 15 px gap would run the window past the
        /// working area, the strip moves closer to the selection instead of overflowing. Without the
        /// <c>Math.Min(workArea.Bottom, …)</c> the strip would be dealt a slot half under the taskbar,
        /// which is painted over it.</summary>
        [Fact]
        public void Near_NotQuiteFifteenPxOfRoom_PullsTheStripTowardTheSelection()
        {
            var region = new ScreenRect(700, 690, 400, 300);   // bottom 990: 48 px of usable room, tray needs 40

            var r = Near(region);

            Assert.Equal(Orientation.Horizontal, r.Orientation);
            Assert.Equal(Work.Bottom - TrayShort, r.Y);        // flush with the working area's edge
            Assert.Equal(region.Bottom + 10, r.Y);             // i.e. a 10 px gap, not the preferred 15
        }

        // ------------------------------------------------------------------ Near: rung 2, above (opt-in)

        /// <summary>With the "above" rung asked for, a selection that has no room below is served a
        /// horizontal strip above it in preference to the vertical rungs: the horizontal shape is the
        /// one whose labels read at a glance, and it stays outside the region.</summary>
        [Fact]
        public void Near_BelowBlockedAndAbovePreferred_SitsAboveHorizontally()
        {
            var region = new ScreenRect(500, 700, 400, 330);   // bottom 1030: 8 px of usable room below

            var r = Near(region, preferAbove: true);

            Assert.Equal(Orientation.Horizontal, r.Orientation);
            Assert.Equal(region.Top - MaxDistance - TrayShort, r.Y);
            Assert.Equal(region.Left + region.Width / 2 - TrayLong / 2, r.X);
        }

        /// <summary>The same geometry without the opt-in takes the vertical rung, so the rung is a
        /// caller decision and not a coincidence of the numbers.</summary>
        [Fact]
        public void Near_BelowBlockedAndAboveNotPreferred_SkipsTheAboveRung()
        {
            var region = new ScreenRect(500, 700, 400, 330);

            Assert.Equal(Orientation.Vertical, Near(region).Orientation);
        }

        // ------------------------------------------------------------------ Near: rungs 3 and 4, right then left

        /// <summary>To the right of the selection, rotated, and anchored so the strip's BOTTOM lines up
        /// with the selection's bottom edge — the old strip's rule, kept because it puts the primary
        /// button nearest the corner the user's pointer is already in.</summary>
        [Fact]
        public void Near_BelowBlocked_GoesRightVerticalAndBottomAligned()
        {
            var region = new ScreenRect(500, 700, 400, 330);   // bottom 1030

            var r = Near(region);

            Assert.Equal(Orientation.Vertical, r.Orientation);
            Assert.Equal(region.Right + MaxDistance, r.X);
            Assert.Equal(region.Bottom - TrayLong, r.Y);
            Assert.Equal(region.Bottom, r.Y + TrayLong);       // bottom-aligned with the selection
        }

        /// <summary>Only when the right-hand side is out of room too does the strip go left — it is the
        /// last rung that keeps it out of the region, so the order matters.</summary>
        [Fact]
        public void Near_BelowAndRightBlocked_GoesLeftVertical()
        {
            var region = new ScreenRect(600, 700, 1320, 330);  // right 1920 (no room), bottom 1030

            var r = Near(region);

            Assert.Equal(Orientation.Vertical, r.Orientation);
            Assert.Equal(region.Left - MaxDistance - TrayShort, r.X);
            Assert.Equal(region.Bottom - TrayLong, r.Y);
        }

        // ------------------------------------------------------------------ Near: rung 5, inside — never withheld

        /// <summary>A full-monitor selection has no room on any side, so the strip is placed INSIDE it,
        /// horizontally, twice the preferred gap up from the placeable bottom edge. It is never
        /// withheld: there is nowhere on that monitor it would be out of the picture, and refusing
        /// would only take away the button that stops the capture.</summary>
        [Fact]
        public void Near_FullMonitorRegion_SitsInsideItAboveTheTaskbar()
        {
            var region = Screen;

            var r = Near(region, preferAbove: true);           // the opt-in changes nothing here: no room above either

            Assert.Equal(Orientation.Horizontal, r.Orientation);
            Assert.Equal(Math.Min(region.Bottom, Work.Bottom) - TrayShort - MaxDistance * 2, r.Y);
            Assert.Equal(region.Left + region.Width / 2 - TrayLong / 2, r.X);
            Assert.True(r.Y + TrayShort <= Work.Bottom);       // clear of the taskbar it would otherwise hide under
        }

        // ------------------------------------------------------------------ Near: the two clamps

        /// <summary>The horizontal clamp, both branches: a selection hugging either edge of the monitor
        /// would centre the strip off-screen, and a strip off-screen is a strip the user cannot use.</summary>
        [Fact]
        public void Near_SelectionAgainstAScreenEdge_ClampsHorizontallyIntoTheWorkingArea()
        {
            var atLeft = Near(new ScreenRect(0, 300, 60, 100));
            Assert.Equal(Work.Left, atLeft.X);

            var atRight = Near(new ScreenRect(1860, 300, 60, 100));
            Assert.Equal(Work.Right - TrayLong, atRight.X);
        }

        /// <summary>The vertical clamp the WPF original never had. A selection that runs past the
        /// working area's bottom edge (a full-height capture on a machine with a taskbar) takes the
        /// bottom-anchored vertical rung, whose Y is derived from the SELECTION's bottom — so without
        /// this clamp the strip lands under the shell.</summary>
        [Fact]
        public void Near_SelectionPastTheWorkingArea_ClampsVerticallyOffTheTaskbar()
        {
            var region = new ScreenRect(500, 760, 400, 320);   // bottom 1080: past the 1040 working edge

            var r = Near(region);

            Assert.Equal(Orientation.Vertical, r.Orientation);
            Assert.Equal(Work.Bottom - TrayLong, r.Y);
        }

        /// <summary>The clamp's other branch: on a monitor shorter than the strip is long, the
        /// bottom-anchored rung produces a negative Y, and the top clamp wins.</summary>
        [Fact]
        public void Near_MonitorShorterThanTheStrip_ClampsToTheTopOfTheWorkingArea()
        {
            var small = new ScreenRect(0, 0, 800, 300);
            var region = new ScreenRect(100, 200, 200, 100);   // bottom 300: no room below, room to the right

            var r = Near(region, screen: small, work: small);

            Assert.Equal(Orientation.Vertical, r.Orientation);
            Assert.Equal(small.Top, r.Y);
        }

        /// <summary>A monitor narrower and shorter than the tray itself: every fit test fails, the
        /// clamps run both branches against each other, and the contract is only that nothing throws
        /// and the window starts inside the placeable area. (This is the projector that reports 200×120
        /// for a frame while it renegotiates a mode.)</summary>
        [Fact]
        public void Near_MonitorSmallerThanTheTray_DoesNotThrow()
        {
            var tiny = new ScreenRect(0, 0, 200, 120);

            var r = Near(tiny, screen: tiny, work: tiny);

            Assert.Equal(Orientation.Horizontal, r.Orientation);
            Assert.Equal(tiny.Left, r.X);
            Assert.True(r.Y >= tiny.Top);
        }

        // ------------------------------------------------------------------ Near: selection, reserve, second pass

        /// <summary>A selection spanning two monitors is placed against the part of it that is on THIS
        /// monitor, so the strip stays on the display the region's centre picked. Taking the whole
        /// region's width would centre the strip off this monitor and the clamp would then shove it
        /// into the corner.</summary>
        [Fact]
        public void Near_RegionSpanningTwoMonitors_UsesTheIntersectionWithThisScreen()
        {
            var region = new ScreenRect(1500, 300, 1000, 200);   // 420 px on this monitor, the rest on the next
            var onThisScreen = region.Intersect(Screen);

            var r = Near(region);

            Assert.Equal(Orientation.Horizontal, r.Orientation);
            Assert.Equal(onThisScreen.Left + onThisScreen.Width / 2 - TrayLong / 2, r.X);
            Assert.Equal(onThisScreen.Bottom + MaxDistance, r.Y);
        }

        /// <summary>The reserve's whole contract in one test. The result is the WINDOW's top-left, so
        /// the tray lands at result + (reserve.Left, reserve.Top): on the below rung the TRAY keeps the
        /// 15 px gap exactly where a reserve-less tray would sit — the window is pulled back toward the
        /// region by its top reserve, so the shadow falls into the gap — and its edge never crosses into
        /// the region (7 &lt; 15). A symmetric side reserve leaves the tray centred where it was.</summary>
        [Fact]
        public void Near_WithAShadowReserve_PullsTheWindowBackSoTheTrayKeepsTheGap()
        {
            var region = new ScreenRect(700, 300, 400, 300);
            var reserve = Compact();                            // (10, 7, 10, 13)

            var bare = Near(region);
            var withReserve = Near(region, reserve: reserve);

            // the TRAY lands where the bare tray did; the window is that shifted by (−Left, −Top)
            Assert.Equal(bare.Y, withReserve.Y + reserve.Top);
            Assert.Equal(region.Bottom + MaxDistance, withReserve.Y + reserve.Top);
            Assert.True(withReserve.Y >= region.Bottom, "the window's reserved fringe must stay outside the region");

            // and a left/right-symmetric reserve leaves the tray centred where it was
            Assert.Equal(bare.X, withReserve.X + reserve.Left);
        }

        /// <summary>The pull-back is capped at the gap: with a reserve deeper than the gap the window
        /// edge lands flush against the region, never inside it.</summary>
        [Fact]
        public void Near_WithAReserveDeeperThanTheGap_StopsTheWindowAtTheRegionEdge()
        {
            var region = new ScreenRect(700, 300, 400, 300);
            var deep = new TrayInsets(34, 22, 34, 46);          // the spec shadow's reserve

            var r = Near(region, reserve: deep);

            Assert.Equal(Orientation.Horizontal, r.Orientation);
            Assert.Equal(region.Bottom, r.Y);
        }

        /// <summary>The second pass after a rotation. The chassis re-measures the rotated tray (336×40
        /// becomes 42×292) and places again; that pass must reach the same rung and the same anchor, or
        /// the strip oscillates between two positions every time it is re-placed.</summary>
        [Fact]
        public void Near_SecondPassWithTheRotatedTraySize_IsStable()
        {
            var region = new ScreenRect(500, 700, 400, 330);

            var first = Near(region);                                                     // 336 × 40
            var second = Near(region, trayLong: RotatedLong, trayShort: RotatedShort);     // 42 × 292

            Assert.Equal(Orientation.Vertical, first.Orientation);
            Assert.Equal(Orientation.Vertical, second.Orientation);
            Assert.Equal(first.X, second.X);                          // same distance from the region's right edge
            Assert.Equal(region.Bottom, second.Y + RotatedLong);      // still bottom-aligned
        }

        /// <summary>
        /// The other outcome of the two passes, and the reason the chassis bounds its rotated re-pass to
        /// exactly one: for a band of geometries the two measurements genuinely disagree, so an unbounded
        /// "it rotated, measure again" loop never converges.
        /// <para>
        /// Free space here falls between the two short edges — 39 px below the region (working area), 42 px
        /// to its right, none to its left, no reserve. Measured at the 40 px short edge the below rung is
        /// 3 px short (39 − 2 minimum clearance = 37) and the right rung fits exactly (42 − 2 = 40), so the
        /// strip is rotated Vertical; measured at the rotated 42 px short edge neither fits, and the last
        /// rung — inside the region — hands back Horizontal. Each pass is individually correct; there is
        /// simply no orientation that is stable at both sizes, which is why
        /// <c>FloatingTrayWindow.Reposition</c> allows the rotation ONE re-queued pass and no third
        /// (the chassis guard is load-bearing, not defensive).
        /// </para>
        /// </summary>
        [Fact]
        public void Near_FreeSpaceBetweenTheTwoShortEdges_DisagreesAcrossPasses()
        {
            // right 1878 (42 px to the working area's right edge), bottom 1001 (39 px below), left 0.
            var region = new ScreenRect(0, 700, 1878, 301);

            Assert.Equal(Orientation.Vertical, Near(region, trayShort: TrayShort).Orientation);
            Assert.Equal(Orientation.Horizontal, Near(region, trayShort: RotatedShort).Orientation);
        }

        /// <summary>Placement is a pure function: the same inputs give the same answer, so the posted
        /// second pass of an un-rotated strip never moves it.</summary>
        [Fact]
        public void Near_CalledTwiceWithTheSameInputs_IsIdempotent()
        {
            var region = new ScreenRect(700, 300, 400, 300);

            Assert.Equal(Near(region), Near(region));
        }

        // ------------------------------------------------------------------ Outside: the refusing cascade

        private const int FixedW = 300, FixedH = 60, Gap = 10;

        /// <summary>Outside with the compact reserve: each candidate is pulled toward the region by its
        /// near-side reserve, so the painted tray keeps the gap and the window still misses the region.</summary>
        [Fact]
        public void Outside_WithAShadowReserve_PullsTheWindowBackWithoutEnteringTheRegion()
        {
            var region = new ScreenRect(700, 300, 400, 300);
            var reserve = Compact();                            // (10, 7, 10, 13)

            var bare = TrayPlacement.Outside(region, Work, TrayLong, TrayShort, Gap);
            var r = TrayPlacement.Outside(region, Work, TrayLong, TrayShort, Gap, reserve);

            Assert.NotNull(r);
            Assert.Equal(bare.Top - reserve.Top, r.Top);
            Assert.False(r.IntersectsWith(region));
        }

        private static ScreenRect Outside(ScreenRect region, ScreenRect area = null, int gap = Gap,
            int width = FixedW, int height = FixedH)
            => TrayPlacement.Outside(region, area ?? Work, width, height, gap);

        /// <summary>Rung 1: below the region, centred on it, one gap clear of it.</summary>
        [Fact]
        public void Outside_RoomBelow_SitsBelowCentred()
        {
            var region = new ScreenRect(700, 300, 400, 200);

            var r = Outside(region);

            Assert.NotNull(r);
            Assert.Equal(region.Bottom + Gap, r.Top);
            Assert.Equal(region.Left + region.Width / 2 - FixedW / 2, r.Left);
            Assert.False(r.IntersectsWith(region));
        }

        /// <summary>Rung 2: below does not fit inside the working area, so the strip goes right,
        /// vertically centred on the region.</summary>
        [Fact]
        public void Outside_BelowOffTheArea_GoesRight()
        {
            var region = new ScreenRect(700, 800, 400, 240);   // bottom 1040 = the working edge

            var r = Outside(region);

            Assert.NotNull(r);
            Assert.Equal(region.Right + Gap, r.Left);
            Assert.Equal(region.Top + region.Height / 2 - FixedH / 2, r.Top);
        }

        /// <summary>Rung 3: no room below or right, so left.</summary>
        [Fact]
        public void Outside_BelowAndRightOffTheArea_GoesLeft()
        {
            var region = new ScreenRect(1600, 800, 320, 240);  // right 1920, bottom 1040

            var r = Outside(region);

            Assert.NotNull(r);
            Assert.Equal(region.Left - Gap - FixedW, r.Left);
            Assert.False(r.IntersectsWith(region));
        }

        /// <summary>Rung 4: a full-width region low on the screen leaves only the space above it.
        /// Unlike <see cref="TrayPlacement.Near"/> there is no fifth rung — see the refusal test.</summary>
        [Fact]
        public void Outside_OnlyRoomAbove_GoesAbove()
        {
            var region = new ScreenRect(0, 800, 1920, 240);

            var r = Outside(region);

            Assert.NotNull(r);
            Assert.Equal(region.Top - Gap - FixedH, r.Top);
            Assert.False(r.IntersectsWith(region));
        }

        /// <summary>With no gap at all the candidate is flush against the region's edge, and that is
        /// accepted: touching <see cref="ScreenRect"/>s do not intersect, so a zero gap means "right up
        /// against it" rather than "refuse". The row of pixels the region owns is still its own.</summary>
        [Fact]
        public void Outside_ZeroGap_AcceptsACandidateTouchingTheRegion()
        {
            var region = new ScreenRect(700, 300, 400, 200);

            var r = Outside(region, gap: 0);

            Assert.NotNull(r);
            Assert.Equal(region.Bottom, r.Top);               // its top edge IS the region's bottom edge
            Assert.False(r.IntersectsWith(region));
        }

        /// <summary>The load-bearing test, exercised directly: a candidate that overlaps the region is
        /// rejected however the arithmetic produced it. A negative gap is the only way to make the
        /// arithmetic produce one from a region that sits wholly inside the area — which is itself the
        /// point of the check, since it does not trust the arithmetic above it. Here all four rungs
        /// overlap, so the answer is a refusal rather than a strip in the picture.</summary>
        [Fact]
        public void Outside_EveryCandidateOverlapsTheRegion_Refuses()
        {
            var region = new ScreenRect(700, 300, 400, 200);

            Assert.Null(Outside(region, gap: -30));
        }

        /// <summary>A working area too small to hold the strip anywhere clear of the region refuses,
        /// and the caller then never shows the window: invisible beats being in the picture.</summary>
        [Fact]
        public void Outside_NoCandidateFitsTheArea_Refuses()
        {
            var area = new ScreenRect(0, 0, 320, 200);

            Assert.Null(Outside(area, area));
        }

        /// <summary>The invariant swept over a grid of region positions and sizes: whatever comes back
        /// is inside the working area and misses the region. This is the test that would catch a future
        /// edit to the candidate arithmetic, which is easy to get subtly wrong per rung.</summary>
        [Fact]
        public void Outside_AcrossAGridOfRegions_NeverOverlapsAndStaysInTheArea()
        {
            for (var x = 0; x <= 1600; x += 200)
            {
                for (var y = 0; y <= 900; y += 150)
                {
                    foreach (var size in new[] { 80, 400, 900 })
                    {
                        var region = new ScreenRect(x, y, size, size / 2);
                        var r = Outside(region);
                        if (r == null)
                            continue;

                        Assert.False(r.IntersectsWith(region), $"overlap at {x},{y} size {size}");
                        Assert.True(r.Left >= Work.Left && r.Top >= Work.Top &&
                                    r.Right <= Work.Right && r.Bottom <= Work.Bottom,
                            $"off-area at {x},{y} size {size}");
                    }
                }
            }
        }

        // ------------------------------------------------------------------ Clamp

        /// <summary>The guarded clamp: a monitor narrower than the strip makes min &gt; max, which
        /// <see cref="Math.Clamp(int, int, int)"/> answers with an exception. The caller's own bounds check
        /// rejects that candidate a line later, so throwing here would turn a placement that is merely
        /// impossible into a crash mid-capture.</summary>
        [Fact]
        public void Clamp_ToleratesMinGreaterThanMax()
        {
            Assert.Equal(5, TrayPlacement.Clamp(5, 0, 10));
            Assert.Equal(0, TrayPlacement.Clamp(-5, 0, 10));
            Assert.Equal(10, TrayPlacement.Clamp(50, 0, 10));
            Assert.Equal(10, TrayPlacement.Clamp(5, 10, 0));    // min > max: the min wins, nothing throws
        }

        // ------------------------------------------------------------------ ShadowReserve / TrayInsets

        /// <summary>The spec's shadow, pinned. 34 px of blur means a 46 px dead band under the strip —
        /// the number that made the compact shadow the shipped one, so it is worth having on record.</summary>
        [Fact]
        public void ShadowReserve_SpecShadow_IsThirtyFourTwentyTwoThirtyFourFortySix()
        {
            Assert.Equal(new Thickness(34, 22, 34, 46), TrayPlacement.ShadowReserve(TrayTokens.Shadow));
        }

        /// <summary>The shipped shadow: a 13 px fringe below, 7 above, 10 either side.</summary>
        [Fact]
        public void ShadowReserve_CompactShadow_IsTenSevenTenThirteen()
        {
            Assert.Equal(new Thickness(10, 7, 10, 13), TrayPlacement.ShadowReserve(TrayTokens.ShadowCompact));
        }

        /// <summary>No shadow, no reserve — the path taken when the platform refuses transparency and
        /// the chassis drops the shadow outright.</summary>
        [Fact]
        public void ShadowReserve_NoShadow_IsZero()
        {
            Assert.Equal(new Thickness(0), TrayPlacement.ShadowReserve(default(BoxShadows)));
        }

        /// <summary>Logical → capture px rounds UP per side: half a pixel of un-reserved shadow is half
        /// a pixel of shadow inside the region.</summary>
        [Fact]
        public void TrayInsets_FromLogical_CeilingsEachSide()
        {
            var insets = TrayInsets.FromLogical(new Thickness(10, 7, 10, 13), 1.5);

            Assert.Equal(new TrayInsets(15, 11, 15, 20), insets);
        }

        private static TrayInsets Compact() => TrayInsets.FromLogical(TrayPlacement.ShadowReserve(TrayTokens.ShadowCompact), 1.0);
    }
}

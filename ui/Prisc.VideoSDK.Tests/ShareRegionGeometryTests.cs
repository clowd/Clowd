using System;
using Prisc.Interop.Testing;
using Prisc.PlatformUtil;
using Prisc.UI;
using Xunit;

namespace Prisc.VideoSDK.Tests
{
    /// <summary>
    /// The share-region resize facade over <c>prisc_rust_core::share_region</c>, the rule the region
    /// helper applies to every region it mirrors. The rules themselves (the floor and even
    /// rounding, the moving edge absorbing the clamp, the edge swap on a crossing, the pure
    /// function of the press-time anchor) are pinned case by case next to that one implementation
    /// (crates/prisc_rust_core/src/share_region.rs, which carries this file's former vectors); what
    /// is left here is that <see cref="ShareRegionGeometry"/> hands the rect across and back intact,
    /// in the gizmo's handle numbering, and refuses what it cannot pass.
    /// </summary>
    public class ShareRegionGeometryTests
    {
        [Fact]
        public void Clamp_floors_rounds_to_even_and_keeps_the_origin()
        {
            InteropTestGate.Require();
            Assert.Equal(64, ShareRegionGeometry.MinRegion);
            Assert.Equal(new ScreenRect(-1920, -1080, 64, 800), ShareRegionGeometry.Clamp(new ScreenRect(-1920, -1080, 10, 801)));

            var once = ShareRegionGeometry.Clamp(new ScreenRect(-7, 13, 63, 65));
            Assert.Equal(once, ShareRegionGeometry.Clamp(once));
        }

        [Fact]
        public void A_drag_keeps_the_anchored_edge_and_the_body_translates()
        {
            InteropTestGate.Require();
            var anchor = new ScreenRect(100, 100, 200, 200);

            Assert.Equal(new ScreenRect(120, 130, 180, 170), ShareRegionGeometry.ApplyDrag(anchor, ShareRegionGeometry.HandleTopLeft, 20, 30));
            Assert.Equal(new ScreenRect(136, 0, 64, 200), ShareRegionGeometry.ApplyDrag(new ScreenRect(0, 0, 200, 200), ShareRegionGeometry.HandleLeft, 180, 0));
            Assert.Equal(new ScreenRect(-700, 0, 800, 200), ShareRegionGeometry.ApplyDrag(new ScreenRect(100, 0, 200, 200), ShareRegionGeometry.HandleRight, -1000, 0));
            Assert.Equal(new ScreenRect(-4900, -60, 200, 200), ShareRegionGeometry.ApplyDrag(anchor, ShareRegionGeometry.HandleBody, -5000, -160));
        }

        [Fact]
        public void The_handle_numbering_is_the_gizmos()
        {
            Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 }, new[]
            {
                ShareRegionGeometry.HandleTopLeft, ShareRegionGeometry.HandleTopRight, ShareRegionGeometry.HandleBottomLeft,
                ShareRegionGeometry.HandleBottomRight, ShareRegionGeometry.HandleLeft, ShareRegionGeometry.HandleTop,
                ShareRegionGeometry.HandleRight, ShareRegionGeometry.HandleBottom, ShareRegionGeometry.HandleBody,
            });
        }

        [Fact]
        public void Null_and_unknown_handles_are_refused()
        {
            Assert.Throws<ArgumentNullException>(() => ShareRegionGeometry.Clamp(null));
            Assert.Throws<ArgumentNullException>(() => ShareRegionGeometry.ApplyDrag(null, ShareRegionGeometry.HandleBody, 0, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => ShareRegionGeometry.ApplyDrag(new ScreenRect(0, 0, 100, 100), -1, 0, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => ShareRegionGeometry.ApplyDrag(new ScreenRect(0, 0, 100, 100), 9, 0, 0));
        }
    }
}

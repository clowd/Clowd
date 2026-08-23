using System;
using Clowd.UI.VideoEditor;
using Clowd.VideoSDK.Model;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The preview's placement math against the new effect content: effect items are never
    /// composed, so <see cref="ItemPlacement"/> must resolve them to "no placement" (which is how
    /// the preview expresses "no gizmo") and the click hit-test must select straight through an
    /// effect row to the picture actually drawn beneath it — the window arranges the zoom focus
    /// reticle, not the gizmo, on a zoom selection. (Clowd.Ui exposes its internals to this
    /// project.)
    /// </summary>
    public class EffectPreviewOverlayTests
    {
        private const int CanvasW = 800, CanvasH = 450;

        private static Track NewTrack(TrackKind kind, int order, string name = null) => new Track
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            Order = order,
            Name = name ?? kind.ToString(),
        };

        private static Item NewItem(Track track, ItemContent content) => new Item
        {
            Id = Guid.NewGuid(),
            TrackId = track.Id,
            TimelineStartTicks = 0,
            DurationTicks = 5 * TimeSpan.TicksPerSecond,
            Content = content,
        };

        /// <summary>A solid on a video row (resolvable with no media on disk) with a zoom row and
        /// the speed row stacked above it, the way the session builds them.</summary>
        private static (Project Project, Item Solid, Item Zoom, Item Speed) BuildProject()
        {
            var video = NewTrack(TrackKind.Video, 0, "Screen");
            var zoomTrack = NewTrack(TrackKind.Effect, 1, "Zoom");
            var speedTrack = NewTrack(TrackKind.Effect, 100, "Speed");

            var solid = NewItem(video, new SolidContent());
            var zoom = NewItem(zoomTrack, new ZoomContent());
            var speed = NewItem(speedTrack, new SpeedContent());

            var project = new Project();
            project.Tracks.AddRange(new[] { video, zoomTrack, speedTrack });
            project.Items.AddRange(new[] { solid, zoom, speed });
            return (project, solid, zoom, speed);
        }

        [Fact]
        public void TryResolve_resolves_effect_items_to_no_placement()
        {
            var (project, _, zoom, speed) = BuildProject();

            Assert.False(ItemPlacement.TryResolve(project, zoom, CanvasW, CanvasH, out _));
            Assert.False(ItemPlacement.TryResolve(project, speed, CanvasW, CanvasH, out _));
            Assert.Null(ItemPlacement.ContentAspect(project, zoom, CanvasW, CanvasH));
            Assert.Null(ItemPlacement.ContentAspect(project, speed, CanvasW, CanvasH));
        }

        [Fact]
        public void HitTest_selects_through_effect_rows_to_the_composed_item()
        {
            var (project, solid, _, _) = BuildProject();

            var hit = ItemPlacement.HitTest(project, TimeSpan.TicksPerSecond,
                CanvasW / 2.0, CanvasH / 2.0, CanvasW, CanvasH);

            Assert.NotNull(hit);
            Assert.Equal(solid.Id, hit.Id);
        }

        [Fact]
        public void HitTest_lands_on_bare_canvas_when_only_effect_items_cover_the_point()
        {
            var (project, solid, _, _) = BuildProject();
            project.Items.Remove(solid);

            Assert.Null(ItemPlacement.HitTest(project, TimeSpan.TicksPerSecond,
                CanvasW / 2.0, CanvasH / 2.0, CanvasW, CanvasH));
        }
    }
}

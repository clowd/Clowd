using System.Linq;
using Clowd.VideoSDK;
using Xunit;

namespace Clowd.Shared.Tests
{
    public class VideoEditDocumentTests
    {
        private static (long, long)[] Spans(System.Collections.Generic.IReadOnlyList<CutRegion> regions) =>
            regions.Select(r => (r.StartMs, r.EndMs)).ToArray();

        [Fact]
        public void NewDocument_KeepsEverything()
        {
            var doc = new VideoEditDocument();

            Assert.Empty(doc.Cuts);
            // TrimEndMs 0 is the "to the end of the media" sentinel, not an empty range.
            Assert.Equal(new[] { (0L, 10_000L) }, Spans(doc.GetKeepSegments(10_000)));
        }

        [Fact]
        public void AddCut_AcceptsReversedArguments_AndClampsBelowZero()
        {
            var doc = new VideoEditDocument();
            doc.AddCut(3000, 1000);
            doc.AddCut(-500, 400);

            Assert.Equal(new[] { (0L, 400L), (1000L, 3000L) }, Spans(doc.GetCutRanges()));
        }

        [Fact]
        public void AddCut_IgnoresRegionsShorterThanTheMinimum()
        {
            var doc = new VideoEditDocument();
            doc.AddCut(1000, 1000 + VideoEditDocument.MinSegmentMs - 1);
            doc.AddCut(5000, 5000); // zero length

            Assert.Empty(doc.Cuts);
        }

        [Fact]
        public void SetCuts_MergesAdjacentShortRegionsBeforeApplyingTheMinimum()
        {
            // each is below the 100 ms minimum on its own, but they touch — merging happens before
            // the length filter, so together they are a single 120 ms cut rather than both
            // vanishing. (Adding them one at a time would drop the first: an isolated 60 ms drag is
            // an accidental click, and AddCut normalizes what is already stored.)
            var doc = new VideoEditDocument();
            doc.SetCuts(new[] { new CutRegion(1000, 1060), new CutRegion(1060, 1120) });

            Assert.Equal(new[] { (1000L, 1120L) }, Spans(doc.GetCutRanges()));
        }

        [Fact]
        public void AddCut_MergesOverlappingAndTouchingRegions_AndSorts()
        {
            var doc = new VideoEditDocument();
            doc.AddCut(5000, 6000);
            doc.AddCut(1000, 2000);
            doc.AddCut(1500, 2500); // overlaps the second
            doc.AddCut(6000, 7000); // touches the first
            doc.AddCut(9000, 9500); // disjoint

            Assert.Equal(new[] { (1000L, 2500L), (5000L, 7000L), (9000L, 9500L) }, Spans(doc.GetCutRanges()));
        }

        [Fact]
        public void AddCut_SwallowsARegionFullyInsideAnother()
        {
            var doc = new VideoEditDocument();
            doc.AddCut(1000, 5000);
            doc.AddCut(2000, 3000);

            Assert.Equal(new[] { (1000L, 5000L) }, Spans(doc.GetCutRanges()));
        }

        [Fact]
        public void GetCutRanges_ReturnsASnapshot()
        {
            var doc = new VideoEditDocument();
            doc.AddCut(1000, 2000);
            var snapshot = doc.GetCutRanges();

            doc.AddCut(4000, 5000);

            Assert.Single(snapshot);
            Assert.Equal(2, doc.Cuts.Count);
        }

        [Fact]
        public void RemoveCut_And_ClearCuts()
        {
            var doc = new VideoEditDocument();
            doc.AddCut(1000, 2000);
            doc.AddCut(4000, 5000);

            Assert.False(doc.RemoveCut(new CutRegion(0, 100)));
            Assert.True(doc.RemoveCut(doc.Cuts[0]));
            Assert.Equal(new[] { (4000L, 5000L) }, Spans(doc.GetCutRanges()));

            doc.ClearCuts();
            Assert.Empty(doc.Cuts);
        }

        [Fact]
        public void Cuts_RaisePropertyChanged_OnlyWhenTheNormalizedListChanges()
        {
            var doc = new VideoEditDocument();
            var raised = 0;
            doc.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(VideoEditDocument.Cuts))
                    raised++;
            };

            doc.AddCut(1000, 2000);
            Assert.Equal(1, raised);

            doc.AddCut(1200, 1800); // already covered — normalizes to the same list
            Assert.Equal(1, raised);

            doc.AddCut(1000, 1050); // below the minimum, dropped
            Assert.Equal(1, raised);

            doc.ClearCuts();
            Assert.Equal(2, raised);
        }

        [Fact]
        public void GetKeepSegments_AppliesTheTrimRange()
        {
            var doc = new VideoEditDocument();
            doc.TrimStartMs = 2000;
            doc.TrimEndMs = 8000;

            Assert.Equal(new[] { (2000L, 8000L) }, Spans(doc.GetKeepSegments(10_000)));
        }

        [Fact]
        public void GetKeepSegments_ClampsTheTrimRangeToTheMedia()
        {
            var doc = new VideoEditDocument();
            doc.TrimStartMs = 1000;
            doc.TrimEndMs = 999_999;

            Assert.Equal(new[] { (1000L, 5000L) }, Spans(doc.GetKeepSegments(5000)));
        }

        [Fact]
        public void GetKeepSegments_IsTheComplementOfTheCuts()
        {
            var doc = new VideoEditDocument();
            doc.AddCut(2000, 3000);
            doc.AddCut(6000, 7000);

            Assert.Equal(
                new[] { (0L, 2000L), (3000L, 6000L), (7000L, 10_000L) },
                Spans(doc.GetKeepSegments(10_000)));
        }

        [Fact]
        public void GetKeepSegments_ClipsCutsToTheTrimRange()
        {
            var doc = new VideoEditDocument();
            doc.TrimStartMs = 1000;
            doc.TrimEndMs = 9000;
            doc.AddCut(0, 2000); // straddles the trim start
            doc.AddCut(8500, 20_000); // straddles the trim end (and the media end)

            Assert.Equal(new[] { (2000L, 8500L) }, Spans(doc.GetKeepSegments(10_000)));
        }

        [Fact]
        public void GetKeepSegments_DropsSegmentsShorterThanTheMinimum()
        {
            // the 50 ms sliver between the two cuts is not worth a segment: the cuts on either
            // side of it simply become one.
            var doc = new VideoEditDocument();
            doc.AddCut(2000, 3000);
            doc.AddCut(3050, 4000);

            Assert.Equal(new[] { (0L, 2000L), (4000L, 10_000L) }, Spans(doc.GetKeepSegments(10_000)));
        }

        [Fact]
        public void GetKeepSegments_ReturnsNothingWhenEverythingIsCutAway()
        {
            var doc = new VideoEditDocument();
            doc.AddCut(0, 10_000);

            Assert.Empty(doc.GetKeepSegments(10_000));
        }

        [Fact]
        public void GetKeepSegments_ReturnsNothingForADegenerateRange()
        {
            var doc = new VideoEditDocument();

            Assert.Empty(doc.GetKeepSegments(0));
            Assert.Empty(doc.GetKeepSegments(-1));

            doc.TrimStartMs = 5000;
            doc.TrimEndMs = 5050; // below the minimum segment length
            Assert.Empty(doc.GetKeepSegments(10_000));
        }

        [Fact]
        public void Trim_ValuesAreClampedNonNegative()
        {
            var doc = new VideoEditDocument();
            doc.TrimStartMs = -5;
            doc.TrimEndMs = -5;

            Assert.Equal(0L, doc.TrimStartMs);
            Assert.Equal(0L, doc.TrimEndMs);
        }

        [Fact]
        public void WebcamOverlay_Defaults()
        {
            var overlay = new VideoEditDocument().Webcam;

            Assert.False(overlay.Enabled);
            Assert.Equal(WebcamOverlayShape.Circle, overlay.Shape);
            Assert.Equal(0.25d, overlay.CornerRadius);
            Assert.InRange(overlay.CenterX, 0, 1);
            Assert.InRange(overlay.CenterY, 0, 1);
            Assert.InRange(overlay.Width, WebcamOverlay.MinWidth, 1);
        }

        [Fact]
        public void WebcamOverlay_ClampsGeometry()
        {
            var overlay = new VideoEditDocument().Webcam;

            overlay.CornerRadius = 5;
            Assert.Equal(0.5d, overlay.CornerRadius);
            overlay.CornerRadius = -1;
            Assert.Equal(0d, overlay.CornerRadius);

            overlay.CenterX = 4;
            overlay.CenterY = -4;
            Assert.Equal(1d, overlay.CenterX);
            Assert.Equal(0d, overlay.CenterY);

            overlay.Width = 9;
            Assert.Equal(1d, overlay.Width);
            overlay.Width = 0;
            Assert.Equal(WebcamOverlay.MinWidth, overlay.Width);

            overlay.CenterX = double.NaN;
            Assert.Equal(0d, overlay.CenterX);
        }
    }
}

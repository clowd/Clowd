using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Clowd.Drawing.Graphics;
using Xunit;

namespace Clowd.Drawing.Tests
{
    /// <summary>
    /// Pins the in-place undo restore of every graphic type (final-design §B.4 / risk #2): after a
    /// field is mutated, committed and undone, the SAME live instance — caches and all — must
    /// behave exactly like a graphic freshly deserialized from the pre-mutation snapshot. A missed
    /// transient/cache invalidation in <c>OnFieldsRestored</c> (a stale fitted polyline geometry, a
    /// stale image obscure overlay, a stale cached bounds) would surface here as a Bounds or
    /// hit-test divergence from the fresh reference. The two caches that live OUTSIDE the
    /// RenderCache sidecar — polyline <c>_final</c> and image <c>_imageObscured</c>, which the
    /// conservative bare-raise clear does not touch — are asserted directly.
    /// </summary>
    public class InPlaceRestoreTests
    {
        static InPlaceRestoreTests()
        {
            Clowd.Config.SettingsRoot.Current ??= new Clowd.Config.SettingsRoot();
        }

        private static readonly DpiScale Dpi = new DpiScale(1, 1);

        private static readonly FieldInfo PolyPoints =
            typeof(GraphicPolyLine).GetField("_points", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo PolyFinal =
            typeof(GraphicPolyLine).GetField("_final", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo PolyBuildFinal =
            typeof(GraphicPolyLine).GetMethod("BuildFinalGeometry", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo ImgObscured =
            typeof(GraphicImage).GetField("_imageObscured", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo ImgSource =
            typeof(GraphicImage).GetField("_imageSource", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo ImgUpdateObscure =
            typeof(GraphicImage).GetMethod("UpdateObscureCache", BindingFlags.Instance | BindingFlags.NonPublic);

        // ====================================================================
        // Generic bounds + hit-test parity (rect / filled-rect / ellipse / line / arrow / text / count)
        // ====================================================================

        [AvaloniaFact]
        public void Rectangle_UndoRestoresBoundsAndHitTest()
        {
            var g = new GraphicRectangle(Colors.Red, 2, new Rect(20, 20, 80, 60));
            RunTypeParity(g, () => g.Right += 37, "rectangle");
        }

        [AvaloniaFact]
        public void FilledRectangle_UndoRestoresBoundsAndHitTest()
        {
            var g = new GraphicFilledRectangle(Colors.Green, new Rect(15, 25, 70, 55));
            RunTypeParity(g, () => g.Bottom += 41, "filled-rectangle");
        }

        [AvaloniaFact]
        public void Ellipse_UndoRestoresBoundsAndHitTest()
        {
            var g = new GraphicEllipse(Colors.Blue, 3, new Rect(30, 30, 90, 60));
            RunTypeParity(g, () => g.Right += 44, "ellipse");
        }

        [AvaloniaFact]
        public void Line_UndoRestoresBoundsAndHitTest()
        {
            var g = new GraphicLine(Colors.Purple, 4, new Point(20, 20), new Point(100, 70));
            RunTypeParity(g, () => g.LineEnd = new Point(140, 30), "line");
        }

        [AvaloniaFact]
        public void Arrow_UndoRestoresBoundsAndHitTest()
        {
            var g = new GraphicArrow(Colors.Teal, 4, new Point(20, 30), new Point(110, 80));
            RunTypeParity(g, () => g.LineEnd = new Point(150, 40), "arrow");
        }

        [AvaloniaFact]
        public void CurvedArrow_UndoRestoresBoundsAndHitTest()
        {
            // the bow lives in its own field and feeds every cached geometry slot (curve, shaft,
            // tip), so undoing it must leave nothing of the curved shape behind
            var g = new GraphicArrow(Colors.Teal, 4, new Point(20, 30), new Point(110, 80)) { CurveOffset = 40 };
            RunTypeParity(g, () => g.CurveOffset = -25, "curved-arrow");
        }

        [AvaloniaFact]
        public void Text_UndoRestoresBoundsAndHitTest()
        {
            var g = new GraphicText(Colors.Black, 2, new Point(40, 40), 0, "hi");
            RunTypeParity(g, () => g.Body = "a considerably longer note body", "text");
        }

        [AvaloniaFact]
        public void Count_UndoRestoresBoundsAndHitTest()
        {
            var g = new GraphicCount(Colors.Black, 2, new Point(60, 60), "3");
            RunTypeParity(g, () => g.FontSize = 36, "count");
        }

        // ====================================================================
        // PolyLine — the fitted geometry (_final, a [Transient] field OUTSIDE the RenderCache) must
        // be dropped when the recorded points are restored (final-design §B.4, spec callout)
        // ====================================================================

        [AvaloniaFact]
        public void PolyLine_UndoOfPointsEdit_DropsFinalGeometry_AndRestoresPointsAndBounds()
        {
            var canvas = new DrawingCanvas { Tool = ToolType.None };
            var poly = new GraphicPolyLine(Colors.Blue, 3, new Point(10, 10));
            poly.AddPoint(new Point(40, 15));
            poly.AddPoint(new Point(70, 50));
            poly.EndDrawing(true);
            canvas.GraphicsList.Add(poly);
            canvas.AddCommandToHistory(false);

            var stateA = GraphicsSerializer.SerializeToUtf8Bytes(new[] { (GraphicBase)poly });
            var pointsA = ((List<Point>)PolyPoints.GetValue(poly)).ToList();

            // mutate the recorded points (no raise of their own — ride along with a LineWidth raise,
            // exactly as the tools commit a point edit) and rebuild _final so it now reflects state B
            var pts = (List<Point>)PolyPoints.GetValue(poly);
            pts.Add(new Point(400, 300));
            poly.LineWidth += 1;
            PolyBuildFinal.Invoke(poly, null);
            _ = poly.Bounds; // warm the (state-B) cached bounds off the rebuilt geometry
            canvas.AddCommandToHistory(false);

            canvas.Undo();

            // OnFieldsRestored must have dropped the stale fitted geometry so it rebuilds from the
            // restored points; a broken override would leave state B's _final in place
            Assert.Null(PolyFinal.GetValue(poly));
            Assert.Equal(pointsA, (List<Point>)PolyPoints.GetValue(poly));

            var fresh = (GraphicPolyLine)Fresh(stateA);
            Assert.True(RectClose(poly.Bounds, fresh.Bounds, 1e-6),
                        $"polyline bounds mismatch: restored={poly.Bounds} fresh={fresh.Bounds}");
        }

        [AvaloniaFact]
        public void PolyLine_UndoOfMove_BoundsCorrectWithoutRender()
        {
            // Regression: ComputeBounds used to trust whatever fitted transform `_final` carried.
            // After a render → Move → commit → render → Undo, the first Bounds read (before any
            // further render pass refits) froze the PRE-UNDO moved rect into CachedBounds, poisoning
            // ContentBounds, shadow placement and the export crop until the next edit. ComputeBounds
            // must refit to the CURRENT UnrotatedBounds itself (EnsureFittedTransform).
            var canvas = new DrawingCanvas { Tool = ToolType.None };
            var poly = new GraphicPolyLine(Colors.Blue, 3, new Point(10, 10));
            poly.AddPoint(new Point(40, 15));
            poly.AddPoint(new Point(70, 50));
            poly.EndDrawing(true);
            canvas.GraphicsList.Add(poly);
            canvas.AddCommandToHistory(false);

            using (canvas.GraphicsList.DrawGraphicsToBitmap(Brushes.White)) { } // force a render fit
            var preMoveLeft = poly.Bounds.Left;

            poly.Move(300, 0);
            canvas.AddCommandToHistory(false);
            using (canvas.GraphicsList.DrawGraphicsToBitmap(Brushes.White)) { } // refit at the moved position

            canvas.Undo();

            // WITHOUT rendering again: the first cold Bounds read must reflect the restored position
            Assert.True(Math.Abs(poly.Bounds.Left - preMoveLeft) <= 1.0,
                        $"post-undo Bounds.Left={poly.Bounds.Left} expected ~{preMoveLeft} (stale fitted transform)");
        }

        // ====================================================================
        // Image — the obscure overlay (_imageObscured, also OUTSIDE the RenderCache) must be dropped
        // when obscuredShapes is restored, while the decoded source (unaffected) is kept
        // ====================================================================

        [AvaloniaFact]
        public void Image_UndoOfPixelate_DropsObscureOverlay_KeepsDecode()
        {
            var dir = Directory.CreateTempSubdirectory();
            try
            {
                var path = Path.Combine(dir.FullName, "img.png");
                WritePng(path, 40, 30);

                var canvas = new DrawingCanvas { Tool = ToolType.None };
                var img = new GraphicImage(path, new Rect(0, 0, 40, 30), default);
                canvas.GraphicsList.Add(img);
                canvas.AddCommandToHistory(false);

                var src = img.ImageSource;
                Assert.NotNull(src);

                img.ObscuredShapes = new[]
                {
                    new GraphicImage.ObscuredShape(new Point(4, 4), new Point(30, 4), new Point(30, 24), new Point(4, 24), 4),
                };
                canvas.AddCommandToHistory(false);

                // build the pixelate overlay so there is a stale cache to drop
                Assert.True((bool)ImgUpdateObscure.Invoke(img, null));
                Assert.NotNull(ImgObscured.GetValue(img));

                canvas.Undo();

                Assert.Empty(img.ObscuredShapes);
                Assert.Null(ImgObscured.GetValue(img));  // OnFieldsRestored dropped the stale overlay
                Assert.Same(src, img.ImageSource);        // a non-source edit keeps the decoded bitmap
            }
            finally
            {
                dir.Delete(true);
            }
        }

        [AvaloniaFact]
        public void Image_UndoOfResize_KeepsDecode_RestoresBoundsAndHitTest()
        {
            var dir = Directory.CreateTempSubdirectory();
            try
            {
                var path = Path.Combine(dir.FullName, "img.png");
                WritePng(path, 40, 30);

                var canvas = new DrawingCanvas { Tool = ToolType.None };
                var img = new GraphicImage(path, new Rect(0, 0, 40, 30), default);
                canvas.GraphicsList.Add(img);
                canvas.AddCommandToHistory(false);

                var src = img.ImageSource;
                Assert.NotNull(src);
                var stateA = GraphicsSerializer.SerializeToUtf8Bytes(new[] { (GraphicBase)img });

                Warm(img);
                img.Right += 25;
                canvas.AddCommandToHistory(false);
                Warm(img);

                canvas.Undo();

                Assert.Same(src, img.ImageSource);        // a resize does not re-null the decode
                AssertParity(img, Fresh(stateA), "image-resize");
            }
            finally
            {
                dir.Delete(true);
            }
        }

        // ====================================================================
        // helpers
        // ====================================================================

        private static void RunTypeParity(GraphicBase g, Action mutate, string ctx)
        {
            var canvas = new DrawingCanvas { Tool = ToolType.None };
            canvas.GraphicsList.Add(g);
            canvas.AddCommandToHistory(false);

            var stateA = GraphicsSerializer.SerializeToUtf8Bytes(new[] { g });
            Warm(g);            // warm the state-A caches
            mutate();
            canvas.AddCommandToHistory(false);
            Warm(g);            // pollute the caches with state B

            canvas.Undo();

            AssertParity(g, Fresh(stateA), ctx);
        }

        private static GraphicBase Fresh(byte[] snapshot)
        {
            var g = GraphicsSerializer.DeserializeFromUtf8Bytes(snapshot)[0];
            g.Normalize(); // mirror the restore path (and ClearHistory's deserialize), idempotent here
            return g;
        }

        private static void Warm(GraphicBase g)
        {
            _ = g.Bounds;
            var r = g.Bounds.Inflate(15);
            for (int ix = 0; ix <= 6; ix++)
                for (int iy = 0; iy <= 6; iy++)
                    _ = g.MakeHitTest(GridPoint(r, ix, iy), Dpi);
        }

        private static void AssertParity(GraphicBase actual, GraphicBase expected, string ctx)
        {
            Assert.True(RectClose(actual.Bounds, expected.Bounds, 1e-6),
                        $"{ctx}: bounds mismatch actual={actual.Bounds} expected={expected.Bounds}");

            var r = expected.Bounds.Inflate(15);
            for (int ix = 0; ix <= 6; ix++)
                for (int iy = 0; iy <= 6; iy++)
                {
                    var p = GridPoint(r, ix, iy);
                    var e = expected.MakeHitTest(p, Dpi);
                    var a = actual.MakeHitTest(p, Dpi);
                    Assert.True(e == a, $"{ctx}: hit-test mismatch at {p} expected={e} actual={a}");
                }
        }

        private static Point GridPoint(Rect r, int ix, int iy) =>
            new Point(r.Left + r.Width * ix / 6.0, r.Top + r.Height * iy / 6.0);

        private static bool RectClose(Rect a, Rect b, double tol) =>
            Math.Abs(a.X - b.X) < tol && Math.Abs(a.Y - b.Y) < tol
            && Math.Abs(a.Width - b.Width) < tol && Math.Abs(a.Height - b.Height) < tol;

        private static void WritePng(string path, int width, int height)
        {
            using var wb = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96),
                                               PixelFormats.Bgra8888, AlphaFormat.Premul);
            wb.Save(path, PngBitmapEncoderOptions.Default);
        }
    }
}

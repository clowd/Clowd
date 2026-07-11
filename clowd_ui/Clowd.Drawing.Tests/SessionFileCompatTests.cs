using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Clowd.Drawing.Graphics;
using Xunit;

namespace Clowd.Drawing.Tests
{
    /// <summary>
    /// Pins backward compatibility with graphics.json files written by the PRE-REBUILD build
    /// (integration.md A.2/A.4): the exact on-disk shape — {"BackgroundColor":"#AARRGGBB",
    /// "Graphics":[{"$type":&lt;short name&gt;, &lt;field-name-minus-underscore&gt;: ...}]} with
    /// value types as single strings — must load through <see cref="DrawingCanvas.RestoreState"/>
    /// into the live document, seed the undo baseline, and never raise StateUpdated during the
    /// load (contract #23). The fixture below is a literal old-format document covering all nine
    /// concrete graphic types with their full persisted field lists; it must never be regenerated
    /// from the current serializer (that would test the serializer against itself).
    /// </summary>
    public class SessionFileCompatTests
    {
        static SessionFileCompatTests()
        {
            Clowd.Config.SettingsRoot.Current ??= new Clowd.Config.SettingsRoot();
        }

        private const string OldBuildGraphicsJson = """
            {
              "BackgroundColor": "#FF1E1E1E",
              "Graphics": [
                {"$type":"GraphicRectangle","id":"fixture-rect","objectColor":"#FFFF0000","lineWidth":2,"dropShadowEffect":true,"centerOfRotation":"60,45","left":20,"top":20,"right":100,"bottom":70,"angle":15},
                {"$type":"GraphicFilledRectangle","id":"fixture-filled","objectColor":"#80336699","lineWidth":0,"dropShadowEffect":true,"centerOfRotation":"40,46","left":5,"top":6,"right":75,"bottom":86,"angle":0},
                {"$type":"GraphicEllipse","id":"fixture-ellipse","objectColor":"#FF008080","lineWidth":4,"dropShadowEffect":true,"centerOfRotation":"16,22","left":1,"top":2,"right":31,"bottom":42,"angle":0},
                {"$type":"GraphicLine","id":"fixture-line","objectColor":"#FFDC143C","lineWidth":2,"dropShadowEffect":true,"lineStart":"1.5,2.5","lineEnd":"99.75,-10"},
                {"$type":"GraphicArrow","id":"fixture-arrow","objectColor":"#FF00FF00","lineWidth":6,"dropShadowEffect":true,"lineStart":"0,0","lineEnd":"50,25"},
                {"$type":"GraphicPolyLine","id":"fixture-poly","objectColor":"#FF000080","lineWidth":3,"dropShadowEffect":true,"centerOfRotation":"40,30","left":10,"top":10,"right":70,"bottom":50,"angle":0,"points":["10,10","40,15","70,50"]},
                {"$type":"GraphicText","id":"fixture-text","objectColor":"#FFF0E68C","lineWidth":2,"dropShadowEffect":true,"centerOfRotation":"90,80","left":40,"top":50,"right":140,"bottom":110,"angle":0,"body":"Hello\r\nWorld","fontName":"Arial","fontSize":18,"fontStyle":"Italic","fontWeight":"Bold","fontStretch":"Condensed"},
                {"$type":"GraphicCount","id":"fixture-count","objectColor":"#FFFF4500","lineWidth":2,"dropShadowEffect":true,"centerOfRotation":"70,70","left":60,"top":60,"right":80,"bottom":80,"angle":0,"body":"7","fontName":"Segoe UI","fontSize":12,"fontStyle":"Normal","fontWeight":"Normal","fontStretch":"Normal"},
                {"$type":"GraphicImage","id":"fixture-image","objectColor":"#00FFFFFF","lineWidth":0,"dropShadowEffect":false,"centerOfRotation":"160,120","left":10,"top":20,"right":310,"bottom":220,"angle":0,"cursorFilePath":"C:\\sessions\\abc\\cursor.png","cursorPosition":"40,50,32,32","cursorVisible":true,"bitmapFilePath":"C:\\sessions\\abc\\desktop.png","scaleX":-1,"scaleY":1,"crop":"5,6,290,190","originalSize":"300,200","obscuredShapes":[{"P0":"1,2","P1":"3,4","P2":"5,6","P3":"7,8","BlurRadius":12}]}
              ]
            }
            """;

        [AvaloniaFact]
        public void OldBuildGraphicsJson_RestoresAllNineTypes_InOrder_WithoutRaising()
        {
            var canvas = new DrawingCanvas();
            int raises = 0;
            canvas.StateUpdated += (_, _) => raises++;

            canvas.RestoreState((JsonObject)JsonNode.Parse(OldBuildGraphicsJson));

            Assert.Equal(0, raises); // contract #23: restore-load never raises
            Assert.Equal(Color.Parse("#FF1E1E1E"), canvas.ArtworkBackground);
            Assert.False(canvas.CommandUndo.CanExecute(null)); // loaded state IS the undo baseline

            // z-order (list order) preserved exactly as written
            Assert.Equal(new[]
            {
                "fixture-rect", "fixture-filled", "fixture-ellipse", "fixture-line", "fixture-arrow",
                "fixture-poly", "fixture-text", "fixture-count", "fixture-image",
            }, canvas.GraphicsList.Select(g => g.Id).ToArray());

            var rect = Assert.IsType<GraphicRectangle>(canvas.GraphicsList[0]);
            Assert.Equal(Colors.Red, rect.ObjectColor);
            Assert.Equal(2, rect.LineWidth);
            Assert.True(rect.DropShadowEffect);
            // load Normalize()s each graphic (parity with the old SetState); with a committed
            // (already-normalized) fixture that is idempotent up to floating-point round-trips
            Assert.Equal(20, rect.Left, 9);
            Assert.Equal(20, rect.Top, 9);
            Assert.Equal(100, rect.Right, 9);
            Assert.Equal(70, rect.Bottom, 9);
            Assert.Equal(15, rect.Angle, 9);

            var filled = Assert.IsType<GraphicFilledRectangle>(canvas.GraphicsList[1]);
            Assert.Equal(Color.Parse("#80336699"), filled.ObjectColor);

            var ellipse = Assert.IsType<GraphicEllipse>(canvas.GraphicsList[2]);
            Assert.Equal(1, ellipse.Left, 9);
            Assert.Equal(42, ellipse.Bottom, 9);

            var line = Assert.IsType<GraphicLine>(canvas.GraphicsList[3]);
            Assert.Equal(new Point(1.5, 2.5), line.LineStart);
            Assert.Equal(new Point(99.75, -10), line.LineEnd);

            var arrow = Assert.IsType<GraphicArrow>(canvas.GraphicsList[4]);
            Assert.Equal(new Point(50, 25), arrow.LineEnd);

            var poly = Assert.IsType<GraphicPolyLine>(canvas.GraphicsList[5]);
            var pointsField = typeof(GraphicPolyLine).GetField("_points", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.Equal(new[] { new Point(10, 10), new Point(40, 15), new Point(70, 50) },
                         (List<Point>)pointsField.GetValue(poly));

            var text = Assert.IsType<GraphicText>(canvas.GraphicsList[6]);
            Assert.Equal("Hello\r\nWorld", text.Body);
            Assert.Equal("Arial", text.FontName);
            Assert.Equal(18, text.FontSize);
            Assert.Equal(FontStyle.Italic, text.FontStyle);
            Assert.Equal(FontWeight.Bold, text.FontWeight);
            Assert.Equal(FontStretch.Condensed, text.FontStretch);
            Assert.Equal(40, text.Left, 9); // Right/Bottom re-derive from the measured text
            Assert.False(text.Editing);

            var count = Assert.IsType<GraphicCount>(canvas.GraphicsList[7]);
            Assert.Equal("7", count.Body);

            var image = Assert.IsType<GraphicImage>(canvas.GraphicsList[8]);
            Assert.Equal(@"C:\sessions\abc\desktop.png", image.BitmapFilePath);
            Assert.Equal(@"C:\sessions\abc\cursor.png", image.CursorFilePath);
            Assert.Equal(new PixelRect(40, 50, 32, 32), image.CursorPosition);
            Assert.True(image.CursorVisible);
            Assert.Equal(new PixelRect(5, 6, 290, 190), image.Crop);
            Assert.Equal(-1, image.FlipX);
            Assert.Equal(1, image.FlipY);
            Assert.Equal(new Size(300, 200), image.OriginalSize);
            Assert.Equal(new GraphicImage.ObscuredShape(new Point(1, 2), new Point(3, 4), new Point(5, 6), new Point(7, 8), 12),
                         Assert.Single(image.ObscuredShapes));
            Assert.False(image.Editing);
            Assert.False(image.IsSelected); // transient — always resets on load

            // the loaded state seeds the undo baseline: one edit + commit, one undo → back to it
            rect.ObjectColor = Colors.Yellow;
            canvas.AddCommandToHistory(false);
            Assert.True(canvas.CommandUndo.CanExecute(null));
            Assert.Equal(1, raises); // the discrete commit raised StateUpdated (the load did not)

            canvas.Undo();
            Assert.Equal(Colors.Red, rect.ObjectColor);
            Assert.Equal(9, canvas.GraphicsList.Count);
        }

        [AvaloniaFact]
        public void PasteTwice_PreservesOrder_AndRegeneratesDuplicateIds()
        {
            // the clipboard payload is a bare polymorphic array; pasting it into a canvas that
            // already contains those ids (copy → paste in the same session) must keep element
            // order and mint fresh ids for the collisions (integration.md A.5)
            var canvas = new DrawingCanvas();
            var payload = new GraphicBase[]
            {
                new GraphicRectangle(Colors.Red, 2, new Rect(0, 0, 10, 10)),
                new GraphicEllipse(Colors.Green, 3, new Rect(5, 5, 20, 20)),
            };
            var bytes = GraphicsSerializer.SerializeToUtf8Bytes(payload);

            canvas.AddGraphics(GraphicsSerializer.DeserializeFromUtf8Bytes(bytes));
            canvas.AddGraphics(GraphicsSerializer.DeserializeFromUtf8Bytes(bytes)); // same ids again

            Assert.Equal(4, canvas.GraphicsList.Count);
            Assert.IsType<GraphicRectangle>(canvas.GraphicsList[0]);
            Assert.IsType<GraphicEllipse>(canvas.GraphicsList[1]);
            Assert.IsType<GraphicRectangle>(canvas.GraphicsList[2]);
            Assert.IsType<GraphicEllipse>(canvas.GraphicsList[3]);

            Assert.Equal(payload[0].Id, canvas.GraphicsList[0].Id); // first paste keeps its ids
            Assert.Equal(payload[1].Id, canvas.GraphicsList[1].Id);
            Assert.Equal(4, canvas.GraphicsList.Select(g => g.Id).Distinct().Count());
        }
    }
}

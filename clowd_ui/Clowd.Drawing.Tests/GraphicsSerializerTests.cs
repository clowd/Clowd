using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Clowd.Drawing.Graphics;
using Xunit;

namespace Clowd.Drawing.Tests
{
    /// <summary>
    /// Round-trips every concrete <see cref="GraphicBase"/> subclass through
    /// <see cref="GraphicsSerializer"/> (the same code path used by undo snapshots, the session
    /// file and the clipboard) and asserts that the persisted state survives and the transient
    /// state resets.
    /// </summary>
    public class GraphicsSerializerTests
    {
        private static T RoundTrip<T>(T graphic) where T : GraphicBase
        {
            var bytes = GraphicsSerializer.SerializeToUtf8Bytes(new GraphicBase[] { graphic });
            var restored = GraphicsSerializer.DeserializeFromUtf8Bytes(bytes);
            var single = Assert.Single(restored);
            return Assert.IsType<T>(single);
        }

        private static void AssertBaseState(GraphicBase expected, GraphicBase actual)
        {
            Assert.Equal(expected.Id, actual.Id);
            Assert.Equal(expected.ObjectColor, actual.ObjectColor);
            Assert.Equal(expected.LineWidth, actual.LineWidth);
            Assert.Equal(expected.DropShadowEffect, actual.DropShadowEffect);
            Assert.False(actual.IsSelected); // transient — always resets
        }

        private static void AssertRectangleState(GraphicRectangle expected, GraphicRectangle actual)
        {
            AssertBaseState(expected, actual);
            Assert.Equal(expected.Left, actual.Left);
            Assert.Equal(expected.Top, actual.Top);
            Assert.Equal(expected.Right, actual.Right);
            Assert.Equal(expected.Bottom, actual.Bottom);
            Assert.Equal(expected.Angle, actual.Angle);
            Assert.Equal(expected.CenterOfRotation, actual.CenterOfRotation);
        }

        [AvaloniaFact]
        public void Rectangle_RoundTrips()
        {
            var g = new GraphicRectangle(Color.FromArgb(255, 10, 20, 30), 3.5, new Rect(10.25, 20.5, 100, 50), 30);
            g.IsSelected = true;

            var r = RoundTrip(g);
            AssertRectangleState(g, r);
        }

        [AvaloniaFact]
        public void FilledRectangle_RoundTrips()
        {
            var g = new GraphicFilledRectangle(Colors.Coral, new Rect(5, 6, 70, 80), 12.5);
            var r = RoundTrip(g);
            AssertRectangleState(g, r);
        }

        [AvaloniaFact]
        public void Ellipse_RoundTrips()
        {
            var g = new GraphicEllipse(Colors.Teal, 4, new Rect(1, 2, 30, 40), -22.5);
            var r = RoundTrip(g);
            AssertRectangleState(g, r);
        }

        [AvaloniaFact]
        public void Line_RoundTrips()
        {
            var g = new GraphicLine(Colors.Crimson, 2, new Point(1.5, 2.5), new Point(99.75, -10));
            var r = RoundTrip(g);
            AssertBaseState(g, r);
            Assert.Equal(g.LineStart, r.LineStart);
            Assert.Equal(g.LineEnd, r.LineEnd);
        }

        [AvaloniaFact]
        public void Arrow_RoundTrips()
        {
            var g = new GraphicArrow(Colors.Lime, 6, new Point(0, 0), new Point(50, 25));
            var r = RoundTrip(g);
            AssertBaseState(g, r);
            Assert.Equal(g.LineStart, r.LineStart);
            Assert.Equal(g.LineEnd, r.LineEnd);
        }

        [AvaloniaFact]
        public void Text_RoundTrips_AndEditingIsTransient()
        {
            var g = new GraphicText(Colors.Khaki, 2, new Point(40, 50), 4, "Hello\r\nWorld")
            {
                FontName = "Arial",
                FontSize = 18,
                FontStyle = FontStyle.Italic,
                FontWeight = FontWeight.Bold,
                FontStretch = FontStretch.Condensed,
                Editing = true,
            };

            var r = RoundTrip(g);
            AssertRectangleState(g, r);
            Assert.Equal(g.Body, r.Body);
            Assert.Equal(g.FontName, r.FontName);
            Assert.Equal(g.FontSize, r.FontSize);
            Assert.Equal(g.FontStyle, r.FontStyle);
            Assert.Equal(g.FontWeight, r.FontWeight);
            Assert.Equal(g.FontStretch, r.FontStretch);
            Assert.False(r.Editing); // transient — always resets
        }

        [AvaloniaFact]
        public void Count_RoundTrips()
        {
            var g = new GraphicCount(Colors.Red, 2, new Point(10, 10), "7");
            var r = RoundTrip(g);
            AssertRectangleState(g, r);
            Assert.Equal("7", r.Body);
        }

        [AvaloniaFact]
        public void PolyLine_RoundTrips_Points()
        {
            var g = new GraphicPolyLine(Colors.Navy, 3, new Point(0, 0));
            g.AddPoint(new Point(10, 5));
            g.AddPoint(new Point(20, 12));
            g.AddPoint(new Point(35, 30));
            g.EndDrawing(true);

            var r = RoundTrip(g);
            AssertRectangleState(g, r);

            // the persisted geometry source is the private point list
            var pointsField = typeof(GraphicPolyLine).GetField("_points", BindingFlags.Instance | BindingFlags.NonPublic);
            var expectedPoints = (List<Point>)pointsField.GetValue(g);
            var actualPoints = (List<Point>)pointsField.GetValue(r);
            Assert.Equal(expectedPoints, actualPoints);

            // transient drawing state resets
            var drawingField = typeof(GraphicPolyLine).GetField("_drawing", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.False((bool)drawingField.GetValue(r));
        }

        [AvaloniaFact]
        public void Image_RoundTrips()
        {
            var g = new GraphicImage(
                @"C:\sessions\abc\desktop.png",
                new Rect(10, 20, 300, 200),
                new PixelRect(5, 6, 290, 190),
                angle: 15,
                flipX: -1,
                flipY: 1,
                cursorFilePath: @"C:\sessions\abc\cursor.png",
                cursorPosition: new PixelRect(40, 50, 32, 32),
                cursorVisible: true);

            g.ObscuredShapes = new[]
            {
                new GraphicImage.ObscuredShape(new Point(1, 2), new Point(3, 4), new Point(5, 6), new Point(7, 8), 12),
                new GraphicImage.ObscuredShape(new Point(9, 9), new Point(10, 10), new Point(11, 11), new Point(12, 12), 0),
            };

            var r = RoundTrip(g);
            AssertRectangleState(g, r);
            Assert.Equal(g.BitmapFilePath, r.BitmapFilePath);
            Assert.Equal(g.CursorFilePath, r.CursorFilePath);
            Assert.Equal(g.CursorPosition, r.CursorPosition);
            Assert.Equal(g.CursorVisible, r.CursorVisible);
            Assert.Equal(g.Crop, r.Crop);
            Assert.Equal(g.FlipX, r.FlipX);
            Assert.Equal(g.FlipY, r.FlipY);
            Assert.Equal(g.OriginalSize, r.OriginalSize);
            Assert.Equal(g.ObscuredShapes, r.ObscuredShapes);
            Assert.False(r.Editing); // crop-editing state is transient
        }

        [AvaloniaFact]
        public void PolymorphicArray_RoundTrips_AsClipboardDoes()
        {
            var graphics = new GraphicBase[]
            {
                new GraphicRectangle(Colors.Red, 2, new Rect(0, 0, 10, 10)),
                new GraphicEllipse(Colors.Green, 3, new Rect(5, 5, 20, 20)),
                new GraphicLine(Colors.Blue, 1, new Point(0, 0), new Point(9, 9)),
                new GraphicArrow(Colors.Black, 4, new Point(1, 1), new Point(8, 2)),
                new GraphicImage(@"C:\img.png", new Size(64, 64)),
            };

            var bytes = GraphicsSerializer.SerializeToUtf8Bytes(graphics);
            var restored = GraphicsSerializer.DeserializeFromUtf8Bytes(bytes);

            Assert.Equal(graphics.Length, restored.Length);
            for (int i = 0; i < graphics.Length; i++)
            {
                Assert.IsType(graphics[i].GetType(), restored[i]);
                Assert.Equal(graphics[i].Id, restored[i].Id);
            }

            // the payload is plain UTF-8 JSON with a $type discriminator per graphic
            var json = Encoding.UTF8.GetString(bytes);
            Assert.Contains("\"$type\":\"GraphicRectangle\"", json);
            Assert.Contains("\"$type\":\"GraphicImage\"", json);
        }

        [AvaloniaFact]
        public void EveryConcreteGraphicType_IsCoveredByARoundTripTest()
        {
            // guards against a new graphic type being added without a serialization test;
            // these are exactly the types the serializer registers as $type-discriminated.
            var concrete = typeof(GraphicBase).Assembly
                                              .GetTypes()
                                              .Where(t => t.IsPublic && !t.IsAbstract && typeof(GraphicBase).IsAssignableFrom(t))
                                              .Select(t => t.Name)
                                              .OrderBy(n => n, StringComparer.Ordinal)
                                              .ToArray();

            Assert.Equal(new[]
            {
                "GraphicArrow", "GraphicCount", "GraphicEllipse", "GraphicFilledRectangle",
                "GraphicImage", "GraphicLine", "GraphicPolyLine", "GraphicRectangle", "GraphicText",
            }, concrete);
        }
    }
}

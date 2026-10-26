using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Clowd.Drawing.Graphics;
using Xunit;

namespace Clowd.Drawing.Tests
{
    /// <summary>
    /// Sessions saved by builds with graphic types that no longer exist (e.g. raster v1's
    /// GraphicRaster) must still load: <see cref="GraphicsSerializer"/> drops array elements
    /// whose "$type" discriminator is not registered and deserializes the rest, while malformed
    /// JSON keeps failing exactly as before.
    /// </summary>
    public class UnknownGraphicTypeTests
    {
        // shaped like a real raster v1 element; the body is irrelevant because the unknown
        // discriminator drops the whole element before its properties are ever read
        private const string UnknownElement =
            "{\"$type\":\"GraphicRaster\",\"id\":\"AAAAAAAA\",\"objectColor\":\"#FF112233\",\"lineWidth\":2," +
            "\"left\":10,\"top\":20,\"right\":110,\"bottom\":120,\"pixelWidth\":100,\"pixelHeight\":100," +
            "\"baseImagePath\":null,\"strokes\":[{\"Color\":\"#FF000000\",\"Size\":4,\"Erase\":true,\"Points\":[\"1,2\",\"3,4\"]}]}";

        [AvaloniaFact]
        public void UnknownType_IsSkipped_KnownGraphicsLoad()
        {
            var rect = new GraphicRectangle(Color.FromArgb(255, 10, 20, 30), 3.5, new Rect(10, 20, 100, 50));
            var rectJson = Encoding.UTF8.GetString(GraphicsSerializer.SerializeToUtf8Bytes(new GraphicBase[] { rect }))
                                   .TrimStart('[')
                                   .TrimEnd(']');

            var payload = Encoding.UTF8.GetBytes("[" + UnknownElement + "," + rectJson + "]");
            var restored = GraphicsSerializer.DeserializeFromUtf8Bytes(payload);

            var single = Assert.Single(restored);
            var r = Assert.IsType<GraphicRectangle>(single);
            Assert.Equal(rect.Id, r.Id);
            Assert.Equal(rect.ObjectColor, r.ObjectColor);
            Assert.Equal(rect.Left, r.Left);
            Assert.Equal(rect.Bottom, r.Bottom);
        }

        [AvaloniaFact]
        public void AllUnknownTypes_YieldEmptyList()
        {
            var payload = Encoding.UTF8.GetBytes("[" + UnknownElement + "," + UnknownElement + "]");
            var restored = GraphicsSerializer.DeserializeFromUtf8Bytes(payload);
            Assert.Empty(restored);
        }

        [AvaloniaFact]
        public void MalformedJson_StillThrows()
        {
            var payload = Encoding.UTF8.GetBytes("[{\"$type\":\"GraphicRectangle\",");
            Assert.ThrowsAny<JsonException>(() => GraphicsSerializer.DeserializeFromUtf8Bytes(payload));
        }
    }
}

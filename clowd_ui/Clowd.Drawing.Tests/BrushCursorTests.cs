using Clowd.Drawing.Tools;
using Xunit;

namespace Clowd.Drawing.Tests
{
    /// <summary>The brush cursor's ring: centred on the hotspot, white inside black, and
    /// clamped to a visible, Windows-safe size.</summary>
    public class BrushCursorTests
    {
        private static (byte b, byte a) Px(byte[] pixels, int size, int x, int y)
        {
            int i = (y * size + x) * 4;
            return (pixels[i], pixels[i + 3]);
        }

        [Fact]
        public void Rasterize_SharesTheDiametersParity_WithAnEmptyCentre_CrispWhiteInsideBlack()
        {
            var pixels = BrushCursor.Rasterize(20, out int size);
            Assert.Equal(0, size % 2);
            int mid = size / 2;

            Assert.Equal(0, Px(pixels, size, mid, mid).a); // see-through middle

            // walking right from the centre: one fully white pixel, then one fully black, then nothing
            // (the row just below the centre, which sits on a pixel corner, so within a hair of the axis)
            var white = Px(pixels, size, mid + 9, mid);
            var black = Px(pixels, size, mid + 10, mid);
            var outside = Px(pixels, size, mid + 11, mid);
            Assert.True(white.b >= 250 && white.a == 255, $"white {white}");
            Assert.True(black.b <= 5 && black.a >= 250, $"black {black}");
            Assert.True(outside.a <= 5, $"outside {outside}");
        }

        [Fact]
        public void Rasterize_IsSymmetric()
        {
            var pixels = BrushCursor.Rasterize(13, out int size);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    Assert.Equal(Px(pixels, size, x, y), Px(pixels, size, size - 1 - x, y));
                    Assert.Equal(Px(pixels, size, x, y), Px(pixels, size, y, x));
                }
        }

        [Theory]
        [InlineData(0.5, BrushCursor.MinDiameter)]
        [InlineData(12.4, 12)]
        [InlineData(5000, BrushCursor.MaxSize - 4)]
        public void Quantize_ClampsToAVisibleWindowsSafeRing(double diameter, int expected)
        {
            Assert.Equal(expected, BrushCursor.Quantize(diameter));
            BrushCursor.Rasterize(BrushCursor.Quantize(diameter), out int size);
            Assert.True(size <= BrushCursor.MaxSize);
        }
    }
}

using System;
using Avalonia.Media;
using Prisc.Interop.Testing;
using Xunit;

namespace Prisc.Shared.Tests
{
    /// <summary>
    /// <see cref="OkLchColor"/>: its conversions are <c>prisc_rust_core::color</c>'s (vectors there:
    /// the CSS reference values, the sRGB round trip, greys, the gamut mapping), so these check the
    /// hand-off, alpha and the gamut flag; the CSS text is this type's own and is pinned in full.
    /// </summary>
    public class OkLchColorTests
    {
        [Fact]
        public void FromColor_MatchesTheReferenceRed_AndKeepsAlpha()
        {
            InteropTestGate.Require();
            var oklch = OkLchColor.FromColor(Color.FromArgb(0x80, 0xFF, 0x00, 0x00));
            Assert.Equal(0.6280, oklch.L, 0.0005);
            Assert.Equal(0.2577, oklch.C, 0.0005);
            Assert.Equal(29.23, oklch.H, 0.05);
            Assert.Equal(0x80 / 255d, oklch.Alpha, 6);
        }

        [Fact]
        public void SrgbColors_RoundTripAndAreInGamut()
        {
            InteropTestGate.Require();
            var random = new Random(1234);
            for (var i = 0; i < 200; i++)
            {
                var color = Color.FromArgb((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
                var oklch = OkLchColor.FromColor(color);
                Assert.True(oklch.IsInSrgb, color.ToString());
                Assert.Equal(color, oklch.ToColor());
            }
        }

        [Fact]
        public void Grays_AreAchromatic()
        {
            InteropTestGate.Require();
            for (var v = 0; v < 256; v += 51)
                Assert.True(OkLchColor.FromColor(Color.FromRgb((byte)v, (byte)v, (byte)v)).IsAchromatic);
            Assert.Equal(SharedDefaults.OklchChromaMax, OkLchColor.ChromaMax);
        }

        [Fact]
        public void ToColor_OutsideSrgb_LowersChromaOnly_AndPastEitherEndIsWhiteOrBlack()
        {
            InteropTestGate.Require();
            var vivid = new OkLchColor(0.7, 0.35, 150);
            Assert.False(vivid.IsInSrgb);
            var mapped = OkLchColor.FromColor(vivid.ToColor());
            Assert.Equal(0.7, mapped.L, 2);
            Assert.Equal(150, mapped.H, 0);
            Assert.True(mapped.C < 0.35);

            Assert.Equal(Colors.White, new OkLchColor(1.2, 0.1, 40).ToColor());
            Assert.Equal(Colors.Black, new OkLchColor(-0.1, 0.1, 40).ToColor());
            Assert.Equal(0x80, new OkLchColor(0.5, 0.1, 40, 0x80 / 255d).ToColor().A);
        }

        [Theory]
        [InlineData(0.628, 0.2577, 29.23, 1, "oklch(62.8% 0.2577 29.23)")]
        [InlineData(0.5, 0.1, 200, 0.5, "oklch(50% 0.1 200 / 50%)")]
        [InlineData(0.123456, 0.0000001, 359.999, 1, "oklch(12.35% 0 360)")]
        public void ToString_WritesCss(double l, double c, double h, double a, string expected)
        {
            Assert.Equal(expected, new OkLchColor(l, c, h, a).ToString());
        }

        [Theory]
        [InlineData("oklch(62.8% 0.2577 29.23)", 0.628, 0.2577, 29.23, 1)]
        [InlineData("oklch(0.7 0.1 200 / 50%)", 0.7, 0.1, 200, 0.5)]
        [InlineData("OKLCH(70% 25% 200deg / 0.25);", 0.7, 0.1, 200, 0.25)]
        [InlineData("0.7, 0.1, -40", 0.7, 0.1, 320, 1)]
        [InlineData("70 0.1 none", 0.7, 0.1, 0, 1)]
        public void TryParse_ReadsCssAndLooseForms(string text, double l, double c, double h, double a)
        {
            Assert.True(OkLchColor.TryParse(text, out var color));
            Assert.Equal(l, color.L, 4);
            Assert.Equal(c, color.C, 4);
            Assert.Equal(h, color.H, 4);
            Assert.Equal(a, color.Alpha, 4);
        }

        [Theory]
        [InlineData("")]
        [InlineData("oklch(0.7 0.1)")]
        [InlineData("oklch(0.7 0.1 200")]
        [InlineData("oklch(0.7 0.1 50%)")]
        [InlineData("rgb(1, 2, 3)")]
        public void TryParse_RejectsOtherText(string text)
        {
            Assert.False(OkLchColor.TryParse(text, out _));
        }

        [Fact]
        public void ToString_RoundTripsThroughTryParse()
        {
            var color = new OkLchColor(0.4567, 0.1234, 123.45, 0.4);
            Assert.True(OkLchColor.TryParse(color.ToString(), out var parsed));
            Assert.Equal(color, parsed);
        }
    }
}

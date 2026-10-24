using System;
using System.Linq;
using Avalonia.Media;
using Xunit;

namespace Clowd.Shared.Tests
{
    public class ImagePaletteTests
    {
        private static ImagePixels Paint(int width, int height, Func<int, int, Color> pixel)
        {
            var bgra = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var c = pixel(x, y);
                    var i = (y * width + x) * 4;
                    bgra[i] = c.B;
                    bgra[i + 1] = c.G;
                    bgra[i + 2] = c.R;
                    bgra[i + 3] = c.A;
                }
            }

            return new ImagePixels(width, height, bgra);
        }

        private static readonly Color[] Stripes = new[] { "#E5484D", "#30A46C", "#0090FF", "#F5D90A", "#6E56CF" }
            .Select(Color.Parse).ToArray();

        /// <summary>Five vertical stripes, one per color.</summary>
        private static ImagePixels StripeImage(int width = 250, int height = 120) =>
            Paint(width, height, (x, _) => Stripes[x * Stripes.Length / width]);

        private static double Lightness(Color c) => 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;

        private static double Chroma(Color c) => Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B));

        [Fact]
        public void FillCrop_IsTheCentredBoxShapedRegion()
        {
            var (x, y, w, h) = ImagePixels.FillCrop(1920, 1080, 260, 220);
            Assert.Equal(1080, h, 6);
            Assert.Equal(1080 * 260 / 220d, w, 6);
            Assert.Equal((1920 - w) / 2, x, 6);
            Assert.Equal(0, y, 6);

            // taller than the box: crops top and bottom instead
            (x, y, w, h) = ImagePixels.FillCrop(500, 1000, 260, 220);
            Assert.Equal(500, w, 6);
            Assert.Equal(500 * 220 / 260d, h, 6);
            Assert.Equal(0, x, 6);
            Assert.Equal((1000 - h) / 2, y, 6);
        }

        [Fact]
        public void Resample_AveragesWhenShrinkingAndCrops()
        {
            var checker = Paint(4, 4, (x, y) => (x + y) % 2 == 0 ? Colors.Black : Colors.White);
            var half = checker.Resample(0, 0, 4, 4, 2, 2);
            Assert.Equal(2, half.Width);
            Assert.InRange(half.GetPixel(1, 1).R, 127, 128);
            Assert.Equal(255, half.GetPixel(1, 1).A);

            // the crop picks out the third stripe alone
            var stripes = StripeImage();
            var crop = stripes.Resample(100, 0, 50, 120, 10, 10);
            Assert.All(Enumerable.Range(0, 10), x => Assert.Equal(Stripes[2], crop.GetPixel(x, 5)));
        }

        [Fact]
        public void Resample_TransparentPixelsLendNoColor()
        {
            var image = Paint(2, 1, (x, _) => x == 0 ? Colors.Red : Color.FromArgb(0, 0, 255, 0));
            var one = image.Resample(0, 0, 2, 1, 1, 1).GetPixel(0, 0);
            Assert.Equal((255, 0, 0), (one.R, one.G, one.B));
            Assert.InRange(one.A, 127, 128);
        }

        [Fact]
        public void Sample_ReadsOneSideOfAnEdgeNotABlend()
        {
            var image = StripeImage();

            // one pixel inside the first stripe, right against the second
            var sample = ImagePalette.Sample(image, 49.5 / 250, 0.5);
            Assert.Equal(Stripes[0], sample.Color);
            Assert.Equal(49.5 / 250, sample.X, 9);
        }

        [Fact]
        public void Sample_AveragesNoise()
        {
            var image = Paint(20, 20, (x, y) => (x + y) % 2 == 0 ? Color.FromRgb(100, 100, 100) : Color.FromRgb(104, 104, 104));
            var sample = ImagePalette.Sample(image, 0.5, 0.5);
            Assert.InRange(sample.Color.R, 101, 103);
        }

        [Fact]
        public void Extract_FindsEachStripeAndRingsSitOnIt()
        {
            var image = StripeImage();
            var palette = ImagePalette.Extract(image, PaletteMood.Colorful);

            Assert.Equal(ImagePalette.ColorCount, palette.Length);
            Assert.Equal(Stripes.OrderBy(c => c.ToUInt32()), palette.Select(s => s.Color).OrderBy(c => c.ToUInt32()));

            foreach (var sample in palette)
            {
                // the ring sits on its own color, and reads it back
                var stripe = Array.IndexOf(Stripes, sample.Color);
                Assert.InRange(sample.X, stripe / 5d, (stripe + 1) / 5d);
                Assert.Equal(sample.Color, ImagePalette.Sample(image, sample.X, sample.Y).Color);
            }
        }

        [Fact]
        public void Extract_IsLightestFirst()
        {
            // one hue at five clearly different lightnesses, in no order
            var shades = new[] { 0.5, 0.95, 0.2, 0.7, 0.35 }.Select(v => new HsvColor(200, 0.8, v).ToColor()).ToArray();
            var palette = ImagePalette.Extract(Paint(250, 100, (x, _) => shades[x / 50]), PaletteMood.Colorful);
            var lightness = palette.Select(s => Lightness(s.Color)).ToArray();
            Assert.Equal(lightness.OrderByDescending(l => l), lightness);
        }

        [Fact]
        public void Extract_IsDeterministic()
        {
            var image = Paint(200, 150, (x, y) => new HsvColor(x * 1.8, 0.3 + y / 300d, 0.4 + (x + y) / 700d).ToColor());
            foreach (var mood in Enum.GetValues<PaletteMood>())
                Assert.Equal(ImagePalette.Extract(image, mood), ImagePalette.Extract(image, mood));
        }

        [Fact]
        public void Extract_ASolidImageRepeatsItsColorAtDifferentSpots()
        {
            var teal = Color.Parse("#008080");
            var palette = ImagePalette.Extract(Paint(64, 48, (_, _) => teal), PaletteMood.Bright);

            Assert.Equal(ImagePalette.ColorCount, palette.Length);
            Assert.All(palette, s => Assert.Equal(teal, s.Color));
            Assert.Equal(palette.Length, palette.Select(s => (s.X, s.Y)).Distinct().Count());
        }

        [Fact]
        public void Extract_IgnoresTransparentPixels()
        {
            var image = Paint(100, 100, (x, _) => x < 50 ? Color.FromArgb(0, 0, 255, 0) : Color.Parse("#E5484D"));
            var palette = ImagePalette.Extract(image, PaletteMood.Colorful);
            Assert.All(palette, s => Assert.Equal(Color.Parse("#E5484D"), s.Color));
            Assert.All(palette, s => Assert.True(s.X >= 0.5));
        }

        [Fact]
        public void Extract_MoodsLeanTheirWay()
        {
            // every hue, from deep shadow to near white, at full and at low saturation
            var image = Paint(240, 160, (x, y) => new HsvColor(x * 1.5, y % 40 < 20 ? 0.9 : 0.15, 0.1 + 0.9 * (y / 159d)).ToColor());

            double MeanLightness(PaletteMood mood) => ImagePalette.Extract(image, mood).Average(s => Lightness(s.Color));
            double MeanChroma(PaletteMood mood) => ImagePalette.Extract(image, mood).Average(s => Chroma(s.Color));

            Assert.True(MeanLightness(PaletteMood.Bright) > MeanLightness(PaletteMood.Colorful));
            Assert.True(MeanLightness(PaletteMood.Colorful) > MeanLightness(PaletteMood.Deep));
            Assert.True(MeanLightness(PaletteMood.Deep) > MeanLightness(PaletteMood.Dark));
            Assert.True(MeanChroma(PaletteMood.Muted) < MeanChroma(PaletteMood.Colorful));
        }

        [Fact]
        public void Extract_KeepsTheFiveColorsApart()
        {
            // a gradient sky over a dark ground: no two picks should be near twins
            var image = Paint(260, 220, (x, y) => y < 150
                ? new HsvColor(250 - y * 1.4, 0.55, 0.45 + y / 300d).ToColor()
                : Color.FromRgb(0x1B, 0x26, 0x3B));
            var colors = ImagePalette.Extract(image, PaletteMood.Colorful).Select(s => s.Color).ToArray();

            for (var i = 0; i < colors.Length; i++)
            {
                for (var j = i + 1; j < colors.Length; j++)
                {
                    var d = Math.Abs(colors[i].R - colors[j].R) + Math.Abs(colors[i].G - colors[j].G) + Math.Abs(colors[i].B - colors[j].B);
                    Assert.True(d > 24, $"{colors[i]} and {colors[j]} are near twins");
                }
            }
        }
    }
}

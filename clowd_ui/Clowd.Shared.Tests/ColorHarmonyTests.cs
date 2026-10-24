using System;
using System.Linq;
using Avalonia.Media;
using Xunit;

namespace Clowd.Shared.Tests
{
    public class ColorHarmonyTests
    {
        private static double HueDistance(double a, double b)
        {
            var d = Math.Abs(a - b) % 360;
            return d > 180 ? 360 - d : d;
        }

        [Theory]
        [InlineData(0x00, 0xAF, 0xF0)]
        [InlineData(0xE5, 0x48, 0x4D)]
        [InlineData(0xFF, 0xFF, 0xFF)]
        [InlineData(0x00, 0x00, 0x00)]
        [InlineData(0x7E, 0x4A, 0x6A)]
        public void Hsv_RoundTripsThroughRgb(byte r, byte g, byte b)
        {
            var color = Color.FromRgb(r, g, b);
            Assert.Equal(color, HsvColor.FromColor(color).ToColor());
        }

        [Fact]
        public void Hsv_NormalizesHueAndClampsChannels()
        {
            var n = new HsvColor(-30, 1.4, -0.2).Normalized();
            Assert.Equal(330, n.Hue, 6);
            Assert.Equal(1, n.Saturation);
            Assert.Equal(0, n.Value);
        }

        [Fact]
        public void Presets_AreInGridOrder()
        {
            Assert.Equal(Enum.GetValues<HarmonyPreset>(), ColorHarmony.Presets);
            Assert.Equal("Split complementary", ColorHarmony.DisplayName(HarmonyPreset.SplitComplementary));
        }

        [Fact]
        public void Generate_KeepsTheBaseInTheMiddle()
        {
            var baseColor = new HsvColor(200, 0.8, 0.94);
            foreach (var preset in ColorHarmony.Presets)
            {
                var colors = ColorHarmony.Generate(preset, baseColor);
                Assert.Equal(ColorHarmony.ColorCount, colors.Length);
                Assert.Equal(baseColor, colors[ColorHarmony.BaseIndex]);
            }
        }

        [Theory]
        [InlineData(HarmonyPreset.Triad, new double[] { 120, 240 })]
        [InlineData(HarmonyPreset.Complementary, new double[] { 180 })]
        [InlineData(HarmonyPreset.SplitComplementary, new double[] { 150, 210 })]
        [InlineData(HarmonyPreset.Square, new double[] { 90, 180, 270 })]
        public void Generate_UsesTheDefiningHueAngles(HarmonyPreset preset, double[] offsets)
        {
            var colors = ColorHarmony.Generate(preset, new HsvColor(200, 0.8, 0.94));
            foreach (var offset in offsets)
                Assert.Contains(colors, c => HueDistance(c.Hue, 200 + offset) < 1e-9);
        }

        [Theory]
        [InlineData(HarmonyPreset.Monochromatic)]
        [InlineData(HarmonyPreset.Shades)]
        public void Generate_SingleHuePresetsStayOnOneHue(HarmonyPreset preset)
        {
            Assert.All(ColorHarmony.Generate(preset, new HsvColor(37, 0.9, 0.9)), c => Assert.Equal(37, c.Hue, 9));
        }

        [Fact]
        public void Generate_ShadesOnlyDarken()
        {
            var colors = ColorHarmony.Generate(HarmonyPreset.Shades, new HsvColor(37, 0.9, 0.9));
            Assert.All(colors, c => Assert.Equal(0.9, c.Saturation, 9));
            Assert.All(colors, c => Assert.True(c.Value <= 0.9 + 1e-9));
        }

        [Fact]
        public void Move_KeepsTheRelationshipAndLandsTheMovedMember()
        {
            var baseColor = new HsvColor(200, 0.6, 0.7);
            foreach (var preset in ColorHarmony.Presets)
            {
                var before = ColorHarmony.Generate(preset, baseColor);
                for (var index = 0; index < ColorHarmony.ColorCount; index++)
                {
                    // rotate the member 25° and nudge it: small enough that no factor clamps
                    var target = new HsvColor(before[index].Hue + 25, before[index].Saturation * 0.9, before[index].Value * 0.95);
                    var after = ColorHarmony.Move(preset, index, target);

                    Assert.True(HueDistance(after[index].Hue, target.Normalized().Hue) < 1e-6, $"{preset} member {index} hue");
                    Assert.Equal(target.Saturation, after[index].Saturation, 6);
                    Assert.Equal(target.Value, after[index].Value, 6);

                    // the whole set turned with it: every member is the same distance from the base
                    for (var j = 0; j < ColorHarmony.ColorCount; j++)
                    {
                        var turned = HueDistance(after[j].Hue, before[j].Hue + 25);
                        Assert.True(turned < 1e-6, $"{preset} member {j} did not follow member {index}");
                    }
                }
            }
        }

        [Fact]
        public void Move_ClampsInsteadOfBreakingTheRelationship()
        {
            // Monochromatic's first member is the base at 35% saturation, so it can never be more
            // saturated than 0.35: dragging it to full saturation tops the base out instead
            var after = ColorHarmony.Move(HarmonyPreset.Monochromatic, 0, new HsvColor(10, 1, 1));
            Assert.Equal(1, after[ColorHarmony.BaseIndex].Saturation, 9);
            Assert.Equal(0.35, after[0].Saturation, 9);
        }

        [Fact]
        public void BaseFor_InvertsGenerate()
        {
            var baseColor = new HsvColor(311, 0.5, 0.6);
            foreach (var preset in ColorHarmony.Presets)
            {
                var colors = ColorHarmony.Generate(preset, baseColor);
                for (var i = 0; i < ColorHarmony.ColorCount; i++)
                {
                    var recovered = ColorHarmony.BaseFor(preset, i, colors[i]);
                    Assert.True(HueDistance(recovered.Hue, baseColor.Hue) < 1e-6);
                    Assert.Equal(baseColor.Saturation, recovered.Saturation, 6);
                    Assert.Equal(baseColor.Value, recovered.Value, 6);
                }
            }
        }

        [Theory]
        [InlineData(HarmonyPreset.Analogous, 30)]
        [InlineData(HarmonyPreset.Triad, 200)]
        [InlineData(HarmonyPreset.Complementary, 350)]
        [InlineData(HarmonyPreset.SplitComplementary, 120)]
        [InlineData(HarmonyPreset.Square, 75)]
        [InlineData(HarmonyPreset.Compound, 260)]
        [InlineData(HarmonyPreset.Shades, 15)]
        [InlineData(HarmonyPreset.Monochromatic, 160)]
        public void FitClosest_RecoversAnExactPalette(HarmonyPreset preset, double hue)
        {
            var baseColor = new HsvColor(hue, 0.8, 0.9);

            // shuffled: the image tab's colors come in no particular order
            var generated = ColorHarmony.Generate(preset, baseColor);
            var shuffled = new[] { 3, 0, 4, 2, 1 }.Select(i => generated[i].ToColor()).ToArray();

            var fit = ColorHarmony.FitClosest(shuffled);

            Assert.Equal(preset, fit.Preset);
            Assert.True(fit.Error < 1, $"error {fit.Error}");
            Assert.True(HueDistance(fit.Base.Hue, hue) < 1.5, $"base hue {fit.Base.Hue}");
            Assert.Equal(new[] { 3, 0, 4, 2, 1 }, fit.MemberOf);
            Assert.Equal(fit.Colors, ColorHarmony.Generate(fit.Preset, fit.Base));
        }

        [Fact]
        public void FitClosest_ToleratesNoise()
        {
            // the mockup's sunset photo: warm, mostly analogous, with dark blues
            var colors = new[] { "#F8D78B", "#E76F51", "#7E4A6A", "#3D405B", "#1B263B" }
                .Select(Color.Parse).ToArray();

            var fit = ColorHarmony.FitClosest(colors);

            Assert.Equal(ColorHarmony.ColorCount, fit.Colors.Length);
            Assert.Equal(new[] { 0, 1, 2, 3, 4 }, fit.MemberOf.OrderBy(m => m));
            Assert.True(double.IsFinite(fit.Error));
        }

        [Fact]
        public void FitClosest_RequiresFiveColors()
        {
            Assert.Throws<ArgumentException>(() => ColorHarmony.FitClosest(new[] { Colors.Red }));
        }

        [Fact]
        public void Glyph_StartsWithTheBaseAndUsesTheBaseHue()
        {
            foreach (var preset in ColorHarmony.Presets)
            {
                var glyph = ColorHarmony.Glyph(preset);
                Assert.NotEmpty(glyph);
                Assert.Contains(glyph, d => d.HueOffset == 0);
            }

            var dot = ColorHarmony.Glyph(HarmonyPreset.Complementary)[1];
            Assert.Equal(new HsvColor(20, 0.8, 0.94).ToColor(), dot.ColorFor(200));
        }
    }
}

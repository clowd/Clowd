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

        /// <summary>Member <paramref name="color"/>'s hue offset from <paramref name="baseColor"/>,
        /// round the artistic wheel, in [0, 360).</summary>
        private static double ArtisticOffset(HsvColor baseColor, HsvColor color) =>
            ((ColorHarmony.ToArtistic(color.Hue) - ColorHarmony.ToArtistic(baseColor.Hue)) % 360 + 360) % 360;

        private static void AssertSameColor(Color expected, Color actual, int tolerance, string message)
        {
            var off = Math.Max(Math.Abs(expected.R - actual.R), Math.Max(Math.Abs(expected.G - actual.G), Math.Abs(expected.B - actual.B)));
            Assert.True(off <= tolerance, $"{message}: expected {expected}, got {actual}");
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

        [Theory]
        [InlineData(0, 0)]
        [InlineData(35, 60)] // orange
        [InlineData(60, 122)] // yellow
        [InlineData(120, 165)] // green
        [InlineData(240, 275)] // blue
        [InlineData(300, 330)] // magenta
        [InlineData(15, 15 * 60 / 35d)]
        public void Artistic_MapsTheKeyHues(double hue, double artistic)
        {
            Assert.Equal(artistic, ColorHarmony.ToArtistic(hue), 9);
            Assert.Equal(hue, ColorHarmony.FromArtistic(artistic), 9);
        }

        [Fact]
        public void Artistic_RoundTripsAndWraps()
        {
            for (double hue = 0; hue < 360; hue += 7.3)
                Assert.True(HueDistance(hue, ColorHarmony.FromArtistic(ColorHarmony.ToArtistic(hue))) < 1e-9, $"hue {hue}");

            Assert.Equal(ColorHarmony.ToArtistic(10), ColorHarmony.ToArtistic(370), 9);
            Assert.Equal(ColorHarmony.FromArtistic(350), ColorHarmony.FromArtistic(-10), 9);
        }

        /// <summary>
        /// The benchmark: every preset against Adobe Color's color wheel, five swatches with the base
        /// in the middle, for a spread of bases (saturated, pastel, dark, near-gray, black, white).
        /// Adobe takes the base in whole degrees and percent, so the base is rounded the same way;
        /// what is left is 8-bit rounding, hence the one-step tolerance.
        /// </summary>
        [Theory]
        [InlineData(HarmonyPreset.Analogous, "#FF8075 #FFB075 #FE9674 #FF75A5 #FFC775")]
        [InlineData(HarmonyPreset.Analogous, "#FF0099 #FF2D00 #FF0000 #E300FF #FF5900")]
        [InlineData(HarmonyPreset.Analogous, "#00F0E3 #0064F0 #00AFF0 #00F092 #0018F0")]
        [InlineData(HarmonyPreset.Analogous, "#E647AB #E66147 #E5484D #D147E6 #E67D47")]
        [InlineData(HarmonyPreset.Analogous, "#774A7D #7D4A4B #7E4A6A #664A7D #7D524A")]
        [InlineData(HarmonyPreset.Analogous, "#3E4A5C #443E5C #3D405B #3E545C #4E3E5C")]
        [InlineData(HarmonyPreset.Analogous, "#F7C98B #F7E48B #F8D78B #F7B68B #F7F18B")]
        [InlineData(HarmonyPreset.Analogous, "#1B303B #1B1C3B #1B263B #1B3A3B #241B3B")]
        [InlineData(HarmonyPreset.Analogous, "#34CC33 #33CC9A #33CC66 #74CC33 #33CACC")]
        [InlineData(HarmonyPreset.Analogous, "#FFE000 #94FF00 #FFFF00 #FFC100 #2AFF00")]
        [InlineData(HarmonyPreset.Analogous, "#2C00FF #D300FF #8000FF #0026FF #FF00B7")]
        [InlineData(HarmonyPreset.Analogous, "#0A0A0A #0A0A0A #0A0A0A #0A0A0A #0A0A0A")]
        [InlineData(HarmonyPreset.Analogous, "#F2F2F2 #F2F2F2 #F2F2F2 #F2F2F2 #F2F2F2")]
        [InlineData(HarmonyPreset.Analogous, "#808080 #808080 #808080 #808080 #808080")]
        [InlineData(HarmonyPreset.Analogous, "#4A7D2E #2E7D4D #2E7D32 #6B7D2E #2E7D68")]
        [InlineData(HarmonyPreset.Analogous, "#FFBFF0 #FFC7BF #FFC0CB #F2BFFF #FFD2BF")]
        [InlineData(HarmonyPreset.Monochromatic, "#CC9887 #998984 #FE9674 #66524B #33211C")]
        [InlineData(HarmonyPreset.Monochromatic, "#CC2929 #993D3D #FF0000 #663D3D #332929")]
        [InlineData(HarmonyPreset.Monochromatic, "#2694BD #37748A #00AFF0 #344D57 #293033")]
        [InlineData(HarmonyPreset.Monochromatic, "#B35B5E #805B5C #E5484D #4C4444 #332324")]
        [InlineData(HarmonyPreset.Monochromatic, "#4A3A44 #E3B8D2 #7E4A6A #FF9CD9 #FF69C5")]
        [InlineData(HarmonyPreset.Monochromatic, "#7C7E8F #8D93C2 #3D405B #828DF5 #5465FF")]
        [InlineData(HarmonyPreset.Monochromatic, "#C4B695 #918A7A #F8D78B #5E543C #332A16")]
        [InlineData(HarmonyPreset.Monochromatic, "#48556E #8A92A1 #1B263B #9DB0D4 #8AB3FF")]
        [InlineData(HarmonyPreset.Monochromatic, "#459961 #42664E #33CC66 #2B332E #26332B")]
        [InlineData(HarmonyPreset.Monochromatic, "#CCCC29 #99993D #FFFF00 #66663D #333329")]
        [InlineData(HarmonyPreset.Monochromatic, "#7A29CC #6B3D99 #8000FF #523D66 #2E2933")]
        [InlineData(HarmonyPreset.Monochromatic, "#3D2525 #702D2D #0A0A0A #A32121 #D60000")]
        [InlineData(HarmonyPreset.Monochromatic, "#BF7373 #8C3838 #F2F2F2 #591212 #330000")]
        [InlineData(HarmonyPreset.Monochromatic, "#4D2E2E #E65C5C #808080 #FF3333 #FF0000")]
        [InlineData(HarmonyPreset.Monochromatic, "#2A4A2C #AFE3B1 #2E7D32 #D4FFD6 #A1FFA5")]
        [InlineData(HarmonyPreset.Monochromatic, "#CCADB3 #99636C #FFC0CB #662E37 #330D13")]
        [InlineData(HarmonyPreset.Triad, "#758CFF #B3FF75 #FE9674 #AA9087 #6B6E80")]
        [InlineData(HarmonyPreset.Triad, "#009DFF #FFFC00 #FF0000 #AA3939 #406780")]
        [InlineData(HarmonyPreset.Triad, "#F0E200 #F00036 #00AFF0 #347F9B #706D38")]
        [InlineData(HarmonyPreset.Triad, "#47ABE6 #E6E247 #E5484D #915D5F #535F66")]
        [InlineData(HarmonyPreset.Triad, "#4A7A7D #7D764A #7E4A6A #D2B8C8 #B3F8FC")]
        [InlineData(HarmonyPreset.Triad, "#495C3E #5C463E #3D405B #8D90B1 #A8DB8A")]
        [InlineData(HarmonyPreset.Triad, "#DE8BF7 #8BF7CD #F8D78B #A29D91 #715978")]
        [InlineData(HarmonyPreset.Triad, "#313B1B #3B201B #1B263B #727C90 #B1BA9C")]
        [InlineData(HarmonyPreset.Triad, "#CC8F33 #8033CC #33CC66 #457756 #4D4539")]
        [InlineData(HarmonyPreset.Triad, "#FF0500 #0094FF #FFFF00 #AAAA39 #804140")]
        [InlineData(HarmonyPreset.Triad, "#00FF54 #FF9900 #8000FF #7139AA #408055")]
        [InlineData(HarmonyPreset.Triad, "#0A0A0A #0A0A0A #0A0A0A #5F2C2C #29648A")]
        [InlineData(HarmonyPreset.Triad, "#F2F2F2 #F2F2F2 #F2F2F2 #9D4949 #225473")]
        [InlineData(HarmonyPreset.Triad, "#808080 #808080 #808080 #D46363 #4DBAFF")]
        [InlineData(HarmonyPreset.Triad, "#7D532E #402E7D #2E7D32 #94D297 #FCEBDC")]
        [InlineData(HarmonyPreset.Triad, "#BFECFF #FFFCBF #FFC0CB #AA7A82 #466E80")]
        [InlineData(HarmonyPreset.Complementary, "#75FFDF #BF9688 #FE9674 #87AAA2 #80706B")]
        [InlineData(HarmonyPreset.Complementary, "#00FF48 #BF3030 #FF0000 #39AA59 #804040")]
        [InlineData(HarmonyPreset.Complementary, "#F07C00 #2C8DB0 #00AFF0 #9B6934 #386170")]
        [InlineData(HarmonyPreset.Complementary, "#47E671 #A65D5F #E5484D #5D916B #665353")]
        [InlineData(HarmonyPreset.Complementary, "#4E7D4A #3D3339 #7E4A6A #BAD2B8 #FCB3E0")]
        [InlineData(HarmonyPreset.Complementary, "#5C553E #898B9C #3D405B #B1A98D #8A92DB")]
        [InlineData(HarmonyPreset.Complementary, "#8BACF7 #B8AD95 #F8D78B #9196A2 #786F59")]
        [InlineData(HarmonyPreset.Complementary, "#3B311B #57637A #1B263B #908672 #9CA7BA")]
        [InlineData(HarmonyPreset.Complementary, "#CC3733 #468C5E #33CC66 #774745 #394D40")]
        [InlineData(HarmonyPreset.Complementary, "#7D00FF #BFBF30 #FFFF00 #7039AA #808040")]
        [InlineData(HarmonyPreset.Complementary, "#FCFF00 #7830BF #8000FF #A9AA39 #604080")]
        [InlineData(HarmonyPreset.Complementary, "#0A0A0A #4A2929 #0A0A0A #2C5F3B #8A2929")]
        [InlineData(HarmonyPreset.Complementary, "#F2F2F2 #B36262 #F2F2F2 #499D61 #732222")]
        [InlineData(HarmonyPreset.Complementary, "#808080 #402323 #808080 #63D483 #FF4D4D")]
        [InlineData(HarmonyPreset.Complementary, "#7D2E4F #263D27 #2E7D32 #D294AD #DCFCDD")]
        [InlineData(HarmonyPreset.Complementary, "#BFFFCB #BF999F #FFC0CB #7AAA83 #804650")]
        [InlineData(HarmonyPreset.SplitComplementary, "#75F1FF #75FFB0 #FE9674 #80706B #6B7D80")]
        [InlineData(HarmonyPreset.SplitComplementary, "#00FF9F #12FF00 #FF0000 #804040 #408067")]
        [InlineData(HarmonyPreset.SplitComplementary, "#F09E00 #F05200 #00AFF0 #386170 #705D38")]
        [InlineData(HarmonyPreset.SplitComplementary, "#47E6A7 #56E647 #E5484D #665353 #53665E")]
        [InlineData(HarmonyPreset.SplitComplementary, "#4A7D58 #637D4A #7E4A6A #FCB3E0 #B3FCC7")]
        [InlineData(HarmonyPreset.SplitComplementary, "#5C593E #5C513E #3D405B #8A92DB #DBD38A")]
        [InlineData(HarmonyPreset.SplitComplementary, "#8B8BF7 #8BCFF7 #F8D78B #786F59 #595978")]
        [InlineData(HarmonyPreset.SplitComplementary, "#3B341B #3B2D1B #1B263B #9CA7BA #BAB49C")]
        [InlineData(HarmonyPreset.SplitComplementary, "#CC5233 #CC3381 #33CC66 #394D40 #4D3D39")]
        [InlineData(HarmonyPreset.SplitComplementary, "#D100FF #2A00FF #FFFF00 #808040 #744080")]
        [InlineData(HarmonyPreset.SplitComplementary, "#91FF00 #FFE100 #8000FF #604080 #648040")]
        [InlineData(HarmonyPreset.SplitComplementary, "#0A0A0A #0A0A0A #0A0A0A #8A2929 #298A65")]
        [InlineData(HarmonyPreset.SplitComplementary, "#F2F2F2 #F2F2F2 #F2F2F2 #732222 #227354")]
        [InlineData(HarmonyPreset.SplitComplementary, "#808080 #808080 #808080 #FF4D4D #4DFFBC")]
        [InlineData(HarmonyPreset.SplitComplementary, "#7D332E #7C2E7D #2E7D32 #DCFCDD #FCDDDC")]
        [InlineData(HarmonyPreset.SplitComplementary, "#BFFFE1 #CBFFBF #FFC0CB #804650 #468064")]
        [InlineData(HarmonyPreset.Square, "#A975FF #75FFDF #FE9674 #FFF975 #AA9087")]
        [InlineData(HarmonyPreset.Square, "#0016FF #00FF48 #FF0000 #FFC800 #AA3939")]
        [InlineData(HarmonyPreset.Square, "#7AF000 #F07C00 #00AFF0 #D200F0 #347F9B")]
        [InlineData(HarmonyPreset.Square, "#4758E6 #47E671 #E5484D #E6C247 #915D5F")]
        [InlineData(HarmonyPreset.Square, "#4A5F7D #4E7D4A #7E4A6A #7D6C4A #D2B8C8")]
        [InlineData(HarmonyPreset.Square, "#3E5C46 #5C553E #3D405B #5C3E3E #8D90B1")]
        [InlineData(HarmonyPreset.Square, "#F78BB8 #8BACF7 #F8D78B #8BF78F #A29D91")]
        [InlineData(HarmonyPreset.Square, "#1B3B1B #3B311B #1B263B #3B1B2B #727C90")]
        [InlineData(HarmonyPreset.Square, "#CCAE33 #CC3733 #33CC66 #3339CC #457756")]
        [InlineData(HarmonyPreset.Square, "#FF4F00 #7D00FF #FFFF00 #00FFE2 #AAAA39")]
        [InlineData(HarmonyPreset.Square, "#00FFE5 #FCFF00 #8000FF #FF5100 #7139AA")]
        [InlineData(HarmonyPreset.Square, "#0A0A0A #0A0A0A #0A0A0A #0A0A0A #5F2C2C")]
        [InlineData(HarmonyPreset.Square, "#F2F2F2 #F2F2F2 #F2F2F2 #F2F2F2 #9D4949")]
        [InlineData(HarmonyPreset.Square, "#808080 #808080 #808080 #808080 #D46363")]
        [InlineData(HarmonyPreset.Square, "#7D652E #7D2E4F #2E7D32 #2E467D #94D297")]
        [InlineData(HarmonyPreset.Square, "#BFCAFF #BFFFCB #FFC0CB #FFEFBF #AA7A82")]
        [InlineData(HarmonyPreset.Compound, "#FF7589 #75D4FF #FE9674 #75FFDF #AA9087")]
        [InlineData(HarmonyPreset.Compound, "#FF00FF #00FFD9 #FF0000 #00FF48 #AA3939")]
        [InlineData(HarmonyPreset.Compound, "#00F0AD #F0B100 #00AFF0 #F07C00 #347F9B")]
        [InlineData(HarmonyPreset.Compound, "#E347E6 #47E6CB #E5484D #47E671 #915D5F")]
        [InlineData(HarmonyPreset.Compound, "#6C4A7D #4A7D63 #7E4A6A #4E7D4A #D2B8C8")]
        [InlineData(HarmonyPreset.Compound, "#3E505C #5C5B3E #3D405B #5C553E #8D90B1")]
        [InlineData(HarmonyPreset.Compound, "#F7BD8B #A38BF7 #F8D78B #8BACF7 #A29D91")]
        [InlineData(HarmonyPreset.Compound, "#1B373B #3B371B #1B263B #3B311B #727C90")]
        [InlineData(HarmonyPreset.Compound, "#5FCC33 #CC6433 #33CC66 #CC3733 #457756")]
        [InlineData(HarmonyPreset.Compound, "#FFCC00 #FF00EE #FFFF00 #7D00FF #AAAA39")]
        [InlineData(HarmonyPreset.Compound, "#000BFF #4AFF00 #8000FF #FCFF00 #7139AA")]
        [InlineData(HarmonyPreset.Compound, "#0A0A0A #0A0A0A #0A0A0A #0A0A0A #5F2C2C")]
        [InlineData(HarmonyPreset.Compound, "#F2F2F2 #F2F2F2 #F2F2F2 #F2F2F2 #9D4949")]
        [InlineData(HarmonyPreset.Compound, "#808080 #808080 #808080 #808080 #D46363")]
        [InlineData(HarmonyPreset.Compound, "#607D2E #7D3C2E #2E7D32 #7D2E4F #94D297")]
        [InlineData(HarmonyPreset.Compound, "#F9BFFF #BFFFEF #FFC0CB #BFFFCB #AA7A82")]
        [InlineData(HarmonyPreset.Shades, "#DB8265 #B36A52 #FE9674 #8A523F #613A2D")]
        [InlineData(HarmonyPreset.Shades, "#DB0000 #B30000 #FF0000 #8A0000 #610000")]
        [InlineData(HarmonyPreset.Shades, "#0096CC #0078A3 #00AFF0 #005A7A #003C52")]
        [InlineData(HarmonyPreset.Shades, "#C23C41 #992F33 #E5484D #702325 #471618")]
        [InlineData(HarmonyPreset.Shades, "#59354B #FC95D5 #7E4A6A #D47DB2 #AB6590")]
        [InlineData(HarmonyPreset.Shades, "#262738 #939ADB #3D405B #787DB3 #5C618A")]
        [InlineData(HarmonyPreset.Shades, "#D4B877 #AB9460 #F8D78B #827149 #594D32")]
        [InlineData(HarmonyPreset.Shades, "#6893E3 #5679BA #1B263B #435E91 #304469")]
        [InlineData(HarmonyPreset.Shades, "#2AA854 #208040 #33CC66 #16572B #3EFA7D")]
        [InlineData(HarmonyPreset.Shades, "#DBDB00 #B3B300 #FFFF00 #8A8A00 #616100")]
        [InlineData(HarmonyPreset.Shades, "#6E00DB #5900B3 #8000FF #45008A #300061")]
        [InlineData(HarmonyPreset.Shades, "#B3B3B3 #8A8A8A #0A0A0A #616161 #383838")]
        [InlineData(HarmonyPreset.Shades, "#CFCFCF #A6A6A6 #F2F2F2 #7D7D7D #545454")]
        [InlineData(HarmonyPreset.Shades, "#5C5C5C #FFFFFF #808080 #D6D6D6 #ADADAD")]
        [InlineData(HarmonyPreset.Shades, "#215924 #5DFC65 #2E7D32 #4ED455 #3FAB45")]
        [InlineData(HarmonyPreset.Shades, "#DBA4AE #B3868D #FFC0CB #8A676D #61494D")]
        public void Generate_MatchesAdobeColor(HarmonyPreset preset, string adobe)
        {
            var expected = adobe.Split(' ').Select(Color.Parse).ToArray();
            var b = HsvColor.FromColor(expected[ColorHarmony.BaseIndex]);
            var baseColor = new HsvColor(Math.Round(b.Hue), Math.Round(b.Saturation * 100) / 100, Math.Round(b.Value * 100) / 100);

            var colors = ColorHarmony.Generate(preset, baseColor);

            for (var i = 0; i < ColorHarmony.ColorCount; i++)
            {
                if (i != ColorHarmony.BaseIndex)
                    AssertSameColor(expected[i], colors[i].ToColor(), 1, $"{preset} member {i}");
            }
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
        [InlineData(HarmonyPreset.SplitComplementary, new double[] { 162, 198 })]
        [InlineData(HarmonyPreset.Square, new double[] { 90, 180, 270 })]
        [InlineData(HarmonyPreset.Compound, new double[] { 330, 210, 180 })]
        public void Generate_UsesTheDefiningArtisticAngles(HarmonyPreset preset, double[] offsets)
        {
            var baseColor = new HsvColor(200, 0.8, 0.94);
            var colors = ColorHarmony.Generate(preset, baseColor);
            foreach (var offset in offsets)
                Assert.Contains(colors, c => HueDistance(ArtisticOffset(baseColor, c), offset) < 1e-6);
        }

        [Theory]
        [InlineData(HarmonyPreset.Monochromatic)]
        [InlineData(HarmonyPreset.Shades)]
        public void Generate_SingleHuePresetsStayOnOneHue(HarmonyPreset preset)
        {
            Assert.All(ColorHarmony.Generate(preset, new HsvColor(37, 0.9, 0.9)), c => Assert.Equal(37, c.Hue, 9));
        }

        [Fact]
        public void ToneChannel_ReflectsOffTheFloor()
        {
            var channel = new ToneChannel(-0.5, 0.1, ToneOverflow.Reflect);
            Assert.Equal(0.3, channel.Apply(0.8), 9);
            Assert.Equal(0.16, channel.Apply(0.54), 9); // 0.04 is under the floor: 0.06 back above it
        }

        [Fact]
        public void ToneChannel_NegatesPastTheFloor()
        {
            var channel = new ToneChannel(-0.5, 0.2, ToneOverflow.Negate);
            Assert.Equal(0.5, channel.Apply(1), 9);
            Assert.Equal(0.8, channel.Apply(0.3), 9); // too dark to go darker: lighter instead
        }

        [Fact]
        public void ToneChannel_WrapsPastWhite()
        {
            var channel = new ToneChannel(0.66, 0.2, ToneOverflow.Wrap);
            Assert.Equal(0.86, channel.Apply(1), 9);
            Assert.Equal(0.86, channel.Apply(0.2), 9);
        }

        [Fact]
        public void ToneChannel_HoldsARaisedMemberAboveTheFloor()
        {
            Assert.Equal(0.2, new ToneChannel(0.1, 0.2, ToneOverflow.Negate).Apply(0.05), 9);
        }

        [Theory]
        [InlineData(0.8, 0.3)]
        [InlineData(0.8, 0.05)] // under the floor: the floor gives way
        [InlineData(0.3, 0.95)]
        [InlineData(0.5, 0.5)]
        public void ToneChannel_ThroughLandsExactly(double baseLevel, double level)
        {
            foreach (var overflow in Enum.GetValues<ToneOverflow>())
            {
                var channel = new ToneChannel(-0.5, 0.2, overflow).Through(baseLevel, level);
                Assert.Equal(level, channel.Apply(baseLevel), 9);
            }
        }

        [Fact]
        public void Palette_BaseMoveTakesEveryMemberWithIt()
        {
            var palette = new HarmonyPalette(HarmonyPreset.Triad, new HsvColor(200, 0.6, 0.7));
            var moved = palette.WithMember(ColorHarmony.BaseIndex, new HsvColor(40, 0.9, 0.9));

            Assert.Equal(new HsvColor(40, 0.9, 0.9), moved.Base);
            Assert.Equal(ColorHarmony.Generate(HarmonyPreset.Triad, moved.Base), moved.Colors);
        }

        [Fact]
        public void Palette_MemberMoveLandsAndKeepsTheHueRelationship()
        {
            var baseColor = new HsvColor(200, 0.6, 0.7);
            foreach (var preset in ColorHarmony.Presets)
            {
                var before = new HarmonyPalette(preset, baseColor);
                for (var index = 0; index < ColorHarmony.ColorCount; index++)
                {
                    var target = new HsvColor(before.Colors[index].Hue + 25, 0.45, 0.85).Normalized();
                    var after = before.WithMember(index, target);

                    Assert.True(HueDistance(after.Colors[index].Hue, target.Hue) < 1e-6, $"{preset} member {index} hue");
                    Assert.Equal(target.Saturation, after.Colors[index].Saturation, 6);
                    Assert.Equal(target.Value, after.Colors[index].Value, 6);

                    // every member kept its angle from the base
                    for (var j = 0; j < ColorHarmony.ColorCount; j++)
                    {
                        var was = ArtisticOffset(before.Base, before.Colors[j]);
                        var now = ArtisticOffset(after.Base, after.Colors[j]);
                        Assert.True(HueDistance(was, now) < 1e-6, $"{preset} member {j} lost its angle when member {index} moved");
                    }
                }
            }
        }

        [Fact]
        public void Palette_MemberToneIsItsOwn()
        {
            var before = new HarmonyPalette(HarmonyPreset.SplitComplementary, new HsvColor(15, 0.54, 1));
            var after = before.WithMember(0, before.Colors[0] with { Value = 0.4 });

            Assert.Equal(0.4, after.Colors[0].Value, 9);
            Assert.Equal(before.Base, after.Base);
            for (var j = 1; j < ColorHarmony.ColorCount; j++)
                Assert.Equal(before.Colors[j], after.Colors[j]);

            // and it follows the base by its new offset from then on
            var dimmed = after.WithBase(after.Base with { Value = 0.9 });
            Assert.Equal(0.3, dimmed.Colors[0].Value, 9);
        }

        [Fact]
        public void Palette_PresetSwitchStartsTheTonesOver()
        {
            var tuned = new HarmonyPalette(HarmonyPreset.Triad, new HsvColor(200, 0.6, 0.7));
            tuned = tuned.WithMember(0, tuned.Colors[0] with { Saturation = 0.2 });

            var switched = tuned.WithPreset(HarmonyPreset.Square);

            Assert.Equal(tuned.Base, switched.Base);
            Assert.Equal(ColorHarmony.Generate(HarmonyPreset.Square, tuned.Base), switched.Colors);
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
            var source = new HarmonyPalette(preset, new HsvColor(hue, 0.8, 0.9));
            // a member retuned by hand is still the same harmony
            source = source.WithMember(4, source.Colors[4] with { Value = 0.5 });

            // shuffled: the image tab's colors come in no particular order
            var shuffled = new[] { 3, 0, 4, 2, 1 }.Select(i => source.Colors[i].ToColor()).ToArray();

            var fit = ColorHarmony.FitClosest(shuffled);

            // Monochromatic and Shades both keep one hue, and with tones free they explain the
            // same palettes equally well
            var sameHue = new[] { HarmonyPreset.Monochromatic, HarmonyPreset.Shades };
            Assert.True(fit.Palette.Preset == preset || (sameHue.Contains(preset) && sameHue.Contains(fit.Palette.Preset)), $"fit {fit.Palette.Preset}");
            Assert.True(fit.Error < 1, $"error {fit.Error}");
            for (var i = 0; i < ColorHarmony.ColorCount; i++)
                AssertSameColor(shuffled[i], fit.Palette.Colors[fit.MemberOf[i]].ToColor(), 1, $"input {i}");
        }

        [Fact]
        public void FitClosest_ToleratesNoise()
        {
            // the mockup's sunset photo: warm, mostly analogous, with dark blues
            var colors = new[] { "#F8D78B", "#E76F51", "#7E4A6A", "#3D405B", "#1B263B" }
                .Select(Color.Parse).ToArray();

            var fit = ColorHarmony.FitClosest(colors);

            Assert.Equal(ColorHarmony.ColorCount, fit.Palette.Colors.Count);
            Assert.Equal(new[] { 0, 1, 2, 3, 4 }, fit.MemberOf.OrderBy(m => m));
            Assert.True(double.IsFinite(fit.Error));

            // every input keeps its own saturation and brightness; only hue is fitted
            for (var i = 0; i < ColorHarmony.ColorCount; i++)
            {
                var input = HsvColor.FromColor(colors[i]);
                var member = fit.Palette.Colors[fit.MemberOf[i]];
                Assert.Equal(input.Saturation, member.Saturation, 6);
                Assert.Equal(input.Value, member.Value, 6);
            }
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

            // the complement of 200° (artistic 237°) is artistic 57°, which is HSV 33.25°
            var dot = ColorHarmony.Glyph(HarmonyPreset.Complementary)[1];
            Assert.Equal(new HsvColor(33.25, 0.8, 0.94).ToColor(), dot.ColorFor(200));
        }
    }
}

using System;
using System.Threading.Tasks;
using Prisc.Interop.Testing;
using Xunit;

namespace Prisc.Interop.Tests
{
    /// <summary>The pure family (colour, OKLCH and the picker's painters, the share-region rules, the
    /// H.264 mapping): the struct layouts Rust pins, the enum values, and one success, one failure
    /// and one round trip per export through the public wrappers. The behaviour vectors live next
    /// to the one implementation in <c>prisc_rust_core</c>. Every export here is any-thread.</summary>
    public sealed class PureTests
    {
        [Fact]
        public unsafe void The_structs_have_the_layout_rust_pins()
        {
            PriscOkLch v = default;
            byte* b = (byte*)&v;
            Assert.Equal(24, sizeof(PriscOkLch));
            Assert.Equal(0, (byte*)&v.l - b);
            Assert.Equal(8, (byte*)&v.c - b);
            Assert.Equal(16, (byte*)&v.h_deg - b);

            PriscAccentPalette p = default;
            byte* pb = (byte*)&p;
            Assert.Equal(136, sizeof(PriscAccentPalette));
            Assert.Equal(0, (byte*)&p.effective - pb);
            Assert.Equal(4, (byte*)&p.comet_body - pb);
            Assert.Equal(8, (byte*)&p.comet_head - pb);
            Assert.Equal(12, (byte*)&p._reserved - pb);
            Assert.Equal(16, (byte*)p.light - pb);
            Assert.Equal(56, (byte*)p.dark - pb);
            Assert.Equal(96, (byte*)p.dark_text - pb);

            PriscH264Quality q = default;
            byte* qb = (byte*)&q;
            Assert.Equal(16, sizeof(PriscH264Quality));
            Assert.Equal(0, (byte*)&q.crf - qb);
            Assert.Equal(4, (byte*)&q.nvenc_cq - qb);
            Assert.Equal(8, (byte*)&q.vt_quality - qb);
            Assert.Equal(12, (byte*)&q.vt_bitrate_kbps - qb);
        }

        [Fact]
        public void The_enums_match_the_abi()
        {
            Assert.Equal(new[] { 0, 1, 2 }, new[] { (int)OkLchChannel.Lightness, (int)OkLchChannel.Chroma, (int)OkLchChannel.Hue });
            Assert.Equal(0, (int)ShareRegionHandle.TopLeft);
            Assert.Equal(3, (int)ShareRegionHandle.BottomRight);
            Assert.Equal(4, (int)ShareRegionHandle.Left);
            Assert.Equal(7, (int)ShareRegionHandle.Bottom);
            Assert.Equal(8, (int)ShareRegionHandle.Body);
        }

        [Fact]
        public async Task The_palette_of_prisc_blue_comes_from_a_pool_thread()
        {
            InteropTestGate.Require();
            var palette = await Task.Run(() => ColorMath.Palette(0x3B97D2, maintainContrast: true));

            Assert.Equal(0x2F7CAEu, palette.Effective);
            Assert.Equal((0x3B97D2u, 0x83B4D8u), (palette.CometBody, palette.CometHead));
            Assert.Equal(10, palette.Light.Length);
            Assert.Equal(new uint[] { 0xEEF5F9, 0xD5E5EF, 0xACCBDF, 0x82B0CE, 0x5996BE, 0x2F7CAE, 0x296D99, 0x22597D, 0x1A4561, 0x133246 },
                palette.Light.ToArray());
            Assert.Equal(0x2F7CAEu, palette.Dark.Span[5]);
            Assert.Equal(0x79ABCBu, palette.DarkText.Span[5]);
            Assert.Equal(0x3B97D2u, ColorMath.Palette(0xFF3B97D2, maintainContrast: false).Effective);
        }

        [Fact]
        public void Luminance_and_oklch_round_trip()
        {
            InteropTestGate.Require();
            Assert.Equal(1.0, ColorMath.RelativeLuminance(0xFFFFFF), 12);
            Assert.Equal(0.0, ColorMath.RelativeLuminance(0x000000), 12);

            var (l, c, h) = OkLch.FromSrgb(0xFF0000);
            Assert.Equal(0.628, l, 0.0005);
            Assert.Equal(0.2577, c, 0.0005);
            Assert.Equal(29.23, h, 0.05);
            Assert.Equal(0xFF0000u, OkLch.ToSrgb(l, c, h, out var inGamut));
            Assert.True(inGamut);

            OkLch.ToSrgb(0.7, 0.35, 150, out inGamut);
            Assert.False(inGamut);
        }

        [Fact]
        public void A_non_finite_value_is_refused_with_the_native_reason()
        {
            InteropTestGate.Require();
            var e = Assert.Throws<InteropException>(() => OkLch.ToSrgb(double.NaN, 0.1, 0, out _));
            Assert.Equal(InteropStatus.InvalidArg, e.Status);
            Assert.Equal("prisc_oklch_to_srgb", e.Operation);
            Assert.Contains("not finite", e.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void A_chart_paints_the_gamut_and_refuses_a_short_buffer()
        {
            InteropTestGate.Require();
            var pixels = new uint[32 * 16];
            Array.Fill(pixels, 0xDEADBEEFu);
            OkLch.PaintSlice(OkLchChannel.Hue, 0.7, 0.15, 250, 32, 16, pixels);
            Assert.DoesNotContain(0xDEADBEEFu, pixels);
            Assert.Contains(pixels, p => p >> 24 == 0xFF);
            Assert.Contains(0u, pixels);

            var e = Assert.Throws<InteropException>(() => OkLch.PaintSlice(OkLchChannel.Hue, 0.7, 0.15, 250, 32, 16, new uint[10]));
            Assert.Equal(InteropStatus.InvalidArg, e.Status);
            Assert.Throws<ArgumentOutOfRangeException>(() => OkLch.PaintSlice(OkLchChannel.Hue, 0.7, 0.15, 250, 0, 16, pixels));
        }

        [Fact]
        public void A_hue_track_sweeps_the_wheel()
        {
            InteropTestGate.Require();
            var track = new uint[256];
            OkLch.PaintTrack(OkLchChannel.Hue, 0.7, 0.05, 0, track);
            Assert.All(track, p => Assert.Equal(0xFFu, p >> 24));
            Assert.NotEqual(track[0], track[128]);
            OkLch.PaintTrack(OkLchChannel.Lightness, 0.5, 0, 0, Span<uint>.Empty);
        }

        [Fact]
        public void Share_region_normalizes_and_drags()
        {
            InteropTestGate.Require();
            Assert.Equal((10, -20, 64, 800), ShareRegion.Normalize(10, -20, 5, 801));
            Assert.Equal((100, 100, 450, 280), ShareRegion.ApplyDrag(100, 100, 400, 300, ShareRegionHandle.BottomRight, 50, -20));
            Assert.Equal((0, 100, 100, 300), ShareRegion.ApplyDrag(100, 100, 400, 300, ShareRegionHandle.Right, -500, 0));

            var e = Assert.Throws<InteropException>(() => ShareRegion.ApplyDrag(0, 0, 100, 100, (ShareRegionHandle)9, 0, 0));
            Assert.Equal(InteropStatus.InvalidArg, e.Status);
        }

        [Fact]
        public void H264_quality_maps_one_crf_and_refuses_a_bad_one()
        {
            InteropTestGate.Require();
            Assert.Equal(new H264Quality(23, 27, 55, 1673), H264Quality.For(23, 1920, 1080, 30000, 1001));

            var e = Assert.Throws<ArgumentOutOfRangeException>(() => H264Quality.For(52, 1920, 1080, 30, 1));
            Assert.Contains("crf 52 is outside 0-51", e.Message, StringComparison.Ordinal);
            Assert.Equal("crf", e.ParamName);
            Assert.Equal("width", Assert.Throws<ArgumentOutOfRangeException>(() => H264Quality.For(23, 0, 1080, 30, 1)).ParamName);
            Assert.Equal("width", Assert.Throws<ArgumentOutOfRangeException>(() => H264Quality.For(23, 1920, -1, 30, 1)).ParamName);
            Assert.Equal("fpsNum", Assert.Throws<ArgumentOutOfRangeException>(() => H264Quality.For(23, 1920, 1080, 30, 0)).ParamName);
        }
    }
}

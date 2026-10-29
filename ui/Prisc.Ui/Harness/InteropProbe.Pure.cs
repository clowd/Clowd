using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Prisc.Interop;

namespace Prisc.UI.Harness
{
    internal static partial class InteropProbe
    {
        /// <summary>The wrapper-level items of this area: one success, one documented failure and one
        /// round trip per export family, keyed <c>&lt;area&gt;.&lt;item&gt;</c>. Every pure export is
        /// any-thread, so every item also runs from a pool thread. The values are the ones the Rust
        /// tests pin (prisc_rust_core color, palette, chart, share_region and encoding), so the E2E
        /// row compares them against those vectors.</summary>
        static partial void ProbePure(ProbeRun run)
        {
            // colour
            run.Item("pure.color.palette", () =>
            {
                var p = ColorMath.Palette(SharedDefaults.PriscBlue, maintainContrast: true);
                return new Dictionary<string, object>
                {
                    ["effective"] = PureHex(p.Effective),
                    ["comet"] = new[] { PureHex(p.CometBody), PureHex(p.CometHead) },
                    ["light"] = p.Light.ToArray().Select(PureHex).ToList(),
                    ["dark"] = p.Dark.ToArray().Select(PureHex).ToList(),
                    ["dark_text"] = p.DarkText.ToArray().Select(PureHex).ToList(),
                    ["raw_effective"] = PureHex(ColorMath.Palette(SharedDefaults.PriscBlue, maintainContrast: false).Effective),
                };
            }, anyThread: true);
            run.Item("pure.color.luminance", () => new[]
            {
                Math.Round(ColorMath.RelativeLuminance(0x000000), 6),
                Math.Round(ColorMath.RelativeLuminance(SharedDefaults.PriscBlue), 6),
                Math.Round(ColorMath.RelativeLuminance(0xFFFFFF), 6),
            }, anyThread: true);

            // OKLCH and the picker's painters
            run.Item("pure.oklch.round_trip", () =>
            {
                var (l, c, h) = OkLch.FromSrgb(0xFF0000);
                var back = OkLch.ToSrgb(l, c, h, out var inGamut);
                OkLch.ToSrgb(0.7, 0.35, 150, out var vividInGamut);
                return new Dictionary<string, object>
                {
                    ["l"] = Math.Round(l, 4),
                    ["c"] = Math.Round(c, 4),
                    ["h"] = Math.Round(h, 2),
                    ["back"] = PureHex(back),
                    ["in_gamut"] = inGamut,
                    ["vivid_in_gamut"] = vividInGamut,
                };
            }, anyThread: true);
            run.Item("pure.oklch.not_finite", () => PureExpectFailure(() => OkLch.ToSrgb(double.NaN, 0.1, 0, out _),
                InteropStatus.InvalidArg), anyThread: true);
            run.Item("pure.oklch.slice", () => new Dictionary<string, object>
            {
                ["lightness"] = PureSlice(OkLchChannel.Lightness),
                ["chroma"] = PureSlice(OkLchChannel.Chroma),
                ["hue"] = PureSlice(OkLchChannel.Hue),
            }, anyThread: true);
            run.Item("pure.oklch.slice_short_buffer", () => PureExpectFailure(
                () => { OkLch.PaintSlice(OkLchChannel.Hue, 0.7, 0.15, 250, 8, 8, new uint[63]); return null; },
                InteropStatus.InvalidArg), anyThread: true);
            run.Item("pure.oklch.track", () =>
            {
                var track = new uint[256];
                OkLch.PaintTrack(OkLchChannel.Hue, 0.7, 0.15, 250, track);
                return new Dictionary<string, object>
                {
                    ["opaque"] = track.Count(p => p >> 24 == 0xFF),
                    ["transparent"] = track.Count(p => p == 0),
                    ["first"] = track[0].ToString("X8", CultureInfo.InvariantCulture),
                    ["middle"] = track[128].ToString("X8", CultureInfo.InvariantCulture),
                };
            }, anyThread: true);

            // share region
            run.Item("pure.share_region.normalize", () => PureRect(ShareRegion.Normalize(10, -20, 5, 801)), anyThread: true);
            run.Item("pure.share_region.drag", () => new Dictionary<string, object>
            {
                ["bottom_right"] = PureRect(ShareRegion.ApplyDrag(100, 100, 400, 300, ShareRegionHandle.BottomRight, 50, -20)),
                ["right_past_left"] = PureRect(ShareRegion.ApplyDrag(100, 100, 400, 300, ShareRegionHandle.Right, -500, 0)),
                ["left_past_floor"] = PureRect(ShareRegion.ApplyDrag(0, 0, 200, 200, ShareRegionHandle.Left, 180, 0)),
                ["body"] = PureRect(ShareRegion.ApplyDrag(100, 100, 400, 300, ShareRegionHandle.Body, -150, 35)),
            }, anyThread: true);
            run.Item("pure.share_region.bad_handle", () => PureExpectFailure(
                () => ShareRegion.ApplyDrag(0, 0, 100, 100, (ShareRegionHandle)9, 0, 0), InteropStatus.InvalidArg), anyThread: true);

            // the H.264 mapping
            run.Item("pure.h264.quality", () => new[] { 0, 23, 51 }
                .Select(crf => H264Quality.For(crf, 1920, 1080, 30000, 1001))
                .Select(q => $"crf={q.Crf} nvenc_cq={q.NvencCq} vt_q={q.VideoToolboxQuality} vt_kbps={q.VideoToolboxBitrateKbps}")
                .ToList(), anyThread: true);
            run.Item("pure.h264.bad_crf", () =>
            {
                try
                {
                    H264Quality.For(52, 1920, 1080, 30, 1);
                }
                catch (ArgumentOutOfRangeException e)
                {
                    return e.Message.Split(Environment.NewLine)[0];
                }
                throw new InvalidOperationException("crf 52 was accepted");
            }, anyThread: true);
        }

        /// <summary>A 64x64 chart through (0.7, 0.15, 250) holding <paramref name="channel"/>: how many
        /// pixels are opaque, transparent and partly covered (the anti-aliased gamut edge).</summary>
        private static Dictionary<string, object> PureSlice(OkLchChannel channel)
        {
            var pixels = new uint[64 * 64];
            OkLch.PaintSlice(channel, 0.7, 0.15, 250, 64, 64, pixels);
            return new Dictionary<string, object>
            {
                ["opaque"] = pixels.Count(p => p >> 24 == 0xFF),
                ["transparent"] = pixels.Count(p => p == 0),
                ["edge"] = pixels.Count(p => p != 0 && p >> 24 != 0xFF),
            };
        }

        /// <summary>The documented failure as the probe records it: the status name when the call
        /// fails as documented, an error when it succeeds or fails differently.</summary>
        private static object PureExpectFailure(Func<object> call, InteropStatus expected)
        {
            try
            {
                call();
            }
            catch (InteropException e) when (e.Status == expected)
            {
                return e.Status.ToString();
            }
            throw new InvalidOperationException($"expected {expected}");
        }

        private static string PureHex(uint rgb) => "#" + rgb.ToString("X6", CultureInfo.InvariantCulture);

        private static string PureRect((int X, int Y, int Width, int Height) r) => $"{r.X},{r.Y} {r.Width}x{r.Height}";
    }
}

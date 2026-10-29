using System;

namespace Prisc.Interop
{
    /// <summary>
    /// OKLCH conversions and the colour picker's gamut painters (<c>prisc_oklch_*</c>), over
    /// <c>prisc_rust_core::color</c>. Lightness is 0 to 1, hue is in degrees, colours are
    /// <c>0x00RRGGBB</c>, and painted pixels are premultiplied BGRA words (<c>0xAARRGGBB</c>, what
    /// Avalonia's <c>Bgra8888</c> reads). Every member: any thread.
    /// </summary>
    public static unsafe class OkLch
    {
        /// <summary>The OKLCH of an sRGB colour; a grey comes back with hue 0.</summary>
        public static (double L, double C, double H) FromSrgb(uint rgb)
        {
            InteropLibrary.Require();
            PriscOkLch v;
            Native.Check(NativeMethods.prisc_oklch_from_srgb(rgb & 0xFFFFFF, &v), "prisc_oklch_from_srgb");
            return (v.l, v.c, v.h_deg);
        }

        /// <summary>The nearest sRGB colour to (<paramref name="l"/>, <paramref name="c"/>,
        /// <paramref name="h"/>): a lightness past either end is white or black, and out of gamut the
        /// chroma alone is lowered until it fits. <paramref name="inGamut"/> says whether the value
        /// was inside sRGB as given.</summary>
        public static uint ToSrgb(double l, double c, double h, out bool inGamut)
        {
            InteropLibrary.Require();
            var v = new PriscOkLch { l = l, c = c, h_deg = h };
            uint rgb;
            byte inside;
            Native.Check(NativeMethods.prisc_oklch_to_srgb(&v, &rgb, &inside), "prisc_oklch_to_srgb");
            inGamut = inside != 0;
            return rgb;
        }

        /// <summary>
        /// Paints the gamut chart through (<paramref name="l"/>, <paramref name="c"/>,
        /// <paramref name="h"/>) that holds <paramref name="fixedChannel"/> into
        /// <paramref name="pixels"/> (<paramref name="width"/> x <paramref name="height"/>, top row
        /// first): lightness held, hue across and chroma up; chroma held, hue across and lightness
        /// up; hue held, lightness across and chroma up. Transparent where sRGB has no such colour,
        /// with an anti-aliased edge.
        /// </summary>
        public static void PaintSlice(OkLchChannel fixedChannel, double l, double c, double h, int width, int height, Span<uint> pixels)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
            InteropLibrary.Require();
            var v = new PriscOkLch { l = l, c = c, h_deg = h };
            fixed (uint* p = pixels)
                Native.Check(NativeMethods.prisc_oklch_paint_slice((int)fixedChannel, &v, (uint)width, (uint)height, p, (nuint)pixels.Length),
                    "prisc_oklch_paint_slice");
        }

        /// <summary>Paints a slider track: <paramref name="channel"/> swept across its range left to
        /// right over <paramref name="pixels"/> (lightness 0 to 1, chroma 0 to
        /// <c>SharedDefaults.OklchChromaMax</c>, hue 0 to 360) at the value's other two channels,
        /// opaque where sRGB has the colour and transparent where it does not.</summary>
        public static void PaintTrack(OkLchChannel channel, double l, double c, double h, Span<uint> pixels)
        {
            InteropLibrary.Require();
            var v = new PriscOkLch { l = l, c = c, h_deg = h };
            fixed (uint* p = pixels)
                Native.Check(NativeMethods.prisc_oklch_paint_track((int)channel, &v, p, (nuint)pixels.Length), "prisc_oklch_paint_track");
        }
    }
}

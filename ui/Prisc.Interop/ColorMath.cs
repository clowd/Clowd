using System;

namespace Prisc.Interop
{
    /// <summary>
    /// Everything one accent colour drives (<c>prisc_color_accent_palette</c>), every colour
    /// <c>0x00RRGGBB</c>: the effective accent, the hint comet's two colours and the three ten-step
    /// ramps the app's theme is repainted from, each with its base at step 5.
    /// </summary>
    public sealed class AccentPalette
    {
        internal AccentPalette(uint effective, uint cometBody, uint cometHead, uint[] light, uint[] dark, uint[] darkText)
        {
            Effective = effective;
            CometBody = cometBody;
            CometHead = cometHead;
            Light = light;
            Dark = dark;
            DarkText = darkText;
        }

        /// <summary>The accent the app paints with: the pick, darkened until white text on it reads
        /// when the contrast rule is on.</summary>
        public uint Effective { get; }

        /// <summary>The hint comet's body, from the pick itself (never the darkened fill).</summary>
        public uint CometBody { get; }

        /// <summary>The comet head's hot tint: the body's hue, lighter, with chroma eased off.</summary>
        public uint CometHead { get; }

        /// <summary>The light theme's ramp: the effective accent mixed toward white below step 5 and
        /// toward black above it.</summary>
        public ReadOnlyMemory<uint> Light { get; }

        /// <summary>The dark theme's fill ramp: the effective accent (lifted only if it would sink
        /// into the dark ground) mixed toward black either side.</summary>
        public ReadOnlyMemory<uint> Dark { get; }

        /// <summary>The dark theme's text ramp: the fill ramp's tints below step 5, the base lifted
        /// until it reads as text on the raised dark surfaces from step 5 up.</summary>
        public ReadOnlyMemory<uint> DarkText { get; }
    }

    /// <summary>The accent colour maths of <c>prisc_rust_core::color</c>, the one implementation
    /// the capture overlay uses too. Colours are <c>0x00RRGGBB</c>. Every member: any thread.</summary>
    public static unsafe class ColorMath
    {
        /// <summary>The palette of <paramref name="pickedRgb"/>, the accent exactly as the user picked
        /// it, with the contrast rule on or off: one native call per accent change.</summary>
        public static AccentPalette Palette(uint pickedRgb, bool maintainContrast)
        {
            InteropLibrary.Require();
            PriscAccentPalette p;
            Native.Check(NativeMethods.prisc_color_accent_palette(pickedRgb & 0xFFFFFF, maintainContrast ? (byte)1 : (byte)0, &p),
                "prisc_color_accent_palette");
            return new AccentPalette(p.effective, p.comet_body, p.comet_head,
                new ReadOnlySpan<uint>(p.light, 10).ToArray(),
                new ReadOnlySpan<uint>(p.dark, 10).ToArray(),
                new ReadOnlySpan<uint>(p.dark_text, 10).ToArray());
        }

        /// <summary>The WCAG relative luminance of <paramref name="rgb"/> (0 = black, 1 = white).</summary>
        public static double RelativeLuminance(uint rgb)
        {
            InteropLibrary.Require();
            double luminance;
            Native.Check(NativeMethods.prisc_color_relative_luminance(rgb & 0xFFFFFF, &luminance), "prisc_color_relative_luminance");
            return luminance;
        }

        /// <summary>The ink a sticky note's text is drawn in on <paramref name="paperArgb"/>
        /// (<c>0xAARRGGBB</c>; translucent paper is judged over white), as <c>0x00RRGGBB</c>: a deep
        /// shade of the paper's hue at the note ink contrast, a pale tint on a dark paper.</summary>
        public static uint NoteInk(uint paperArgb)
        {
            InteropLibrary.Require();
            uint ink;
            Native.Check(NativeMethods.prisc_color_note_ink(paperArgb, &ink), "prisc_color_note_ink");
            return ink;
        }
    }
}

using System;
using Avalonia.Media;
using Prisc.Config;
using Prisc.Interop.Testing;
using Xunit;

namespace Prisc.Shared.Tests
{
    /// <summary>
    /// <see cref="AccentColors"/> and the accent settings over <c>prisc_rust_core::color</c>, the
    /// one implementation the capture overlay paints with too. The maths' own vectors (the contrast
    /// rule, the pinned comet colours, the ramps as master's C# built them) live with it in
    /// crates/prisc_rust_core/src/color.rs and color/palette.rs; these check that the C# side hands
    /// colours across and back intact and that the settings apply the rule where they should.
    /// </summary>
    public class AccentColorTests
    {
        private static double ContrastWithWhite(Color color) => 1.05 / (AccentColors.RelativeLuminance(color) + 0.05);

        [Fact]
        public void The_shipped_constants_need_no_native_call()
        {
            // generated literals: reading them must work even where the library is missing
            Assert.Equal(Color.FromRgb(0x3B, 0x97, 0xD2), AccentColors.PriscBlue);
            Assert.Equal(Color.FromRgb(0x2F, 0x7C, 0xAE), AccentColors.Default);
            Assert.Equal(0x2F7CAEu, SharedDefaults.DefaultAccent);
            Assert.Equal(4.5, SharedDefaults.MinimumContrastWithWhite);
        }

        [Fact]
        public void The_default_is_prisc_blue_put_through_the_contrast_rule()
        {
            InteropTestGate.Require();
            Assert.Equal(AccentColors.Default, AccentColors.EnsureContrastWithWhite(AccentColors.PriscBlue));
        }

        [Fact]
        public void Luminance_spans_black_to_white()
        {
            InteropTestGate.Require();
            Assert.Equal(0.0, AccentColors.RelativeLuminance(Colors.Black), 6);
            Assert.Equal(1.0, AccentColors.RelativeLuminance(Colors.White), 6);
            Assert.InRange(ContrastWithWhite(AccentColors.PriscBlue), 3.2, 3.25);
        }

        [Fact]
        public void EnsureContrastWithWhite_darkens_a_light_colour_and_keeps_alpha()
        {
            InteropTestGate.Require();
            var adjusted = AccentColors.EnsureContrastWithWhite(Color.FromArgb(0x80, 0xFF, 0xC0, 0x40));

            Assert.Equal(0x80, adjusted.A);
            Assert.True(adjusted.R > adjusted.G && adjusted.G > adjusted.B);
            Assert.InRange(ContrastWithWhite(adjusted), SharedDefaults.MinimumContrastWithWhite, SharedDefaults.MinimumContrastWithWhite + 0.1);
            Assert.Equal(Colors.Black, AccentColors.EnsureContrastWithWhite(Colors.Black));
        }

        [Fact]
        public void The_comet_matches_the_capturer()
        {
            InteropTestGate.Require();
            Assert.Equal((Color.FromRgb(0x3B, 0x97, 0xD2), Color.FromRgb(0x83, 0xB4, 0xD8)), AccentColors.CometColors(AccentColors.PriscBlue));
        }

        [Fact]
        public void The_palette_carries_the_effective_accent_at_step_five()
        {
            InteropTestGate.Require();
            var palette = AccentColors.Palette(AccentColors.PriscBlue, maintainContrast: true);

            Assert.Equal(AccentColors.Default, palette.Effective);
            Assert.Equal(10, palette.Light.Length);
            Assert.Equal(AccentColors.Default, palette.Light[5]);
            Assert.Equal(AccentColors.Default, palette.Dark[5]);
            Assert.Equal(Color.FromRgb(0xEE, 0xF5, 0xF9), palette.Light[0]);
            Assert.Equal(Color.FromRgb(0x79, 0xAB, 0xCB), palette.DarkText[5]);
            Assert.Equal(AccentColors.CometColors(AccentColors.PriscBlue), (palette.CometBody, palette.CometHead));

            Assert.Equal(AccentColors.PriscBlue, AccentColors.Palette(AccentColors.PriscBlue, maintainContrast: false).Effective);
        }

        [Fact]
        public void The_palette_keeps_a_translucent_picks_alpha_where_the_accent_itself_shows()
        {
            InteropTestGate.Require();
            // a light pick: darkened for white text, then lifted for the dark ground's text ramp
            var picked = Color.FromArgb(0x80, 0xFF, 0xC0, 0x40);
            var palette = AccentColors.Palette(picked, maintainContrast: true);

            Assert.Equal(AccentColors.EnsureContrastWithWhite(picked), palette.Effective);
            Assert.Equal(palette.Effective, palette.Light[5]);
            Assert.Equal(palette.Effective, palette.Dark[5]);
            Assert.Equal(0xFF, palette.Light[4].A);
            Assert.Equal(0xFF, palette.Light[6].A);
            Assert.Equal(0xFF, palette.DarkText[5].A);
            Assert.Equal(0xFF, palette.CometBody.A);

            // near black with the guard off: the dark fill is lifted off the ground, so it is a mix
            var dim = AccentColors.Palette(Color.FromArgb(0x80, 0x08, 0x08, 0x10), maintainContrast: false);
            Assert.Equal(0x80, dim.Effective.A);
            Assert.Equal(0x80, dim.Light[5].A);
            Assert.NotEqual(dim.Effective.ToUInt32() & 0xFFFFFF, dim.Dark[5].ToUInt32() & 0xFFFFFF);
            Assert.Equal(0xFF, dim.Dark[5].A);
        }

        [Fact]
        public void GetEffectiveAccentColor_UsesTheChosenColorWhenNotFollowingTheSystem()
        {
            InteropTestGate.Require();
            var settings = new SettingsGeneral { UseSystemAccentColor = false, AccentColor = Color.FromRgb(0x00, 0x40, 0x00) };

            Assert.Equal(Color.FromRgb(0x00, 0x40, 0x00), settings.GetEffectiveAccentColor());
        }

        [Fact]
        public void AccentColor_IsStoredExactlyAsPicked()
        {
            // the correction happens where the color is used, not on assignment: turning
            // MaintainMinimumContrast off has to be able to give the original back.
            var settings = new SettingsGeneral { UseSystemAccentColor = false, AccentColor = Colors.White };

            Assert.Equal(Colors.White, settings.AccentColor);
        }

        [Fact]
        public void GetEffectiveAccentColor_CorrectsAColorTooLightForWhiteText()
        {
            InteropTestGate.Require();
            var settings = new SettingsGeneral { UseSystemAccentColor = false, AccentColor = Colors.White };

            Assert.NotEqual(settings.AccentColor, settings.GetEffectiveAccentColor());
            Assert.True(ContrastWithWhite(settings.GetEffectiveAccentColor()) >= SharedDefaults.MinimumContrastWithWhite);
            Assert.Equal(settings.GetEffectiveAccentColor(), settings.GetAccentPalette().Effective);
        }

        [Fact]
        public void GetEffectiveAccentColor_HandsBackTheRawColorWhenContrastIsNotMaintained()
        {
            InteropTestGate.Require();
            var settings = new SettingsGeneral
            {
                UseSystemAccentColor = false,
                AccentColor = Colors.White,
                MaintainMinimumContrast = false,
            };

            // the user asked for exactly their color, illegible or not
            Assert.Equal(Colors.White, settings.GetEffectiveAccentColor());
            Assert.Equal(Colors.White, settings.GetAccentPalette().Effective);
        }

        [Fact]
        public void DefaultAccent_IsPriscBlueCorrectedToTheDocumentedValue()
        {
            InteropTestGate.Require();
            // the stored default is the raw legacy blue; correcting it at use has to land exactly on
            // the value the capturer corrects its own --accent-color default to.
            var settings = new SettingsGeneral { UseSystemAccentColor = false };

            Assert.True(settings.MaintainMinimumContrast);
            Assert.Equal(AccentColors.PriscBlue, settings.AccentColor);
            Assert.Equal(AccentColors.Default, settings.GetEffectiveAccentColor());
        }

        [Fact]
        public void UseSystemAccentColor_IsOffWhereThereIsNoSystemAccent()
        {
            var settings = new SettingsGeneral { UseSystemAccentColor = true };

            Assert.Equal(AccentColors.SystemAccentSupported, settings.UseSystemAccentColor);
            Assert.Equal(OperatingSystem.IsWindows(), AccentColors.SystemAccentSupported);
        }
    }
}

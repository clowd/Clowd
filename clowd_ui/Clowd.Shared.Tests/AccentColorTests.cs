using System;
using Avalonia.Media;
using Clowd.Config;
using Xunit;

namespace Clowd.Shared.Tests
{
    public class AccentColorTests
    {
        [Fact]
        public void ContrastWithWhite_MatchesKnownRatios()
        {
            Assert.Equal(1.0, AccentColors.ContrastWithWhite(Colors.White), 3);
            Assert.Equal(21.0, AccentColors.ContrastWithWhite(Colors.Black), 3);
            // the legacy clowd blue is the color issue #48 is about: readable-ish, but under AA.
            Assert.InRange(AccentColors.ContrastWithWhite(AccentColors.ClowdBlue), 3.2, 3.25);
        }

        [Fact]
        public void EnsureContrastWithWhite_LeavesDarkColorsAlone()
        {
            foreach (var color in new[] { Colors.Black, Color.FromRgb(0x00, 0x00, 0x80), Color.FromRgb(0x68, 0x00, 0x81) })
                Assert.Equal(color, AccentColors.EnsureContrastWithWhite(color));
        }

        [Theory]
        [InlineData(0xFF, 0xFF, 0xFF)] // white
        [InlineData(0x3B, 0x97, 0xD2)] // clowd blue
        [InlineData(0x00, 0x78, 0xD4)] // the Windows default accent
        [InlineData(0xFF, 0xFF, 0x00)] // a maximally light-but-saturated accent
        [InlineData(0xF0, 0xC0, 0xF4)] // a light purple from a Windows accent palette
        public void EnsureContrastWithWhite_DarkensUntilLegible(byte r, byte g, byte b)
        {
            var adjusted = AccentColors.EnsureContrastWithWhite(Color.FromRgb(r, g, b));

            Assert.True(AccentColors.ContrastWithWhite(adjusted) >= AccentColors.MinimumContrastWithWhite,
                        $"#{adjusted.R:X2}{adjusted.G:X2}{adjusted.B:X2} is still too light");

            // darker on every channel, and never darker than it had to be
            Assert.True(adjusted.R <= r && adjusted.G <= g && adjusted.B <= b);
            Assert.InRange(AccentColors.ContrastWithWhite(adjusted), AccentColors.MinimumContrastWithWhite, AccentColors.MinimumContrastWithWhite + 0.1);
        }

        [Fact]
        public void EnsureContrastWithWhite_PreservesAlphaAndHueOrdering()
        {
            var adjusted = AccentColors.EnsureContrastWithWhite(Color.FromArgb(0x80, 0xFF, 0xC0, 0x40));

            Assert.Equal(0x80, adjusted.A);
            Assert.True(adjusted.R > adjusted.G && adjusted.G > adjusted.B);
        }

        [Theory]
        [InlineData(0x3B, 0x97, 0xD2)] // clowd blue
        [InlineData(0x00, 0x78, 0xD4)] // the Windows default accent
        [InlineData(0x7A, 0x1F, 0xA2)] // a deep purple
        [InlineData(0xFF, 0xFF, 0x00)] // a maximally light-but-saturated accent
        [InlineData(0xE8, 0x11, 0x23)] // a red
        public void CometColors_KeepTheHueAtAGlowingLightness(byte r, byte g, byte b)
        {
            var picked = Color.FromRgb(r, g, b);
            var (body, head) = AccentColors.CometColors(picked);
            var (_, pickedC, pickedH) = AccentColors.ToOkLch(picked);
            var (bodyL, bodyC, bodyH) = AccentColors.ToOkLch(body);
            var (headL, headC, _) = AccentColors.ToOkLch(head);

            // light enough to stand off the graphite tray, not so light it washes out
            Assert.InRange(bodyL, AccentColors.CometMinLightness - 0.01, AccentColors.CometMaxLightness + 0.01);

            // the user's hue, as long as there is chroma for a hue to be read from
            if (pickedC > 0.05 && bodyC > 0.05)
                Assert.True(Math.Abs(Math.IEEERemainder(bodyH - pickedH, 2 * Math.PI)) < 0.06, $"hue drifted to #{body.R:X2}{body.G:X2}{body.B:X2}");

            // the head is the hotter, paler core of the same colour
            Assert.True(headL > bodyL);
            Assert.True(headC < bodyC + 1e-3);
        }

        /// <summary>clowd_capture/src/accent.rs pins the same values: the hint comet and the tray
        /// comet are derived from one pick by two ports of the same maths.</summary>
        [Fact]
        public void CometColors_MatchTheCapturer()
        {
            Assert.Equal((Color.FromRgb(0x3B, 0x97, 0xD2), Color.FromRgb(0x83, 0xB4, 0xD8)), AccentColors.CometColors(AccentColors.ClowdBlue));
            Assert.Equal((Color.FromRgb(0xAE, 0x59, 0xDA), Color.FromRgb(0xBE, 0x8E, 0xDA)), AccentColors.CometColors(Color.FromRgb(0x7A, 0x1F, 0xA2)));
        }

        [Theory]
        [InlineData(0x3B, 0x97, 0xD2)]
        [InlineData(0x12, 0x34, 0x56)]
        [InlineData(0xFF, 0x80, 0x00)]
        public void OkLch_RoundTrips(byte r, byte g, byte b)
        {
            var color = Color.FromRgb(r, g, b);
            var (l, c, h) = AccentColors.ToOkLch(color);
            Assert.Equal(color, AccentColors.FromOkLch(l, c, h));
        }

        /// <summary>The capturer carries the same default in its own CLI (clowd_capture/src/settings.rs)
        /// for standalone runs; if this value moves, that one has to move with it.</summary>
        [Fact]
        public void Default_IsContrastCorrectedClowdBlue()
        {
            Assert.Equal(Color.FromRgb(0x2F, 0x7C, 0xAE), AccentColors.Default);
        }

        [Fact]
        public void GetEffectiveAccentColor_UsesTheChosenColorWhenNotFollowingTheSystem()
        {
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
            var settings = new SettingsGeneral { UseSystemAccentColor = false, AccentColor = Colors.White };

            Assert.NotEqual(settings.AccentColor, settings.GetEffectiveAccentColor());
            Assert.True(AccentColors.ContrastWithWhite(settings.GetEffectiveAccentColor()) >= AccentColors.MinimumContrastWithWhite);
        }

        [Fact]
        public void GetEffectiveAccentColor_HandsBackTheRawColorWhenContrastIsNotMaintained()
        {
            var settings = new SettingsGeneral
            {
                UseSystemAccentColor = false,
                AccentColor = Colors.White,
                MaintainMinimumContrast = false,
            };

            // the user asked for exactly their color, illegible or not
            Assert.Equal(Colors.White, settings.GetEffectiveAccentColor());
        }

        [Fact]
        public void EffectiveAccentColor_IsAlwaysLegibleByDefault()
        {
            // whatever the platform hands back (system accent or the stored color), the overlay
            // never receives something white text cannot sit on.
            var settings = new SettingsGeneral();

            Assert.True(settings.MaintainMinimumContrast);
            Assert.True(AccentColors.ContrastWithWhite(settings.GetEffectiveAccentColor()) >= AccentColors.MinimumContrastWithWhite);
        }

        [Fact]
        public void DefaultAccent_IsClowdBlueCorrectedToTheDocumentedValue()
        {
            // the stored default is the raw legacy blue; correcting it at use has to land exactly on
            // the value the capturer corrects its own --accent-color default to (src/accent.rs).
            var settings = new SettingsGeneral { UseSystemAccentColor = false };

            Assert.Equal(AccentColors.ClowdBlue, settings.AccentColor);
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

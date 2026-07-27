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
            // the legacy clowd blue is the colour issue #48 is about: readable-ish, but under AA.
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
            var settings = new SettingsCapture { UseSystemAccentColor = false, AccentColor = Color.FromRgb(0x00, 0x40, 0x00) };

            Assert.Equal(Color.FromRgb(0x00, 0x40, 0x00), settings.GetEffectiveAccentColor());
        }

        [Fact]
        public void AccentColor_IsContrastCorrectedOnAssignment()
        {
            var settings = new SettingsCapture { UseSystemAccentColor = false, AccentColor = Colors.White };

            // what the settings page shows in its swatch is what the overlay gets
            Assert.Equal(settings.AccentColor, settings.GetEffectiveAccentColor());
            Assert.True(AccentColors.ContrastWithWhite(settings.AccentColor) >= AccentColors.MinimumContrastWithWhite);
        }

        [Fact]
        public void EffectiveAccentColor_IsAlwaysLegible()
        {
            // whatever the platform hands back (system accent or the stored colour), the overlay
            // never receives something white text cannot sit on.
            var settings = new SettingsCapture();

            Assert.True(AccentColors.ContrastWithWhite(settings.GetEffectiveAccentColor()) >= AccentColors.MinimumContrastWithWhite);
        }

        [Fact]
        public void UseSystemAccentColor_IsOffWhereThereIsNoSystemAccent()
        {
            var settings = new SettingsCapture { UseSystemAccentColor = true };

            Assert.Equal(AccentColors.SystemAccentSupported, settings.UseSystemAccentColor);
            Assert.Equal(OperatingSystem.IsWindows(), AccentColors.SystemAccentSupported);
        }
    }
}

using System;
using System.Globalization;
using Clowd.UI.Controls;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The inspector's reset dots take their default from XAML as a string ("0.5") against a
    /// double binding. GitHub #102: under a comma-decimal locale the dot compared and reset
    /// through the current culture, where "0.5" parses as 5 — so it never hid at the default and a
    /// click sent Center X to 100% (5 clamped to 1) instead of 50%. The value logic is exercised
    /// through <see cref="ResetDefaultValue"/>, which is what the control calls.
    /// </summary>
    public class ResetDefaultButtonTests
    {
        private static T UnderCulture<T>(string culture, Func<T> body)
        {
            var saved = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture;
            try
            {
                return body();
            }
            finally
            {
                (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = saved;
            }
        }

        [Theory]
        [InlineData("en-US")]
        [InlineData("de-DE")]
        [InlineData("fr-FR")]
        public void A_fractional_string_default_matches_its_number_in_any_culture(string culture)
        {
            Assert.True(UnderCulture(culture, () => ResetDefaultValue.IsDefault(0.5, "0.5")));
            Assert.True(UnderCulture(culture, () => ResetDefaultValue.IsDefault(0.25, "0.25")));
            Assert.False(UnderCulture(culture, () => ResetDefaultValue.IsDefault(0.7, "0.5")));
            Assert.False(UnderCulture(culture, () => ResetDefaultValue.IsDefault(1.0, "0.5")));
        }

        [Theory]
        [InlineData("en-US")]
        [InlineData("de-DE")]
        [InlineData("fr-FR")]
        public void A_reset_hands_the_binding_a_typed_double_in_any_culture(string culture)
        {
            var value = UnderCulture(culture, () => ResetDefaultValue.ForReset(0.7, "0.5"));

            // a double, not the XAML string: the binding must never be handed text to parse
            Assert.Equal(0.5, Assert.IsType<double>(value));
        }

        [Fact]
        public void Integer_and_enum_bindings_get_their_own_type_back()
        {
            Assert.Equal(48, Assert.IsType<int>(ResetDefaultValue.ForReset(40, "48")));
            Assert.Equal(DayOfWeek.Monday,
                Assert.IsType<DayOfWeek>(ResetDefaultValue.ForReset(DayOfWeek.Friday, "Monday")));
            Assert.True(ResetDefaultValue.IsDefault(DayOfWeek.Monday, DayOfWeek.Monday));
            Assert.False(ResetDefaultValue.IsDefault(DayOfWeek.Monday, DayOfWeek.Friday));
        }

        [Fact]
        public void Typed_and_string_defaults_pass_through_untouched()
        {
            // a default set from code keeps its own type
            Assert.Equal(true, ResetDefaultValue.ForReset(false, true));
            Assert.False(ResetDefaultValue.IsDefault(false, true));

            // a string-valued binding (the colour rows) resets to the string itself
            Assert.Equal("#FFFF0000", ResetDefaultValue.ForReset("#FF00FF00", "#FFFF0000"));
            Assert.False(ResetDefaultValue.IsDefault("#FF00FF00", "#FFFF0000"));
            Assert.True(ResetDefaultValue.IsDefault("#FFFF0000", "#FFFF0000"));

            // nothing bound yet: the default is returned as it is, and does not count as matched
            Assert.Equal("0.5", ResetDefaultValue.ForReset(null, "0.5"));
            Assert.False(ResetDefaultValue.IsDefault(null, "0.5"));
        }

        [Fact]
        public void Text_that_is_not_a_number_never_throws()
        {
            Assert.False(ResetDefaultValue.IsDefault(0.5, "half"));
            Assert.Equal("half", ResetDefaultValue.ForReset(0.5, "half"));
        }
    }
}

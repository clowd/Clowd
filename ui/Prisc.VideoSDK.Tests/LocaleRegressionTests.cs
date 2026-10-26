using System;
using System.Globalization;
using System.IO;
using Avalonia.Media;
using Clowd.UI.Converters;
using Clowd.Upload;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// Text that crosses a machine boundary — CSS on the clipboard, a file name the Rust overlay
    /// must reproduce, an upload's mime lookup — must not depend on the user's culture. Each test
    /// runs under a culture that breaks the naive code (comma decimals, a non-Gregorian calendar,
    /// the Turkish dotless i) and checks the invariant result. Companion to
    /// <see cref="ResetDefaultButtonTests"/> (GitHub #102).
    /// </summary>
    public class LocaleRegressionTests
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
        public void Css_colour_text_writes_a_dot_decimal_alpha_and_reads_itself_back(string culture)
        {
            var color = Color.FromArgb(128, 10, 20, 30);

            var rgba = UnderCulture(culture, () => ColorTextHelper.GetRgb(color));
            Assert.Equal("rgba(10, 20, 30, 0.5)", rgba);

            var hsla = UnderCulture(culture, () => ColorTextHelper.GetHsl(color));
            Assert.EndsWith(", 0.5)", hsla);

            // what the picker copied must be what the picker accepts back
            Assert.True(UnderCulture(culture, () => ColorTextHelper.TryParse(rgba, out _, out _)));
            Assert.True(UnderCulture(culture, () => ColorTextHelper.TryParse(hsla, out _, out _)));
        }

        [Theory]
        [InlineData("th-TH")] // Thai Buddhist calendar: year 2569
        [InlineData("ar-SA")] // Um Al-Qura calendar: year 1448
        [InlineData("de-DE")] // "/" and ":" would become the culture's separators
        public void Capture_file_names_are_rendered_the_way_the_rust_overlay_renders_them(string culture)
        {
            var dir = Path.Combine(Path.GetTempPath(), "clowd-locale-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var year = DateTime.Now.Year.ToString(CultureInfo.InvariantCulture);

                var name = UnderCulture(culture, () => PathConstants.GetFreePatternFileName(dir, "yyyy-MM-dd HH-mm-ss"));
                Assert.StartsWith(year + "-", name);

                // separators are literal on both sides, never the culture's
                var slashed = UnderCulture(culture, () => PathConstants.GetFreePatternFileName(dir, "yyyy/MM"));
                Assert.Equal(year + "/", slashed.Substring(0, 5));

                var dated = UnderCulture(culture, () => PathConstants.GetDatedFileName("session", "0"));
                Assert.StartsWith("session_" + year, dated);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Theory]
        [InlineData("en-US")]
        [InlineData("tr-TR")] // ".GIF".ToLower() is "gıf" here
        public void Upload_mime_lookup_lowercases_the_extension_invariantly(string culture)
        {
            var provider = new MimeProvider();

            Assert.Equal("image/gif", UnderCulture(culture, () => provider.GetMimeFromExtension(".GIF").ContentType));
            Assert.Equal("image/gif", UnderCulture(culture, () => provider.GetMimeFromExtension("gif").ContentType));
            Assert.Equal(ContentCategory.Image, UnderCulture(culture, () => provider.GetCategoryFromExtension(".GIF")));
        }
    }
}

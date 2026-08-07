using Avalonia.Media;

namespace Clowd.Drawing
{
    /// <summary>
    /// Guards against font family names Avalonia cannot parse. <see cref="FontFamily"/>'s
    /// constructor accepts any string; the parse (Typeface.Normalize) only runs during glyph
    /// lookup, deep inside the render pass, where a name it rejects — e.g. one with leading
    /// whitespace — throws a FormatException that takes down the whole compositor loop
    /// (CLOWD-10). Names reach that point from persisted settings, saved sessions and the
    /// system font enumeration, so every such boundary validates through here.
    /// </summary>
    public static class FontUtil
    {
        /// <summary>Whether <paramref name="familyName"/> survives the same parse the renderer
        /// will run. False for names that would throw mid-render (a missing font is fine — the
        /// renderer substitutes the default typeface for those).</summary>
        public static bool IsSafeFamilyName(string familyName)
        {
            if (string.IsNullOrWhiteSpace(familyName))
                return false;

            try
            {
                FontManager.Current.TryGetGlyphTypeface(new Typeface(familyName), out _);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>A <see cref="FontFamily"/> for <paramref name="familyName"/> (trimmed), or
        /// <see cref="FontFamily.Default"/> when the name would crash the renderer.</summary>
        public static FontFamily CreateSafe(string familyName)
        {
            var trimmed = familyName?.Trim();
            return IsSafeFamilyName(trimmed) ? new FontFamily(trimmed) : FontFamily.Default;
        }
    }
}

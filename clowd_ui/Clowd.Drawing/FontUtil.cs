using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
        // plain family name → font source (e.g. "Cascadia Mono" → "avares://…/Fonts#Cascadia Mono")
        private static readonly ConcurrentDictionary<string, string> _embedded =
            new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Makes a font the app embeds addressable by its plain family name. Persisted documents and
        /// tool settings store only the plain name, so they stay readable (and fall back to the system
        /// font of that name, or the default) wherever the embedding host is absent, e.g. in tests.
        /// </summary>
        public static void RegisterEmbeddedFamily(string familyName, string source) => _embedded[familyName] = source;

        /// <summary>Plain names of every registered embedded family, for font pickers.</summary>
        public static IEnumerable<string> EmbeddedFamilyNames => _embedded.Keys;

        /// <summary>Whether <paramref name="familyName"/> survives the same parse the renderer
        /// will run. False for names that would throw mid-render (a missing font is fine — the
        /// renderer substitutes the default typeface for those).</summary>
        public static bool IsSafeFamilyName(string familyName)
        {
            if (string.IsNullOrWhiteSpace(familyName))
                return false;

            if (_embedded.ContainsKey(familyName))
                return true;

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
            if (trimmed != null && _embedded.TryGetValue(trimmed, out var source))
                return FontFamily.Parse(source);
            return IsSafeFamilyName(trimmed) ? new FontFamily(trimmed) : FontFamily.Default;
        }
    }
}

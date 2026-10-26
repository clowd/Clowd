using Avalonia.Media;

namespace Clowd
{
    /// <summary>
    /// Default font families of the drawing tools. Both faces ship embedded with the app, which
    /// registers them under these plain names at startup (FontUtil.RegisterEmbeddedFamily).
    /// </summary>
    public static class EditorFonts
    {
        /// <summary>Text tool (sticky notes), and the default for any tool with a font.</summary>
        public const string Text = "Inter";
        public const double TextSize = 16;

        /// <summary>Numeric step badges.</summary>
        public const string Numeric = "Cascadia Mono";
        public const double NumericSize = 20;
        public static readonly FontWeight NumericWeight = FontWeight.Bold;
    }
}

using Prisc.VideoSDK.Model;

namespace Prisc.UI.VideoEditor
{
    /// <summary>
    /// What the editor calls each <see cref="ShapeKind"/> and which glyph stands for it: the picker
    /// tiles' labels and tooltips, and the timeline card's glyph and label, all read from here so
    /// the two places never disagree about a name.
    /// </summary>
    internal static class ShapeKinds
    {
        public static string Label(ShapeKind kind) => kind switch
        {
            ShapeKind.Rectangle => "Rectangle",
            ShapeKind.Ellipse => "Ellipse",
            ShapeKind.Triangle => "Triangle",
            ShapeKind.Diamond => "Diamond",
            ShapeKind.Polygon => "Polygon",
            ShapeKind.Star => "Star",
            ShapeKind.Heart => "Heart",
            ShapeKind.Bubble => "Bubble",
            ShapeKind.Line => "Line",
            ShapeKind.Arrow => "Arrow",
            ShapeKind.DoubleArrow => "Double arrow",
            _ => kind.ToString(),
        };

        /// <summary>The tile's tooltip: the label, except where the short label leaves out what
        /// the shape is for.</summary>
        public static string Tip(ShapeKind kind) => kind == ShapeKind.Bubble ? "Speech bubble" : Label(kind);

        /// <summary>The vector icon resource drawn on the kind's timeline card. The image editor's
        /// tool glyphs where it has one for the kind, and the toolbar's shapes glyph for the rest.</summary>
        public static string IconKey(ShapeKind kind) => kind switch
        {
            ShapeKind.Rectangle => "IconToolRectangle",
            ShapeKind.Ellipse => "IconToolEllipse",
            ShapeKind.Star => "IconStarOutline",
            ShapeKind.Line => "IconToolLine",
            ShapeKind.Arrow or ShapeKind.DoubleArrow => "IconToolArrow",
            _ => "IconShapes",
        };
    }
}

using System;
using System.Collections.Generic;
using Avalonia.Media;

namespace Clowd.UI.DrawOnScreen
{
    /// <summary>
    /// The fixed choices the draw-on-screen toolbar offers: eight colours, four sizes and the
    /// eight tools, with the localisation and icon keys that go with them. Pure tables, so the
    /// values the approved mockup pins are tested (<c>DrawPaletteTests</c>) rather than eyeballed
    /// at a smoke check.
    /// </summary>
    public static class DrawPalette
    {
        /// <summary>One size step. <see cref="Stroke"/> is the visible line width in canvas units
        /// (the brush's dot diameter, a shape's outline), <see cref="PreviewDot"/> the diameter of
        /// the dot the size button shows, <see cref="TextSize"/> the text tool's font size.</summary>
        public readonly record struct DrawSize(string Key, double Stroke, double PreviewDot, double TextSize);

        public static IReadOnlyList<Color> Colors { get; } = new[]
        {
            Color.Parse("#DF2828"),
            Color.Parse("#E27F38"),
            Color.Parse("#E2CF38"),
            Color.Parse("#86E238"),
            Color.Parse("#359EC9"),
            Color.Parse("#7855D4"),
            Color.Parse("#F4F4F4"),
            Color.Parse("#1E1E1E"),
        };

        /// <summary>Tooltip keys for <see cref="Colors"/>, index for index.</summary>
        public static IReadOnlyList<string> ColorKeys { get; } = new[]
        {
            "Draw_Color_Red",
            "Draw_Color_Orange",
            "Draw_Color_Yellow",
            "Draw_Color_Green",
            "Draw_Color_Blue",
            "Draw_Color_Purple",
            "Draw_Color_White",
            "Draw_Color_Black",
        };

        public static IReadOnlyList<DrawSize> Sizes { get; } = new[]
        {
            new DrawSize("Draw_Size_Thin", 3, 6, 18),
            new DrawSize("Draw_Size_Medium", 9, 10, 34),
            new DrawSize("Draw_Size_Thick", 15, 14, 50),
            new DrawSize("Draw_Size_Huge", 21, 18, 66),
        };

        /// <summary>The tools, in toolbar order. <see cref="ToolType.Eraser"/> lives only here: the
        /// editor never offers it. <see cref="ToolType.Pointer"/> is the select tool.</summary>
        public static IReadOnlyList<ToolType> Tools { get; } = new[]
        {
            ToolType.Pointer,
            ToolType.Brush,
            ToolType.Arrow,
            ToolType.Line,
            ToolType.Rectangle,
            ToolType.Ellipse,
            ToolType.Text,
            ToolType.Eraser,
        };

        public const int DefaultColorIndex = 0;
        public const int DefaultSizeIndex = 1;
        public const ToolType DefaultTool = ToolType.Brush;

        /// <summary>
        /// The canvas LineWidth that makes <paramref name="tool"/> draw <paramref name="size"/>'s
        /// <see cref="DrawSize.Stroke"/> as its visible width.
        /// </summary>
        /// <remarks>
        /// The one knob means different things per graphic. GraphicBrush hands perfect-freehand a
        /// size of GraphicBrush.SizePerLineWidth (3)·LineWidth, and its ink runs from half that at
        /// speed to one and a half times it at rest: the brush takes half the stroke, which puts a
        /// quick line at 0.75·Stroke and a click at 1.5·Stroke, about the weight of the shapes
        /// beside it. Rectangle, ellipse and line stroke their outline with a pen LineWidth wide,
        /// so they take the stroke as is. The arrow's tapered shaft treats LineWidth as its nominal
        /// weight (thinner at the tail, heavier toward the head) and takes it as is too, matching
        /// the mockup, where every shape is outlined at the size's stroke.
        /// </remarks>
        public static double LineWidthFor(ToolType tool, DrawSize size) =>
            tool == ToolType.Brush ? size.Stroke / 2 : size.Stroke;

        /// <summary>Tooltip key for a tool: <c>Draw_Tool_Brush</c> and so on.</summary>
        public static string ToolKey(ToolType tool) => "Draw_Tool_" + tool;

        /// <summary>Resource key of a tool's icon in Assets/VectorIcons.axaml: <c>IconToolBrush</c>
        /// and so on. The select tool has its own icon (arrow tip + four-way arrows, after the Move
        /// cursor): the editor's plain pointer is already the click-through toggle's.</summary>
        public static string ToolIconKey(ToolType tool) =>
            tool == ToolType.Pointer ? "IconDrawSelect" : "IconTool" + tool;
    }
}

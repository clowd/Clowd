using System;

namespace Clowd.UI.DrawOnScreen
{
    /// <summary>
    /// The draw-on-screen session's mode as pure transitions: which tool is picked, whether the
    /// canvases let the mouse through to the desktop, and whether the ink is hidden. Every
    /// transition returns a new instance, so the rules are tested without a window
    /// (<c>DrawOnScreenStateTests</c>).
    /// </summary>
    /// <remarks>
    /// Hidden implies click-through: invisible ink must not swallow clicks. Click-through without
    /// hiding is the "use the desktop, keep the ink up" state a right-click on a canvas drops to.
    /// </remarks>
    public sealed record DrawOnScreenState(ToolType Tool, bool ClickThrough, bool Hidden, int ColorIndex, int SizeIndex)
    {
        /// <summary>A session opens in click-through, so the desktop stays usable until a tool is picked.</summary>
        public static DrawOnScreenState Initial { get; } =
            new(DrawPalette.DefaultTool, true, false, DrawPalette.DefaultColorIndex, DrawPalette.DefaultSizeIndex);

        /// <summary>Picking a tool always resumes drawing: the ink comes back and the canvas takes the mouse.</summary>
        public DrawOnScreenState PickTool(ToolType tool) => this with { Tool = tool, ClickThrough = false, Hidden = false };

        /// <summary>Leaving click-through also unhides, since hidden ink cannot be drawn on.</summary>
        public DrawOnScreenState ToggleClickThrough() =>
            ClickThrough ? this with { ClickThrough = false, Hidden = false } : this with { ClickThrough = true };

        public DrawOnScreenState CanvasRightClick() => this with { ClickThrough = true };

        /// <summary>Hiding forces click-through; unhiding resumes the tool.</summary>
        public DrawOnScreenState ToggleHide() =>
            Hidden ? this with { Hidden = false, ClickThrough = false } : this with { Hidden = true, ClickThrough = true };

        /// <summary>Undo and clear show the ink so their effect can be seen, and hand the mouse back
        /// to the desktop: tidying up is the end of a drawing, not the start of the next one.</summary>
        public DrawOnScreenState AfterUndoOrClear() => this with { Hidden = false, ClickThrough = true };

        /// <summary>Picking a colour means "draw with this": from click-through, hidden ink or the
        /// eraser it takes up the brush; any drawing tool already in hand keeps going.</summary>
        public DrawOnScreenState SelectColor(int index) =>
            ResumeDrawing() with { ColorIndex = Math.Clamp(index, 0, DrawPalette.Colors.Count - 1) };

        /// <summary>As <see cref="SelectColor"/>, for the stroke size.</summary>
        public DrawOnScreenState SelectSize(int index) =>
            ResumeDrawing() with { SizeIndex = Math.Clamp(index, 0, DrawPalette.Sizes.Count - 1) };

        private DrawOnScreenState ResumeDrawing() =>
            ClickThrough || Hidden || Tool == ToolType.Eraser ? PickTool(ToolType.Brush) : this;

        /// <summary>Whether <paramref name="tool"/>'s button shows as active: picked, and actually drawing.</summary>
        public bool IsToolLit(ToolType tool) => Tool == tool && !ClickThrough && !Hidden;
    }
}

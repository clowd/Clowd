using System;
using System.Linq;

namespace Prisc.UI.DrawOnScreen
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
    /// A <see cref="Frozen"/> session has neither: the canvases cover a picture of the screen, not
    /// the live desktop, so there is nothing to click through to.
    /// </remarks>
    public sealed record DrawOnScreenState(ToolType Tool, bool ClickThrough, bool Hidden, int ColorIndex, int SizeIndex)
    {
        /// <summary>A session opens in click-through, so the desktop stays usable until a tool is picked.</summary>
        public static DrawOnScreenState Initial { get; } =
            new(DrawPalette.DefaultTool, true, false, DrawPalette.DefaultColorIndex, DrawPalette.DefaultSizeIndex);

        /// <summary><see cref="Initial"/> with a remembered colour and size (null for the default),
        /// clamped to the palette: the stored indices may predate a palette change.</summary>
        public static DrawOnScreenState Restored(int? colorIndex, int? sizeIndex, bool showKeys = false) => Initial with
        {
            ColorIndex = Math.Clamp(colorIndex ?? DrawPalette.DefaultColorIndex, 0, DrawPalette.Colors.Count - 1),
            SizeIndex = Math.Clamp(sizeIndex ?? DrawPalette.DefaultSizeIndex, 0, DrawPalette.Sizes.Count - 1),
            ShowKeys = showKeys,
        };

        /// <summary>Whether the keys being pressed are shown on screen. Independent of every other
        /// part of the mode: the keys are not ink, so hiding, click-through and the tools leave
        /// them alone, and they show whether or not the user is drawing.</summary>
        public bool ShowKeys { get; init; }

        public DrawOnScreenState ToggleKeys() => Frozen ? this : this with { ShowKeys = !ShowKeys };

        /// <summary>Whether the canvases are drawn over a screenshot taken as the session opened (Draw
        /// on Instant Screenshot, issue #111) rather than over the live desktop. Set once, by
        /// <see cref="Freeze"/>: the session is drawing for its whole life, and click-through, hiding
        /// and the keys are not offered.</summary>
        public bool Frozen { get; init; }

        /// <summary>A frozen session opens with the tool in hand: the picture is there to be drawn on.
        /// <paramref name="lastTool"/> is the last frozen session's tool (null for the default); one
        /// that is not a drawing tool on the toolbar falls back to the default.</summary>
        public DrawOnScreenState Freeze(ToolType? lastTool = null) => this with
        {
            Frozen = true, ClickThrough = false, Hidden = false, ShowKeys = false,
            Tool = lastTool is { } t && IsDrawingTool(t) ? t : DrawPalette.DefaultTool,
        };

        /// <summary>Whether <paramref name="tool"/> is a toolbar tool that puts ink down: not the
        /// select tool or the eraser, which have nothing to work on when a session opens.</summary>
        public static bool IsDrawingTool(ToolType tool) =>
            tool is not (ToolType.Pointer or ToolType.Eraser) && DrawPalette.Tools.Contains(tool);

        /// <summary>Picking a tool always resumes drawing: the ink comes back and the canvas takes the mouse.</summary>
        public DrawOnScreenState PickTool(ToolType tool) => this with { Tool = tool, ClickThrough = false, Hidden = false };

        /// <summary>Leaving click-through also unhides, since hidden ink cannot be drawn on.</summary>
        public DrawOnScreenState ToggleClickThrough() =>
            Frozen ? this : ClickThrough ? this with { ClickThrough = false, Hidden = false } : this with { ClickThrough = true };

        /// <summary>Esc, a right-click on a canvas, or the keyboard leaving the session: the ink stays up
        /// and the mouse goes back to the desktop. The tool stays picked, for when drawing resumes.</summary>
        public DrawOnScreenState StopDrawing() => Frozen ? this : this with { ClickThrough = true };

        /// <summary>Hiding forces click-through; unhiding resumes the tool.</summary>
        public DrawOnScreenState ToggleHide() =>
            Frozen ? this : Hidden ? this with { Hidden = false, ClickThrough = false } : this with { Hidden = true, ClickThrough = true };

        /// <summary>Undo and clear show the ink so their effect can be seen, and hand the mouse back
        /// to the desktop: tidying up is the end of a drawing, not the start of the next one. A frozen
        /// session has no desktop to hand it to, so it keeps drawing.</summary>
        public DrawOnScreenState AfterUndoOrClear() => Frozen ? this : this with { Hidden = false, ClickThrough = true };

        /// <summary>Picking a colour means "draw with this": from click-through, hidden ink, the
        /// select tool or the eraser it takes up the brush; any drawing tool already in hand keeps
        /// going.</summary>
        public DrawOnScreenState SelectColor(int index) =>
            ResumeDrawing() with { ColorIndex = Math.Clamp(index, 0, DrawPalette.Colors.Count - 1) };

        /// <summary>As <see cref="SelectColor"/>, for the stroke size.</summary>
        public DrawOnScreenState SelectSize(int index) =>
            ResumeDrawing() with { SizeIndex = Math.Clamp(index, 0, DrawPalette.Sizes.Count - 1) };

        private DrawOnScreenState ResumeDrawing() =>
            ClickThrough || Hidden || !IsDrawingTool(Tool) ? PickTool(ToolType.Brush) : this;

        /// <summary>Whether the canvases are taking the mouse: the ink is up and not passed through.</summary>
        public bool IsDrawing => !ClickThrough && !Hidden;

        /// <summary>Whether the session's hotkeys are live: while the session has the keyboard
        /// (<paramref name="focused"/>: the toolbar or a canvas window), or a tool is in hand. The two
        /// part only for a moment: a session that loses the keyboard stops drawing. A frozen session is
        /// always drawing, so there only the keyboard counts: the strip never claims keys it lacks.</summary>
        public bool AreHotkeysLive(bool focused) => focused || (IsDrawing && !Frozen);

        /// <summary>Whether <paramref name="tool"/>'s button shows as active: picked, and actually drawing.</summary>
        public bool IsToolLit(ToolType tool) => Tool == tool && IsDrawing;
    }
}

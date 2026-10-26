using System;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Clowd.Drawing.Graphics;
using Clowd.Drawing.Rendering;
using Xunit;

namespace Clowd.Drawing.Tests
{
    /// <summary>
    /// Pins the drag-end cached-bounds re-round (ToolPointer.OnMouseUp, Move mode): body-move
    /// deltas are root-space doubles, so <c>TranslateCachedBounds</c> leaves the cached Bounds at a
    /// fractional offset of the pre-drag ROUNDED bounds. The old build recomputed rounded bounds on
    /// every read, so a document's export offset stayed integral; the rebuild must restore that at
    /// rest by clearing the Bounds aspect on the moved graphics at drag end and re-arming the
    /// validator (RequestValidation now also flags <c>_boundsDirty</c> so the cleared per-graphic
    /// caches propagate into ContentBounds).
    /// </summary>
    public class DragEndBoundsTests
    {
        static DragEndBoundsTests()
        {
            Clowd.Config.SettingsRoot.Current ??= new Clowd.Config.SettingsRoot();
        }

        [AvaloniaFact]
        public void MoveDragEnd_ReRoundsCachedBounds_AndContentBoundsIsIntegral()
        {
            var canvas = new DrawingCanvas { Tool = ToolType.None };
            var rect = new GraphicRectangle(Colors.Red, 2, new Rect(10, 10, 50, 40));
            canvas.GraphicsList.Add(rect);
            canvas.AddCommandToHistory(false);

            _ = rect.Bounds; // warm the cached bounds so Move takes the TranslateCachedBounds fast path

            // fractional body-move, as any drag at zoom/DPI != 1 produces
            rect.Move(0.37, 0.37);

            // sanity: without the drag-end clears the cached bounds sit at a fractional offset
            Assert.NotEqual(0, rect.Bounds.Left - Math.Floor(rect.Bounds.Left));

            // the drag-end clears exactly as ToolPointer.OnMouseUp performs them for Move mode
            foreach (var g in canvas.GraphicsList.SelectedItems)
                g.RenderCache.Clear(InvalidationAspects.Bounds);
            rect.RenderCache.Clear(InvalidationAspects.Bounds); // rect may not be selected in this synthetic drag
            canvas.GraphicsList.RequestValidation();
            canvas.AddCommandToHistory(false);

            // cached bounds must equal a cold rounded recompute at the final position
            var expected = HelperFunctions.CreateRectSafeRounded(rect.Left, rect.Top, rect.Right, rect.Bottom);
            Assert.Equal(expected, rect.Bounds);

            // and the union propagates (RequestValidation arms _boundsDirty): integral ContentBounds
            var cb = canvas.GraphicsList.ContentBounds;
            Assert.Equal(Math.Round(cb.Left), cb.Left);
            Assert.Equal(Math.Round(cb.Top), cb.Top);
            Assert.Equal(Math.Round(cb.Width), cb.Width);
            Assert.Equal(Math.Round(cb.Height), cb.Height);
        }
    }
}

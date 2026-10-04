using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Clowd.Drawing.Graphics;
using Clowd.Drawing.Tools;
using Xunit;

namespace Clowd.Drawing.Tests
{
    /// <summary>
    /// The marquee rule the pointer and the eraser share (<see cref="ToolPointer.GraphicsInMarquee"/>):
    /// full containment, list order, hidden and locked graphics and the marquee itself skipped.
    /// </summary>
    public class MarqueeRuleTests
    {
        static MarqueeRuleTests()
        {
            Clowd.Config.SettingsRoot.Current ??= new Clowd.Config.SettingsRoot();
        }

        private static GraphicRectangle Rect(double x, double y, double w, double h) =>
            new GraphicRectangle(Colors.Red, 2, new Rect(x, y, w, h));

        [AvaloniaFact]
        public void FullyContained_IsIn_PartiallyOverlapping_IsNot()
        {
            var canvas = new DrawingCanvas { Tool = ToolType.None };
            var inside = Rect(10, 10, 30, 30);
            var straddling = Rect(80, 80, 50, 50); // overlaps the marquee's corner
            var outside = Rect(300, 300, 10, 10);
            var touching = Rect(0, 0, 100, 100); // exactly the marquee: contained (Rect.Contains is inclusive)
            canvas.GraphicsList.Add(outside);
            canvas.GraphicsList.Add(inside);
            canvas.GraphicsList.Add(straddling);
            canvas.GraphicsList.Add(touching);

            var hits = ToolPointer.GraphicsInMarquee(canvas.GraphicsList, new Rect(0, 0, 100, 100));

            // list order, not hit order
            Assert.Equal(new GraphicBase[] { inside, touching }, hits);
        }

        [AvaloniaFact]
        public void HiddenAndLocked_AreSkipped_AndSoIsTheMarqueeItself()
        {
            var canvas = new DrawingCanvas { Tool = ToolType.None };
            var plain = Rect(10, 10, 20, 20);
            var hidden = Rect(40, 10, 20, 20);
            var locked = Rect(10, 40, 20, 20);
            hidden.Hidden = true;
            locked.Locked = true;
            canvas.GraphicsList.Add(plain);
            canvas.GraphicsList.Add(hidden);
            canvas.GraphicsList.Add(locked);
            canvas.GraphicsList.Add(new GraphicSelectionRectangle(new Rect(0, 0, 5, 5)));

            var hits = ToolPointer.GraphicsInMarquee(canvas.GraphicsList, new Rect(0, 0, 100, 100));

            Assert.Equal(new GraphicBase[] { plain }, hits);
        }

        [AvaloniaFact]
        public void EmptyMarquee_SelectsNothing()
        {
            var canvas = new DrawingCanvas { Tool = ToolType.None };
            canvas.GraphicsList.Add(Rect(10, 10, 20, 20));

            Assert.Empty(ToolPointer.GraphicsInMarquee(canvas.GraphicsList, new Rect(500, 500, 10, 10)));
        }
    }
}

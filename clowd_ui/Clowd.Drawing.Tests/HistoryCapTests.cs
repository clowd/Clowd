using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Clowd.Drawing.Graphics;
using Xunit;

namespace Clowd.Drawing.Tests
{
    /// <summary>
    /// Pins the 200-step history cap (final-design §B.5): the engine keeps at most 200 delta steps,
    /// dropping the OLDEST (each delta carries before+after, so dropping the oldest merely shortens
    /// how far undo reaches — the original state becomes unreachable). When a step is dropped, the
    /// retained live instances it referenced (deleted graphics kept for undo-of-delete) get their
    /// heavy transient caches trimmed, so history memory stays bounded.
    /// </summary>
    public class HistoryCapTests
    {
        static HistoryCapTests()
        {
            Clowd.Config.SettingsRoot.Current ??= new Clowd.Config.SettingsRoot();
        }

        private static readonly FieldInfo ImgSource =
            typeof(GraphicImage).GetField("_imageSource", BindingFlags.Instance | BindingFlags.NonPublic);

        [AvaloniaFact]
        public void History_CapsAtTwoHundredSteps_DroppingOldest()
        {
            var canvas = new DrawingCanvas { Tool = ToolType.None };
            var rect = new GraphicRectangle(Colors.Red, 2, new Rect(0, 0, 10, 10));
            canvas.GraphicsList.Add(rect);
            canvas.AddCommandToHistory(false); // baseline: LineWidth == 2

            // 250 distinct, non-mergable steps: LineWidth walks 3 → 252
            for (int i = 3; i <= 252; i++)
            {
                rect.LineWidth = i;
                canvas.AddCommandToHistory(false);
            }

            int undos = 0;
            while (canvas.CommandUndo.CanExecute(null))
            {
                canvas.Undo();
                undos++;
            }

            Assert.Equal(200, undos);                 // exactly the cap is reachable
            Assert.Equal(252 - 200, rect.LineWidth);  // drop-oldest: the LineWidth==2 origin is gone
            Assert.False(canvas.CommandUndo.CanExecute(null));
        }

        [AvaloniaFact]
        public void DroppingAStep_TrimsTheRetainedInstanceCaches()
        {
            var dir = Directory.CreateTempSubdirectory();
            try
            {
                var path = Path.Combine(dir.FullName, "img.png");
                WritePng(path, 40, 30);

                var canvas = new DrawingCanvas { Tool = ToolType.None };
                var rect = new GraphicRectangle(Colors.Red, 2, new Rect(0, 0, 10, 10));
                var img = new GraphicImage(path, new Rect(0, 0, 40, 30), default);
                canvas.GraphicsList.Add(rect);
                canvas.GraphicsList.Add(img);
                canvas.AddCommandToHistory(false); // baseline

                // delete the image — the step retains the live instance so undo-of-delete is a
                // list insert with caches intact
                img.IsSelected = true;
                canvas.Delete();
                Assert.DoesNotContain(img, canvas.GraphicsList);

                // re-warm the retained instance's decoded bitmap so the later trim is observable
                Assert.NotNull(img.ImageSource);
                Assert.NotNull(ImgSource.GetValue(img));

                // push well past the cap so the step(s) retaining the image drop off the oldest end
                for (int i = 0; i < 210; i++)
                {
                    rect.LineWidth = 3 + i;
                    canvas.AddCommandToHistory(false);
                }

                // the dropped step trimmed the retained instance's heavy transient caches
                Assert.Null(ImgSource.GetValue(img));
                Assert.DoesNotContain(img, canvas.GraphicsList);
            }
            finally
            {
                dir.Delete(true);
            }
        }

        private static void WritePng(string path, int width, int height)
        {
            using var wb = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96),
                                               PixelFormats.Bgra8888, AlphaFormat.Premul);
            wb.Save(path);
        }
    }
}

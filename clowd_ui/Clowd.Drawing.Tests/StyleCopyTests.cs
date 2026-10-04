using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Clowd.Config;
using Clowd.Drawing.Graphics;
using Xunit;

namespace Clowd.Drawing.Tests
{
    /// <summary>
    /// The style menu's canvas half: which selection counts as a style subject, copying a style
    /// to graphics of the same type, and writing it into a tool's settings.
    /// </summary>
    public class StyleCopyTests
    {
        static StyleCopyTests()
        {
            SettingsRoot.Current ??= new SettingsRoot();
        }

        private static DrawingCanvas MakeCanvas(params GraphicBase[] graphics)
        {
            var canvas = new DrawingCanvas();
            canvas.Tool = ToolType.Pointer;
            foreach (var g in graphics)
                canvas.GraphicsList.Add(g);
            canvas.AddCommandToHistory(false); // the step undo goes back to
            return canvas;
        }

        private static void Select(DrawingCanvas canvas, params GraphicBase[] graphics)
        {
            canvas.UnselectAll();
            foreach (var g in graphics)
                g.IsSelected = true;
            canvas.ResyncToolSettings();
        }

        [AvaloniaFact]
        public void OnlyASingleStyledSelection_IsAStyleSubject()
        {
            var a = new GraphicArrow(Colors.Red, 3, new Point(0, 0), new Point(50, 0));
            var b = new GraphicArrow(Colors.Red, 3, new Point(0, 10), new Point(50, 10));
            var canvas = MakeCanvas(a, b);

            Assert.False(canvas.HasStyleSubject);

            Select(canvas, a);
            Assert.True(canvas.HasStyleSubject);
            Assert.Equal(ToolType.Arrow, canvas.StyleSubjectTool);

            Select(canvas, a, b);
            Assert.False(canvas.HasStyleSubject);
            Assert.Null(canvas.StyleSubjectTool);
        }

        [AvaloniaFact]
        public void CopyStyleToSimilar_ReachesSameTypeOnly_AsOneUndoStep()
        {
            var source = new GraphicArrow(Colors.Lime, 9, new Point(0, 0), new Point(50, 0)) { DashStyle = LineDashStyle.Dashed };
            var arrow = new GraphicArrow(Colors.Red, 3, new Point(0, 10), new Point(50, 10));
            var line = new GraphicLine(Colors.Red, 3, new Point(0, 20), new Point(50, 20));
            var canvas = MakeCanvas(source, arrow, line);
            Select(canvas, source);

            Assert.Equal(1, canvas.CopyStyleToSimilar());

            Assert.Equal(Colors.Lime, arrow.ObjectColor);
            Assert.Equal(9, arrow.LineWidth);
            Assert.Equal(LineDashStyle.Dashed, arrow.DashStyle);
            // an arrow is a line, but not the same kind of graphic
            Assert.Equal(Colors.Red, line.ObjectColor);
            Assert.Equal(3, line.LineWidth);

            canvas.Undo();
            var restored = Assert.IsType<GraphicArrow>(canvas.GraphicsList[1]);
            Assert.Equal(Colors.Red, restored.ObjectColor);
            Assert.Equal(3, restored.LineWidth);
        }

        [AvaloniaFact]
        public void CopyStyleToSettings_WritesTheTextStyle_AndTurnsAutoFillOff()
        {
            var text = new GraphicText(Colors.Navy, 2, new Point(10, 10), 0, "hi")
            {
                Foreground = Colors.White,
                FontSize = 31,
                FontWeight = FontWeight.Bold,
            };
            var canvas = MakeCanvas(text);
            Select(canvas, text);

            var settings = new SavedToolSettings { AutoFill = true, BlurRadius = 5 };
            Assert.True(canvas.CopyStyleToSettings(settings));

            Assert.Equal(Colors.White, settings.ObjectColor);
            Assert.Equal(Colors.Navy, settings.FillColor);
            Assert.Equal(31, settings.FontSize);
            Assert.Equal(FontWeight.Bold, settings.FontWeight);
            Assert.False(settings.AutoFill);
            // not part of a text's style: left as it was
            Assert.Equal(5, settings.BlurRadius);
        }

        [AvaloniaFact]
        public void SessionResolver_SupersedesTheSavedSettings_ForTheToolsItHolds()
        {
            var session = new System.Collections.Generic.Dictionary<ToolType, SavedToolSettings>
            {
                [ToolType.Arrow] = new SavedToolSettings { LineWidth = 17 },
            };
            var canvas = new DrawingCanvas
            {
                ToolSettingsResolver = t => session.TryGetValue(t, out var s) ? s : SettingsRoot.Current.Editor.GetToolSettings(t),
            };

            canvas.Tool = ToolType.Arrow;
            Assert.Equal(17, canvas.LineWidth);

            canvas.Tool = ToolType.Line;
            Assert.Equal(SettingsRoot.Current.Editor.GetToolSettings(ToolType.Line).LineWidth, canvas.LineWidth);
        }

        [AvaloniaFact]
        public void Clone_IsDetached_AndKeepsUnsetValuesUnset()
        {
            var original = SavedToolSettings.CreateDefault(ToolType.Rectangle);
            original.LineWidth = 8;
            var raised = false;
            original.PropertyChanged += (_, _) => raised = true;

            var copy = original.Clone();
            copy.LineWidth = 2;

            Assert.False(raised);
            Assert.Equal(8, original.LineWidth);
            Assert.Equal(SavedToolSettings.DefaultRectangleCornerRadius, copy.CornerRadius);
            Assert.Equal(original.ObjectColor, copy.ObjectColor);
        }
    }
}

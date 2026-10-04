using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Clowd.Config;
using Clowd.Drawing.Graphics;
using Clowd.Drawing.Tools;
using Xunit;

namespace Clowd.Drawing.Tests
{
    /// <summary>
    /// The draw-on-screen gates on <see cref="DrawingCanvas.IsOverlayMode"/>, each checked
    /// against the editor's default so the flag is proven to be the only thing that changes
    /// behaviour: sticky tools, no zoom or pan, the settings resolver, the HistoryAppended seam,
    /// DeleteGraphics, and the text-edit and right-press gates in the pointer handler.
    /// </summary>
    public class OverlayModeTests
    {
        static OverlayModeTests()
        {
            SettingsRoot.Current ??= new SettingsRoot();
        }

        private static IPointer MakePointer() => new Pointer(1, PointerType.Mouse, true);

        private static PointerState Down(Point p, IPointer pointer) =>
            new PointerState(p, KeyModifiers.None, true, false, false, pointer, 0, null);

        private static PointerState Move(Point p, IPointer pointer) =>
            new PointerState(p, KeyModifiers.None, true, false, false, pointer, 0, null);

        private static PointerState Up(Point p, IPointer pointer) =>
            new PointerState(p, KeyModifiers.None, false, false, false, pointer, 0, null);

        private static GraphicRectangle Rect(double x, double y, double w, double h) =>
            new GraphicRectangle(Colors.Red, 2, new Rect(x, y, w, h));

        private static int CountSteps(Action action)
        {
            int steps = 0;
            Action<UndoManager, SortedSet<string>> hook = (_, changes) => { if (changes.Count > 0) steps++; };
            UndoManager.DiagnosticCommitBuilt += hook;
            try
            {
                action();
            }
            finally
            {
                UndoManager.DiagnosticCommitBuilt -= hook;
            }

            return steps;
        }

        // ====================================================================
        // Routed pointer events, raised the way the input manager would — from the clickable
        // surface (Children[0], the hit-test source in the app) with root-relative positions, so
        // the DrawingCanvas.OnPointer* handlers and the focus-on-press tunnel are under test, not
        // just the tools. Positions only resolve inside a shown window (see Host).
        // ====================================================================

        private static Window Host(DrawingCanvas canvas)
        {
            var window = new Window { Width = 800, Height = 600, Content = canvas };
            window.Show();
            return window;
        }

        private static Visual RootOf(Visual v) => (Visual)TopLevel.GetTopLevel(v) ?? v;

        private static Point ToRoot(Visual v, Point p) => v.TranslatePoint(p, RootOf(v)) ?? p;

        private static Interactive SurfaceOf(DrawingCanvas canvas) => canvas.Children[0];

        private static PointerPressedEventArgs Press(DrawingCanvas canvas, IPointer pointer, Point p, MouseButton button, int clickCount = 1)
        {
            var props = button == MouseButton.Right
                ? new PointerPointProperties(RawInputModifiers.RightMouseButton, PointerUpdateKind.RightButtonPressed)
                : new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);
            var source = SurfaceOf(canvas);
            var e = new PointerPressedEventArgs(source, pointer, RootOf(canvas), ToRoot(canvas, p), 0, props, KeyModifiers.None, clickCount);
            source.RaiseEvent(e);
            return e;
        }

        private static void Release(DrawingCanvas canvas, IPointer pointer, Point p)
        {
            var props = new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased);
            var source = SurfaceOf(canvas);
            source.RaiseEvent(new PointerReleasedEventArgs(source, pointer, RootOf(canvas), ToRoot(canvas, p), 0, props, KeyModifiers.None, MouseButton.Left));
        }

        private static void Wheel(DrawingCanvas canvas, IPointer pointer, Point p, double delta)
        {
            var props = new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other);
            var source = SurfaceOf(canvas);
            source.RaiseEvent(new PointerWheelEventArgs(source, pointer, RootOf(canvas), ToRoot(canvas, p), 0, props, KeyModifiers.None, new Vector(0, delta)));
        }

        // ====================================================================
        // Sticky tools
        // ====================================================================

        [AvaloniaTheory]
        [InlineData(false)]
        [InlineData(true)]
        public void Brush_StaysActiveWithNothingSelected_OnlyInOverlayMode(bool overlay)
        {
            var canvas = new DrawingCanvas { IsOverlayMode = overlay, Tool = ToolType.Brush };
            var pointer = MakePointer();
            var tool = canvas.ToolBrush;

            var steps = CountSteps(() =>
            {
                tool.OnMouseDown(canvas, Down(new Point(10, 10), pointer), 1);
                tool.OnMouseMove(canvas, Move(new Point(40, 20), pointer));
                tool.OnMouseUp(canvas, Up(new Point(40, 20), pointer));
            });

            Assert.Equal(1, steps);
            var stroke = Assert.IsType<GraphicBrush>(Assert.Single(canvas.GraphicsList));
            Assert.False(canvas.IsMouseCaptured);

            if (overlay)
            {
                Assert.Equal(ToolType.Brush, canvas.Tool);
                Assert.False(stroke.IsSelected);
                Assert.Equal(0, canvas.SelectedCount);
            }
            else
            {
                Assert.Equal(ToolType.Pointer, canvas.Tool);
                Assert.True(stroke.IsSelected);
            }
        }

        [AvaloniaTheory]
        [InlineData(false)]
        [InlineData(true)]
        public void Rectangle_StaysActiveWithNothingSelected_OnlyInOverlayMode(bool overlay)
        {
            var canvas = new DrawingCanvas { IsOverlayMode = overlay, Tool = ToolType.Rectangle };
            var window = Host(canvas);
            var pointer = MakePointer();

            var steps = CountSteps(() =>
            {
                Press(canvas, pointer, new Point(10, 10), MouseButton.Left);
                Assert.True(canvas.IsToolDragActive);
                Release(canvas, pointer, new Point(60, 50));
            });

            Assert.Equal(1, steps);
            var rect = Assert.IsType<GraphicRectangle>(Assert.Single(canvas.GraphicsList));
            Assert.Equal(new Rect(10, 10, 50, 40), rect.UnrotatedBounds); // overlay or not, canvas units are DIPs here
            Assert.False(canvas.IsToolDragActive);

            if (overlay)
            {
                Assert.Equal(ToolType.Rectangle, canvas.Tool);
                Assert.False(rect.IsSelected);
                Assert.Same(CursorResources.Rect, canvas.Cursor);
            }
            else
            {
                Assert.Equal(ToolType.Pointer, canvas.Tool);
                Assert.True(rect.IsSelected);
            }

            window.Close();
        }

        [AvaloniaTheory]
        [InlineData(false)]
        [InlineData(true)]
        public void CancelCurrentOperation_KeepsTheTool_OnlyInOverlayMode(bool overlay)
        {
            var canvas = new DrawingCanvas { IsOverlayMode = overlay, Tool = ToolType.Brush };
            var pointer = MakePointer();
            canvas.ToolBrush.OnMouseDown(canvas, Down(new Point(10, 10), pointer), 1);
            Assert.Single(canvas.GraphicsList);

            canvas.CancelCurrentOperation();

            Assert.Empty(canvas.GraphicsList); // the half-drawn stroke is aborted either way
            Assert.False(canvas.IsMouseCaptured);
            Assert.Equal(overlay ? ToolType.Brush : ToolType.Pointer, canvas.Tool);
        }

        [AvaloniaFact]
        public void Eraser_InTheEditor_AlsoStaysActive_AndADoubleClickIsTwoClicks()
        {
            // the eraser is overlay-only in practice, but its gates must not depend on the mode
            var canvas = new DrawingCanvas { Tool = ToolType.Eraser };
            var window = Host(canvas);
            var a = Rect(10, 10, 30, 30);
            canvas.GraphicsList.Add(a);
            canvas.AddCommandToHistory(false);
            var pointer = MakePointer();

            Press(canvas, pointer, new Point(25, 25), MouseButton.Left, clickCount: 2);
            Release(canvas, pointer, new Point(25, 25));

            Assert.Empty(canvas.GraphicsList);
            Assert.Equal(ToolType.Eraser, canvas.Tool);

            window.Close();
        }

        // ====================================================================
        // Fixed 1:1 view
        // ====================================================================

        [AvaloniaTheory]
        [InlineData(false)]
        [InlineData(true)]
        public void Resize_KeepsContentOffsetAtZero_OnlyInOverlayMode(bool overlay)
        {
            var canvas = new DrawingCanvas { IsOverlayMode = overlay, Tool = ToolType.Brush };
            canvas.Measure(new Size(800, 600));
            canvas.Arrange(new Rect(0, 0, 800, 600));
            canvas.Measure(new Size(1200, 900));
            canvas.Arrange(new Rect(0, 0, 1200, 900));

            Assert.Equal(new Size(1200, 900), canvas.Bounds.Size);
            if (overlay)
            {
                Assert.Equal(default, canvas.ContentOffset);
                Assert.Equal(1, canvas.ContentScale); // DpiZoom is 1 while detached
                Assert.Equal(1, canvas.CanvasUiElementScale.DpiScaleX);
            }
            else
            {
                Assert.NotEqual(default, canvas.ContentOffset); // the editor recenters on resize
            }
        }

        [AvaloniaTheory]
        [InlineData(false)]
        [InlineData(true)]
        public void Wheel_DoesNotZoom_OnlyInOverlayMode(bool overlay)
        {
            var canvas = new DrawingCanvas { IsOverlayMode = overlay, Tool = ToolType.Brush };
            canvas.Measure(new Size(800, 600));
            canvas.Arrange(new Rect(0, 0, 800, 600));
            var before = canvas.ContentScale;

            Wheel(canvas, MakePointer(), new Point(100, 100), 1);

            if (overlay)
                Assert.Equal(before, canvas.ContentScale);
            else
                Assert.True(canvas.ContentScale > before);
        }

        // ====================================================================
        // Settings resolver
        // ====================================================================

        [AvaloniaFact]
        public void ToolSettingsResolver_IsUsed_AndTheEditorSettingsAreUntouched()
        {
            var editorTools = SettingsRoot.Current.Editor.Tools;
            var editorBefore = editorTools.ToDictionary(kv => kv.Key, kv => (kv.Value.ObjectColor, kv.Value.LineWidth));

            var session = new Dictionary<ToolType, SavedToolSettings>();
            var canvas = new DrawingCanvas
            {
                IsOverlayMode = true,
                ToolSettingsResolver = t => session.TryGetValue(t, out var s) ? s : session[t] = new SavedToolSettings(),
            };
            canvas.Tool = ToolType.Rectangle;

            Assert.True(session.ContainsKey(ToolType.Rectangle));
            session[ToolType.Rectangle].LineWidth = 28;
            session[ToolType.Rectangle].ObjectColor = Colors.Lime;
            Assert.Equal(28, canvas.LineWidth);
            Assert.Equal(Colors.Lime, canvas.ObjectColor);

            // a change made through the canvas lands in the session's settings...
            canvas.LineWidth = 12;
            Assert.Equal(12, session[ToolType.Rectangle].LineWidth);

            // ...and nothing in the editor's dictionary moved
            Assert.Equal(editorBefore.Count, editorTools.Count);
            foreach (var kv in editorBefore)
                Assert.Equal(kv.Value, (editorTools[kv.Key].ObjectColor, editorTools[kv.Key].LineWidth));

            // the eraser asks the resolver for nothing
            canvas.Tool = ToolType.Eraser;
            Assert.False(session.ContainsKey(ToolType.Eraser));
        }

        // ====================================================================
        // History seams
        // ====================================================================

        [AvaloniaFact]
        public void HistoryAppended_FiresOncePerAppend_NotOnUndoRedoOrEmptySteps()
        {
            var canvas = new DrawingCanvas { IsOverlayMode = true, Tool = ToolType.Brush };
            int appended = 0;
            canvas.HistoryAppended += (_, _) => appended++;

            canvas.GraphicsList.Add(Rect(10, 10, 30, 30));
            canvas.AddCommandToHistory(false);
            Assert.Equal(1, appended);

            canvas.AddCommandToHistory(false); // nothing changed: no step, no raise
            Assert.Equal(1, appended);

            canvas.GraphicsList.Add(Rect(50, 50, 30, 30));
            canvas.AddCommandToHistory(false);
            Assert.Equal(2, appended);

            canvas.Undo();
            canvas.Undo();
            Assert.Equal(2, appended);
            canvas.Redo();
            Assert.Equal(2, appended);

            // a merge into the current step is not an append either
            var g = (GraphicRectangle)canvas.GraphicsList[0];
            g.Angle = 10;
            canvas.AddCommandToHistory(true);
            Assert.Equal(3, appended);
            g.Angle = 20;
            canvas.AddCommandToHistory(true);
            Assert.Equal(3, appended);
        }

        [AvaloniaFact]
        public void DeleteGraphics_IsOneStep_AndLeavesTheSelectionAlone()
        {
            var canvas = new DrawingCanvas { Tool = ToolType.None };
            var a = Rect(10, 10, 30, 30);
            var b = Rect(50, 50, 30, 30);
            var c = Rect(90, 90, 30, 30);
            canvas.GraphicsList.Add(a);
            canvas.GraphicsList.Add(b);
            canvas.GraphicsList.Add(c);
            canvas.AddCommandToHistory(false);
            c.IsSelected = true;

            bool removed = false;
            var steps = CountSteps(() => removed = canvas.DeleteGraphics(new[] { a, b }));

            Assert.True(removed);
            Assert.Equal(1, steps);
            Assert.Same(c, Assert.Single(canvas.GraphicsList));
            Assert.True(c.IsSelected);
            Assert.Equal(1, canvas.SelectedCount);

            // graphics no longer in the list: nothing to do, no step
            steps = CountSteps(() => removed = canvas.DeleteGraphics(new[] { a }));
            Assert.False(removed);
            Assert.Equal(0, steps);

            canvas.Undo();
            Assert.Equal(new GraphicBase[] { a, b, c }, canvas.GraphicsList);
        }

        [AvaloniaFact]
        public void Delete_StillRemovesTheSelection_InOneStep()
        {
            var canvas = new DrawingCanvas { Tool = ToolType.None };
            var a = Rect(10, 10, 30, 30);
            var b = Rect(50, 50, 30, 30);
            canvas.GraphicsList.Add(a);
            canvas.GraphicsList.Add(b);
            canvas.AddCommandToHistory(false);
            a.IsSelected = true;

            var steps = CountSteps(() => canvas.Delete());

            Assert.Equal(1, steps);
            Assert.Same(b, Assert.Single(canvas.GraphicsList));
        }

        // ====================================================================
        // Pointer-handler gates
        // ====================================================================

        [AvaloniaFact]
        public void PressWhileTextEditing_OnlyCommitsTheText_InOverlayMode()
        {
            var canvas = new DrawingCanvas { IsOverlayMode = true, Tool = ToolType.Text };
            var window = Host(canvas);

            int editingChanges = 0;
            canvas.TextEditingChanged += (_, _) => editingChanges++;

            var text = new GraphicText(canvas, new Point(100, 100)) { Body = "hello" };
            canvas.GraphicsList.Add(text);
            canvas.AddCommandToHistory(false);
            canvas.ToolText.CreateTextBox(text, canvas, false);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs(); // the posted TextBox.Focus()

            Assert.True(canvas.IsTextEditing);
            Assert.Equal(1, editingChanges);
            Assert.Single(canvas.Children.OfType<TextBox>());

            var e = Press(canvas, MakePointer(), new Point(400, 400), MouseButton.Left);

            Assert.True(e.Handled);
            Assert.False(canvas.IsTextEditing);
            Assert.Equal(2, editingChanges);
            Assert.Empty(canvas.Children.OfType<TextBox>());
            Assert.Same(text, Assert.Single(canvas.GraphicsList)); // no new text box was started
            Assert.False(text.IsSelected); // the overlay leaves nothing selected
            Assert.False(canvas.IsToolDragActive);
            Assert.Equal(ToolType.Text, canvas.Tool);

            window.Close();
        }

        [AvaloniaFact]
        public void CommitTextEdit_EndsTheEdit_AndIsInertOtherwise()
        {
            var canvas = new DrawingCanvas { IsOverlayMode = true, Tool = ToolType.Text };
            var window = Host(canvas);

            canvas.CommitTextEdit(); // nothing open: no-op
            Assert.False(canvas.IsTextEditing);

            var text = new GraphicText(canvas, new Point(100, 100)) { Body = "hello" };
            canvas.GraphicsList.Add(text);
            canvas.ToolText.CreateTextBox(text, canvas, false);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.True(canvas.IsTextEditing);

            canvas.CommitTextEdit();

            Assert.False(canvas.IsTextEditing);
            Assert.Empty(canvas.Children.OfType<TextBox>());
            Assert.Equal("hello", text.Body);

            window.Close();
        }

        [AvaloniaTheory]
        [InlineData(false)]
        [InlineData(true)]
        public void RightPress_LeavesTheToolAlone_OnlyInOverlayMode(bool overlay)
        {
            var canvas = new DrawingCanvas { IsOverlayMode = overlay, Tool = ToolType.Brush };
            var window = Host(canvas);
            var a = Rect(10, 10, 30, 30);
            canvas.GraphicsList.Add(a);

            Press(canvas, MakePointer(), new Point(25, 25), MouseButton.Right);

            if (overlay)
            {
                Assert.Equal(ToolType.Brush, canvas.Tool);
                Assert.False(a.IsSelected);
            }
            else
            {
                Assert.Equal(ToolType.Pointer, canvas.Tool);
                Assert.True(a.IsSelected); // the editor selects what the context menu is about
            }

            window.Close();
        }

        [AvaloniaFact]
        public void OverlayMode_IsOffByDefault_AndDropsTheContextMenu()
        {
            var editor = new DrawingCanvas();
            Assert.False(editor.IsOverlayMode);
            Assert.NotNull(editor.ContextMenu);

            var overlay = new DrawingCanvas { IsOverlayMode = true };
            Assert.Null(overlay.ContextMenu);
            Assert.Equal(default, overlay.ContentOffset);
        }
    }
}

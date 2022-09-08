using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Clowd.Drawing.Graphics;

namespace Clowd.Drawing.Tools
{
    internal class ToolText : ToolBase
    {
        internal override ToolActionType ActionType => ToolActionType.Object;

        public string OldText { get; private set; }
        public TextBox TextBox { get; private set; }

        GraphicText editedGraphicsText;
        DrawingCanvas drawingCanvas;

        public ToolText(DrawingCanvas drawingCanvas) : base(Resource.CursorText)
        {
            this.drawingCanvas = drawingCanvas;
        }

        public override void OnMouseDown(DrawingCanvas drawingCanvas, MouseButtonEventArgs e)
        {
            Point point = e.GetPosition(drawingCanvas);
            var o = new GraphicText(drawingCanvas, point);
            drawingCanvas.UnselectAll();
            o.IsSelected = true;
            drawingCanvas.GraphicsList.Add(o);
            drawingCanvas.CaptureMouse();
        }

        public override void OnMouseUp(DrawingCanvas drawingCanvas, MouseButtonEventArgs e)
        {
            base.OnMouseUp(drawingCanvas, e);
            if (drawingCanvas.Count > 0)
            {
                drawingCanvas[drawingCanvas.Count - 1].Normalize();
                GraphicText t = drawingCanvas[drawingCanvas.Count - 1] as GraphicText;
                if (t != null)
                {
                    // Create textbox for editing of graphics object which is just created
                    CreateTextBox(t, drawingCanvas, true);
                }
            }

            // Commnnd will be added to History later, after closing
            // in-place textbox.
        }

        public override void OnMouseMove(DrawingCanvas drawingCanvas, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                if (drawingCanvas.IsMouseCaptured)
                {
                    if (drawingCanvas.Count > 0)
                    {
                        Point point = e.GetPosition(drawingCanvas);
                        var gr = drawingCanvas[drawingCanvas.Count - 1] as GraphicText;
                        if (gr != null)
                        {
                            gr.Left = point.X;
                            gr.Top = point.Y;
                            gr.Normalize();
                        }
                    }
                }
            }
        }

        public void CreateTextBox(GraphicText graphicsText, DrawingCanvas drawingCanvas, bool newGraphic = false)
        {
            graphicsText.Editing = true;

            // Keep old text in the case Esc is pressed while editing
            OldText = graphicsText.Body;

            // Keep reference to edited object
            editedGraphicsText = graphicsText;

            TextBox = new TextBox();
            TextBox.FontFamily = new FontFamily(graphicsText.FontName);
            TextBox.FontSize = graphicsText.FontSize;
            TextBox.FontStretch = graphicsText.FontStretch;
            TextBox.FontStyle = graphicsText.FontStyle;
            TextBox.FontWeight = graphicsText.FontWeight;
            TextBox.Width = Double.NaN;
            TextBox.Height = Double.NaN;
            TextBox.Background = Brushes.Transparent;
            TextBox.Text = graphicsText.Body;
            TextBox.BorderThickness = new Thickness(0, 0, 0, 0);
            TextBox.BorderBrush = Brushes.Transparent;
            TextBox.Tag = graphicsText;
            TextBox.Style = null;
            TextBox.AcceptsReturn = true;

            var finalTransform = new TransformGroup();
            finalTransform.Children.Add(new TranslateTransform(GraphicText.TextPadding - 2, GraphicText.TextPadding));
            finalTransform.Children.Add(new RotateTransform(graphicsText.Angle, (graphicsText.Right - graphicsText.Left) / 2,
                (graphicsText.Bottom - graphicsText.Top) / 2));
            TextBox.RenderTransform = finalTransform;

            if (newGraphic)
            {
                TextBox.Text = graphicsText.Body;
                TextBox.SelectAll();
                OldText = "";
            }
            else
            {
                OldText = graphicsText.Body;
                TextBox.CaretIndex = int.MaxValue;
            }

            drawingCanvas.Children.Add(TextBox);

            Canvas.SetLeft(TextBox, graphicsText.Left);
            Canvas.SetTop(TextBox, graphicsText.Top);

            TextBox.Focus();

            TextBox.LostFocus += (_, _) => HideTextbox(editedGraphicsText, drawingCanvas);
            TextBox.LostKeyboardFocus += (_, _) => HideTextbox(editedGraphicsText, drawingCanvas);
            TextBox.PreviewKeyDown += new KeyEventHandler(textBox_PreviewKeyDown);
            TextBox.TextChanged += textBox_TextChanged;

            // Notes:
            // TextBox context menu is set to null
            // The reason I did this is the following:
            // I must hide textbox when user clicks anywhere
            // outside of textbox, outside of this program window,
            // or any other window pops up and steals focus.
            // The only function which works for all these cases for 100%
            // is LostKeyboardFocus handler. However, LostKeyboardFocus
            // is raised also when textbox context menu is shown, and this
            // breaks all logic. To keep things consistent, I don't allow
            // showing context menu.
            TextBox.ContextMenu = null;
        }

        private void HideTextbox(GraphicBase graphic, DrawingCanvas drawingCanvas)
        {
            if (TextBox == null)
                return;

            var graphicsText = graphic as GraphicText;
            if (graphicsText == null)
                return;

            graphicsText.Editing = false;

            if (TextBox.Text.Trim().Length == 0)
            {
                // Textbox is empty: remove text object.
                drawingCanvas.GraphicsList.Remove(graphic);

                if (!String.IsNullOrEmpty(OldText)) // existing text was edited
                    drawingCanvas.AddCommandToHistory();
            }
            else
            {
                if (!String.IsNullOrEmpty(OldText)) // existing text was edited
                {
                    if (TextBox.Text.Trim() != OldText) // text was really changed
                    {
                        // Make change in the text object
                        graphicsText.Body = TextBox.Text.Trim();

                        // Keep state after change and add command to the history
                        drawingCanvas.AddCommandToHistory();
                    }
                }
                else // new text was added
                {
                    // Make change in the text object
                    graphicsText.Body = TextBox.Text.Trim();

                    // Add command to the history
                    drawingCanvas.AddCommandToHistory();
                }
            }

            // Remove textbox and set it to null.
            drawingCanvas.Children.Remove(TextBox);
            TextBox = null;

            // This enables back all ApplicationCommands,
            // which are disabled while textbox is active.
            drawingCanvas.Focus();
        }

        private void textBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var grap = (sender as TextBox)?.Tag as GraphicText;
            if (grap == null)
                return;
            grap.Body = ((TextBox)sender).Text;
            drawingCanvas.RefreshBounds();
        }

        private void textBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                TextBox.Text = OldText;
                HideTextbox(editedGraphicsText, drawingCanvas);
                e.Handled = true;
            }

            // Enter without modifiers - Shift+Enter should be available.
            else if (e.Key == Key.Return && Keyboard.Modifiers == ModifierKeys.None)
            {
                HideTextbox(editedGraphicsText, drawingCanvas);
                e.Handled = true;
            }
        }
    }
}

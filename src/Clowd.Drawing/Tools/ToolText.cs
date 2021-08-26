using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.IO;
using Clowd.Drawing.Graphics;
using Clowd.Drawing.Commands;

namespace Clowd.Drawing.Tools
{
    internal class ToolText : ToolBase
    {
        internal override ToolActionType ActionType => ToolActionType.Object;

        public string OldText { get; private set; }
        public TextBox TextBox { get; private set; }

        GraphicText editedGraphicsText;
        DrawingCanvas drawingCanvas;

        public ToolText(DrawingCanvas drawingCanvas)
            : base(new Cursor(new MemoryStream(Properties.Resources.Text)))
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
                        }
                    }
                }
            }
        }

        public void CreateTextBox(GraphicText graphicsText, DrawingCanvas drawingCanvas, bool newGraphic = false)
        {
            graphicsText.IsSelected = false;  // selection marks don't look good with textbox
            graphicsText.Editing = true;

            // Keep old text in the case Esc is pressed while editing
            OldText = graphicsText.Body;

            // Keep reference to edited object
            editedGraphicsText = graphicsText;

            TextBox = new TextBox();
            TextBox.RenderTransform = new RotateTransform(graphicsText.Angle, (graphicsText.Right - graphicsText.Left) / 2, (graphicsText.Bottom - graphicsText.Top) / 2);
            TextBox.FontFamily = new FontFamily(graphicsText.FontName);
            TextBox.FontSize = graphicsText.FontSize;
            TextBox.FontStretch = graphicsText.FontStretch;
            TextBox.FontStyle = graphicsText.FontStyle;
            TextBox.FontWeight = graphicsText.FontWeight;
            TextBox.Width = Double.NaN;
            TextBox.Height = Double.NaN;
            TextBox.Background = Brushes.Transparent;
            TextBox.Text = graphicsText.Body;
            TextBox.BorderThickness = new Thickness(0, 0, 0, 1);
            TextBox.BorderBrush = Brushes.Transparent;
            TextBox.Tag = graphicsText;
            TextBox.Style = null;

            if (newGraphic)
            {
                TextBox.Text = graphicsText.Body = "Start typing your note.\r\nUse Shift+Enter for new lines.";
                TextBox.SelectAll();
                OldText = "";
            }
            else
            {
                OldText = graphicsText.Body;
                TextBox.CaretIndex = int.MaxValue;
            }

            TextBox.AcceptsReturn = true;

            drawingCanvas.Children.Add(TextBox);

            Canvas.SetLeft(TextBox, graphicsText.Left + graphicsText.Padding);
            Canvas.SetTop(TextBox, graphicsText.Top + graphicsText.Padding);

            TextBox.Focus();

            TextBox.LostFocus += new RoutedEventHandler(textBox_LostFocus);
            TextBox.LostKeyboardFocus += new KeyboardFocusChangedEventHandler(textBox_LostKeyboardFocus);
            TextBox.PreviewKeyDown += new KeyEventHandler(textBox_PreviewKeyDown);
            TextBox.ContextMenu = null;     // see notes in textBox_LostKeyboardFocus
            TextBox.TextChanged += textBox_TextChanged;

            // Initially textbox is set to the same rectangle as graphicsText.
            // After textbox loading its template is available, and we can
            // correct textbox position - see details in the textBox_Loaded function.
            TextBox.Loaded += new RoutedEventHandler(textBox_Loaded);
        }

        private void HideTextbox(GraphicBase graphic, DrawingCanvas drawingCanvas)
        {
            if (TextBox == null)
            {
                return;
            }
            var graphicsText = graphic as GraphicText;
            if (graphicsText == null)
                return;

            graphicsText.IsSelected = true;   // restore selection which was removed for better textbox appearance
            graphicsText.Editing = false;

            if (TextBox.Text.Trim().Length == 0)
            {
                // Textbox is empty: remove text object.

                if (!String.IsNullOrEmpty(OldText))  // existing text was edited
                {
                    // Since text object is removed now,
                    // Add Delete command to the history
                    drawingCanvas.AddCommandToHistory(new CommandDelete(drawingCanvas));
                }

                // Remove empty text object
                drawingCanvas.GraphicsList.Remove(graphic);
            }
            else
            {
                if (!String.IsNullOrEmpty(OldText))  // existing text was edited
                {
                    if (TextBox.Text.Trim() != OldText)     // text was really changed
                    {
                        // Create command
                        CommandChangeState command = new CommandChangeState(drawingCanvas);

                        // Make change in the text object
                        graphicsText.Body = TextBox.Text.Trim();

                        // Keep state after change and add command to the history
                        command.NewState(drawingCanvas);
                        drawingCanvas.AddCommandToHistory(command);
                    }
                }
                else                                          // new text was added
                {
                    // Make change in the text object
                    graphicsText.Body = TextBox.Text.Trim();

                    // Add command to the history
                    drawingCanvas.AddCommandToHistory(new CommandAdd(graphic));
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
        }

        private void textBox_Loaded(object sender, RoutedEventArgs e)
        {
            double xOffset, yOffset;

            // compute text offset

            // Set hard-coded values initially
            xOffset = 5;
            yOffset = 3;

            try
            {
                var tb = TextBox;
                ContentControl cc = (ContentControl)tb.Template.FindName("PART_ContentHost", tb);

                /*
                // Way to see control template (Charles Petzold, Applications = Code + Markup, part 25).
                
                System.Xml.XmlWriterSettings settings = new System.Xml.XmlWriterSettings();
                settings.Indent = true;
                settings.IndentChars = new string(' ', 4);
                settings.NewLineOnAttributes = true;

                System.Text.StringBuilder strbuild = new System.Text.StringBuilder();
                System.Xml.XmlWriter xmlwrite = System.Xml.XmlWriter.Create(strbuild, settings);
                System.Windows.Markup.XamlWriter.Save(tb.Template, xmlwrite);
                string s = strbuild.ToString();
                System.Diagnostics.Trace.WriteLine(s);
                 */

                GeneralTransform tf = ((Visual)cc.Content).TransformToAncestor(tb);
                Point offset = tf.Transform(new Point(0, 0));

                xOffset = offset.X;
                yOffset = offset.Y;
            }
            catch (ArgumentException ex)
            {
                System.Diagnostics.Trace.WriteLine("ComputeTextOffset: " + ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                System.Diagnostics.Trace.WriteLine("ComputeTextOffset: " + ex.Message);
            }

            // end compute text offset


            Canvas.SetLeft(TextBox, Canvas.GetLeft(TextBox) - xOffset);
            Canvas.SetTop(TextBox, Canvas.GetTop(TextBox) - yOffset);
            TextBox.Width = TextBox.Width + xOffset + xOffset;
            TextBox.Height = TextBox.Height + yOffset + yOffset;
        }

        private void textBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                TextBox.Text = OldText;

                HideTextbox(editedGraphicsText, drawingCanvas);

                e.Handled = true;
                return;
            }

            // Enter without modifiers - Shift+Enter should be available.
            if (e.Key == Key.Return && Keyboard.Modifiers == ModifierKeys.None)
            {
                HideTextbox(editedGraphicsText, drawingCanvas);

                e.Handled = true;
                return;
            }

            // Handle A-Z here, so that keybindings to those keys will not be handled later on.
            if ((int)e.Key >= 44 && (int)e.Key <= 69)
            {
                var key = (char)KeyInterop.VirtualKeyFromKey(e.Key);
                string str = "";
                if (Keyboard.Modifiers == ModifierKeys.None)
                {
                    if (Console.CapsLock) str = key.ToString();
                    else str = key.ToString().ToLower();
                }
                else if (Keyboard.Modifiers == ModifierKeys.Shift)
                {
                    if (Console.CapsLock) str = key.ToString().ToLower();
                    else str = key.ToString();
                }

                if (!String.IsNullOrWhiteSpace(str))
                {
                    var comp = new TextComposition(InputManager.Current, TextBox, str);
                    TextBox.RaiseEvent(new TextCompositionEventArgs(InputManager.Current.PrimaryKeyboardDevice, comp)
                    {
                        RoutedEvent = TextCompositionManager.TextInputEvent
                    });
                    e.Handled = true;
                    return;
                }
            }
        }

        private void textBox_LostFocus(object sender, RoutedEventArgs e)
        {
            HideTextbox(editedGraphicsText, drawingCanvas);
        }

        private void textBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            HideTextbox(editedGraphicsText, drawingCanvas);

            // Notes:
            // TextBox context menu is set to null in CreateTextBox function.
            // The reason I did this is the following:
            // I must hide textbox when user clicks anywhere
            // outside of textbox, outside of this program window,
            // or any other window pops up and steals focus.
            // The only function which works for all these cases for 100%
            // is LostKeyboardFocus handler. However, LostKeyboardFocus
            // is raised also when textbox context menu is shown, and this
            // breaks all logic. To keep things consistent, I don't allow
            // showing context menu.
        }
    }
}

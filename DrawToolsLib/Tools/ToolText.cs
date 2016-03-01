using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.IO;
using System.Diagnostics;


namespace DrawToolsLib
{
    /// <summary>
    /// Text tool
    /// </summary>
    class ToolText : ToolRectangleBase
    {
        TextBox textBox;
        string oldText;

        GraphicsText editedGraphicsText;
        DrawingCanvas drawingCanvas;


        public ToolText(DrawingCanvas drawingCanvas)
        {
            this.drawingCanvas = drawingCanvas;
            MemoryStream stream = new MemoryStream(Properties.Resources.Text);
            ToolCursor = new Cursor(stream);
        }

        /// <summary>
        /// Textbox is exposed for DrawingCanvas Visual Children Overrides.
        /// If it is not null, overrides should include this textbox.
        /// </summary>
        public TextBox TextBox
        {
            get { return textBox; }
            set { textBox = value; }
        }

        /// <summary>
        /// Create new text object
        /// </summary>
        public override void OnMouseDown(DrawingCanvas drawingCanvas, MouseButtonEventArgs e)
        {
            Point p = e.GetPosition(drawingCanvas);

            AddNewObject(drawingCanvas,
                new GraphicsText(
                String.Empty,
                p.X,
                p.Y,
                p.X + 1,
                p.Y + 1,
                drawingCanvas.ObjectColor,
                drawingCanvas.TextFontSize,
                drawingCanvas.TextFontFamilyName,
                drawingCanvas.TextFontStyle,
                drawingCanvas.TextFontWeight,
                drawingCanvas.TextFontStretch,
                drawingCanvas.ActualScale));
        }

        /// <summary>
        /// Left mouse is released.
        /// New object is created and resized.
        /// </summary>
        public override void OnMouseUp(DrawingCanvas drawingCanvas, MouseButtonEventArgs e)
        {
            drawingCanvas.Tool = ToolType.Pointer;
            drawingCanvas.Cursor = HelperFunctions.DefaultCursor;
            drawingCanvas.ReleaseMouseCapture();

            if (drawingCanvas.Count > 0)
            {
                drawingCanvas[drawingCanvas.Count - 1].Normalize();

                GraphicsText t = drawingCanvas[drawingCanvas.Count - 1] as GraphicsText;
                if (t.Rectangle.Width < 150)
                    t.Right = t.Left + 150;
                if (t.Rectangle.Height < 100)
                    t.Bottom = t.Top + 150;

                if (t != null)
                {
                    // Create textbox for editing of graphics object which is just created
                    CreateTextBox(t, drawingCanvas);
                }
            }

            // Commnnd will be added to History later, after closing
            // in-place textbox.
        }

        /// <summary>
        /// Create textbox for in-place editing
        /// </summary>
        public void CreateTextBox(GraphicsText graphicsText, DrawingCanvas drawingCanvas)
        {
            graphicsText.IsSelected = false;  // selection marks don't look good with textbox

            // Keep old text in the case Esc is pressed while editing
            oldText = graphicsText.Text;

            // Keep reference to edited object
            editedGraphicsText = graphicsText;


            textBox = new TextBox();

            textBox.Width = graphicsText.Rectangle.Width;
            textBox.Height = graphicsText.Rectangle.Height;
            textBox.FontFamily = new FontFamily(graphicsText.TextFontFamilyName);
            textBox.FontSize = graphicsText.TextFontSize;
            textBox.FontStretch = graphicsText.TextFontStretch;
            textBox.FontStyle = graphicsText.TextFontStyle;
            textBox.FontWeight = graphicsText.TextFontWeight;
            textBox.Text = graphicsText.Text;

            textBox.AcceptsReturn = true;
            textBox.TextWrapping = TextWrapping.Wrap;

            drawingCanvas.Children.Add(textBox);

            Canvas.SetLeft(textBox, graphicsText.Rectangle.Left);
            Canvas.SetTop(textBox, graphicsText.Rectangle.Top);
            textBox.Width = textBox.Width;
            textBox.Height = textBox.Height;

            textBox.Focus();

            textBox.LostFocus += new RoutedEventHandler(textBox_LostFocus);
            textBox.LostKeyboardFocus += new KeyboardFocusChangedEventHandler(textBox_LostKeyboardFocus);
            textBox.PreviewKeyDown += new KeyEventHandler(textBox_PreviewKeyDown);
            textBox.ContextMenu = null;     // see notes in textBox_LostKeyboardFocus

            // Initially textbox is set to the same rectangle as graphicsText.
            // After textbox loading its template is available, and we can
            // correct textbox position - see details in the textBox_Loaded function.
            textBox.Loaded += new RoutedEventHandler(textBox_Loaded);
        }


        /// <summary>
        /// Correct textbox position.
        /// Without this correction text shown in a textbox appears with some
        /// horizontal and vertical offset relatively to textbox bounding rectangle.
        /// We need to apply this correction, moving textbox in left-up direction.
        /// 
        /// Visually, text should not "jump" on the screen, when in-place editing
        /// textbox is open and closed.
        /// </summary>
        void textBox_Loaded(object sender, RoutedEventArgs e)
        {
            double xOffset, yOffset;

            ComputeTextOffset(textBox, out xOffset, out yOffset);


            Canvas.SetLeft(textBox, Canvas.GetLeft(textBox) - xOffset);
            Canvas.SetTop(textBox, Canvas.GetTop(textBox) - yOffset);
            textBox.Width = textBox.Width + xOffset + xOffset;
            textBox.Height = textBox.Height + yOffset + yOffset;
        }


        /// <summary>
        /// Compute distance between textbox top-left point and actual
        /// text top-left point inside of textbox.
        /// 
        /// Thanks to Nick Holmes for showing this code in MSDN WPF Forum.
        /// </summary>
        static void ComputeTextOffset(TextBox tb, out double xOffset, out double yOffset)
        {
            // Set hard-coded values initially
            xOffset = 5;
            yOffset = 3;

            try
            {
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
            catch (ArgumentException e)
            {
                System.Diagnostics.Trace.WriteLine("ComputeTextOffset: " + e.Message);
            }
            catch (InvalidOperationException e)
            {
                System.Diagnostics.Trace.WriteLine("ComputeTextOffset: " + e.Message);
            }
        }


        // Hide textbox when Enter or Esc is changed
        void textBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                textBox.Text = oldText;

                drawingCanvas.HideTextbox(editedGraphicsText);

                e.Handled = true;
                return;
            }

            // Enter without modifiers - Shift+Enter should be available.
            if (e.Key == Key.Return && Keyboard.Modifiers == ModifierKeys.None)
            {
                drawingCanvas.HideTextbox(editedGraphicsText);

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
                    var comp = new TextComposition(InputManager.Current, textBox, str);
                    textBox.RaiseEvent(new TextCompositionEventArgs(InputManager.Current.PrimaryKeyboardDevice, comp)
                    {
                        RoutedEvent = TextCompositionManager.TextInputEvent
                    });
                    e.Handled = true;
                    return;
                }
            }
        }

        /// <summary>
        /// Hide textbox when it looses focus.  
        /// </summary>
        void textBox_LostFocus(object sender, RoutedEventArgs e)
        {
            drawingCanvas.HideTextbox(editedGraphicsText);
        }


        /// <summary>
        /// Hide textbox when it looses keyboard focus.  
        /// </summary>
        void textBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            drawingCanvas.HideTextbox(editedGraphicsText);

            /// Notes:
            /// TextBox context menu is set to null in CreateTextBox function.
            /// The reason I did this is the following:
            /// I must hide textbox when user clicks anywhere
            /// outside of textbox, outside of this program window,
            /// or any other window pops up and steals focus.
            /// The only function which works for all these cases for 100%
            /// is LostKeyboardFocus handler. However, LostKeyboardFocus
            /// is raised also when textbox context menu is shown, and this
            /// breaks all logic. To keep things consistent, I don't allow
            /// showing context menu.
        }

        /// <summary>
        /// Textbox text value before in-place editing.
        /// </summary>
        public string OldText
        {
            get { return oldText; }
        }

    }
}

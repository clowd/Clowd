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

        private GraphicText _newText;
        private GraphicText _editText;
        private TextBox _txtBox;
        private string _oldText;

        public ToolText(Cursor cursor = null, SnapMode snapMode = SnapMode.None) : base(cursor ?? Resource.CursorText, snapMode)
        { }

        protected override void OnMouseDownImpl(DrawingCanvas canvas, Point pt)
        {
            _newText = new GraphicText(canvas, pt);
            _newText.IsSelected = true;
            canvas.GraphicsList.Add(_newText);
        }

        protected override void OnMouseMoveImpl(DrawingCanvas canvas, Point pt)
        {
            if (_newText != null)
            {
                _newText.Left = pt.X;
                _newText.Top = pt.Y;
                _newText.Normalize();
            }
        }

        protected override void OnMouseUpImpl(DrawingCanvas canvas)
        {
            if (_newText != null)
            {
                CreateTextBox(_newText, canvas, true);
                _newText = null;
            }
        }

        public void CreateTextBox(GraphicText graphicsText, DrawingCanvas drawingCanvas, bool newGraphic = false)
        {
            if (_txtBox != null || _editText != null)
                AbortOperation(drawingCanvas);

            graphicsText.Editing = true;
            _editText = graphicsText;

            _txtBox = new TextBox();
            _txtBox.FontFamily = new FontFamily(graphicsText.FontName);
            _txtBox.FontSize = graphicsText.FontSize;
            _txtBox.FontStretch = graphicsText.FontStretch;
            _txtBox.FontStyle = graphicsText.FontStyle;
            _txtBox.FontWeight = graphicsText.FontWeight;
            _txtBox.Width = Double.NaN;
            _txtBox.Height = Double.NaN;
            _txtBox.Background = Brushes.Transparent;
            _txtBox.Text = graphicsText.Body;
            _txtBox.BorderThickness = new Thickness(0, 0, 0, 0);
            _txtBox.BorderBrush = Brushes.Transparent;
            _txtBox.Tag = graphicsText;
            _txtBox.Style = null;
            _txtBox.AcceptsReturn = true;

            var finalTransform = new TransformGroup();
            finalTransform.Children.Add(new TranslateTransform(GraphicText.TextPadding - 2, GraphicText.TextPadding));
            finalTransform.Children.Add(new RotateTransform(graphicsText.Angle, (graphicsText.Right - graphicsText.Left) / 2,
                (graphicsText.Bottom - graphicsText.Top) / 2));
            _txtBox.RenderTransform = finalTransform;

            if (newGraphic)
            {
                _txtBox.Text = graphicsText.Body;
                _txtBox.SelectAll();
                _oldText = "";
            }
            else
            {
                _oldText = graphicsText.Body;
                _txtBox.CaretIndex = int.MaxValue;
            }

            drawingCanvas.Children.Add(_txtBox);

            Canvas.SetLeft(_txtBox, graphicsText.Left);
            Canvas.SetTop(_txtBox, graphicsText.Top);

            _txtBox.Focus();
            _txtBox.LostFocus += (_, _) => FinishEdit(drawingCanvas, newGraphic);
            _txtBox.LostKeyboardFocus += (_, _) => FinishEdit(drawingCanvas, newGraphic);

            _txtBox.PreviewKeyDown += (sender, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    AbortOperation(drawingCanvas);
                }

                // Enter without modifiers - Shift+Enter should be available for new-lines.
                else if (e.Key == Key.Return && Keyboard.Modifiers == ModifierKeys.None)
                {
                    e.Handled = true;
                    FinishEdit(drawingCanvas, newGraphic);
                }
            };

            _txtBox.TextChanged += (sender, e) =>
            {
                graphicsText.Body = _txtBox.Text;
            };

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
            _txtBox.ContextMenu = null;
        }

        public override void AbortOperation(DrawingCanvas canvas)
        {
            if (_newText != null)
            {
                canvas.GraphicsList.Remove(_newText);
                _newText = null;
            }

            if (_editText != null)
            {
                if (String.IsNullOrEmpty(_oldText))
                {
                    // if this textbox is brand new, remove it
                    canvas.GraphicsList.Remove(_editText);
                }
                else
                {
                    // otherwise, revert it to it's previous text
                    _editText.Body = _oldText;
                    _editText.Editing = false;
                    _editText.IsSelected = true;
                }

                _editText = null;
            }

            if (_txtBox != null)
            {
                canvas.Children.Remove(_txtBox);
                _txtBox = null;
            }

            // This enables back all ApplicationCommands,
            // which are disabled while textbox is active.
            canvas.Focus();
        }

        private void FinishEdit(DrawingCanvas drawingCanvas, bool newGraphic)
        {
            if (_txtBox == null || _editText == null || String.IsNullOrWhiteSpace(_txtBox.Text))
            {
                AbortOperation(drawingCanvas);
                return;
            }

            var newText = _txtBox.Text.Trim();
            _editText.Body = newText;

            if (newText != _oldText)
            {
                drawingCanvas.AddCommandToHistory();
            }

            _editText.Editing = false;
            _editText.IsSelected = true;

            drawingCanvas.Children.Remove(_txtBox);
            _txtBox = null;
            _editText = null;

            // This enables back all ApplicationCommands,
            // which are disabled while textbox is active.
            drawingCanvas.Focus();
        }
    }
}

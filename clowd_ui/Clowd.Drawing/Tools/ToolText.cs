using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Clowd.Drawing.Graphics;

namespace Clowd.Drawing.Tools
{
    internal class ToolText : ToolBase
    {
        // Alignment fudge so the TextBox text lines up with the rendered GraphicText body (re-tuned in
        // WP15). WPF needed -2 because its TextBox kept a 2px internal inset even with Style=null;
        // Avalonia's chrome-less ControlTheme below (Padding 0, template = ScrollViewer+TextPresenter)
        // has no inset, so the correct value here is 0 — measured empirically by diffing rendered vs.
        // edit-mode frames (leftmost text ink matched exactly; Y needed no fudge in either port).
        internal const double TEXTBOX_ALIGN_X = 0;

        private GraphicText _newText;
        private GraphicText _editText;
        private TextBox _txtBox;
        private string _oldText;
        private WindowBase _topLevel;
        private EventHandler _deactivatedHandler;

        private static ControlTheme _editTextBoxTheme;

        public ToolText(Func<Cursor> cursorFn = null, SnapMode snapMode = SnapMode.None) : base(cursorFn ?? (() => CursorResources.Text), snapMode)
        { }

        protected override void OnMouseDownImpl(DrawingCanvas canvas, Point pt)
        {
            _newText = new GraphicText(canvas, pt);
            _newText.IsSelected = true;
            canvas.GraphicsList.Add(_newText);
            OnMouseMoveImpl(canvas, pt);
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
            // decision #40: WPF "Style = null" → minimal local ControlTheme (transparent chrome, Padding 0)
            _txtBox.Theme = GetEditTextBoxTheme();
            _txtBox.FontFamily = FontUtil.CreateSafe(graphicsText.FontName);
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
            _txtBox.AcceptsReturn = true;

            var finalTransform = new TransformGroup();
            finalTransform.Children.Add(new TranslateTransform(GraphicText.TextPadding + TEXTBOX_ALIGN_X, GraphicText.TextPadding));
            finalTransform.Children.Add(new RotateTransform(graphicsText.Angle, (graphicsText.Right - graphicsText.Left) / 2,
                (graphicsText.Bottom - graphicsText.Top) / 2));
            _txtBox.RenderTransform = finalTransform;
            // decision #15: Avalonia defaults to center; WPF rotated around the top-left implicitly
            _txtBox.RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative);

            _oldText = newGraphic ? "" : graphicsText.Body;

            drawingCanvas.Children.Add(_txtBox);

            Canvas.SetLeft(_txtBox, graphicsText.Left);
            Canvas.SetTop(_txtBox, graphicsText.Top);

            Dispatcher.UIThread.Post(() =>
            {
                if (_txtBox == null)
                    return;
                _txtBox.Focus();
                // all text starts selected, for new graphics and double-click edits alike
                _txtBox.SelectAll();
            });

            // decision #39: WPF LostFocus + LostKeyboardFocus → LostFocus once + TopLevel.Deactivated
            _txtBox.LostFocus += (_, _) => FinishEdit(drawingCanvas, newGraphic);
            _topLevel = TopLevel.GetTopLevel(drawingCanvas) as WindowBase;
            if (_topLevel != null)
            {
                _deactivatedHandler = (_, _) => FinishEdit(drawingCanvas, newGraphic);
                _topLevel.Deactivated += _deactivatedHandler;
            }

            // decision #38: WPF PreviewKeyDown → tunnel KeyDown handler
            _txtBox.AddHandler(InputElement.KeyDownEvent, (sender, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    AbortOperation(drawingCanvas);
                }

                // Enter without modifiers - Shift+Enter should be available for new-lines.
                else if (e.Key == Key.Return && e.KeyModifiers == KeyModifiers.None)
                {
                    e.Handled = true;
                    FinishEdit(drawingCanvas, newGraphic);
                }
            }, RoutingStrategies.Tunnel);

            _txtBox.TextChanged += (sender, e) =>
            {
                graphicsText.Body = ((TextBox)sender).Text ?? "";
            };

            // Notes:
            // TextBox context menu is set to null
            // The reason I did this is the following:
            // I must hide textbox when user clicks anywhere
            // outside of textbox, outside of this program window,
            // or any other window pops up and steals focus.
            // The only function which works for all these cases for 100%
            // is the lost-focus handling. However, focus loss
            // is raised also when textbox context menu is shown, and this
            // breaks all logic. To keep things consistent, I don't allow
            // showing context menu.
            _txtBox.ContextFlyout = null;
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
                // null the field before removal so re-entrant LostFocus (fired by removing the focused
                // control) cannot run FinishEdit/AbortOperation against a half-torn-down state.
                var txtBox = _txtBox;
                _txtBox = null;
                DetachDeactivated();
                canvas.Children.Remove(txtBox);
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
                drawingCanvas.AddCommandToHistory(false);
            }

            _editText.Editing = false;
            _editText.IsSelected = true;

            // null the fields before removal so re-entrant LostFocus is a no-op (see AbortOperation)
            var txtBox = _txtBox;
            _txtBox = null;
            _editText = null;
            DetachDeactivated();
            drawingCanvas.Children.Remove(txtBox);

            // This enables back all ApplicationCommands,
            // which are disabled while textbox is active.
            drawingCanvas.Focus();
        }

        private void DetachDeactivated()
        {
            if (_topLevel != null && _deactivatedHandler != null)
                _topLevel.Deactivated -= _deactivatedHandler;
            _topLevel = null;
            _deactivatedHandler = null;
        }

        private static ControlTheme GetEditTextBoxTheme()
        {
            if (_editTextBoxTheme != null)
                return _editTextBoxTheme;

            // Minimal TextBox theme: transparent chrome in every state, Padding 0, black text/caret.
            // The template is just a ScrollViewer + TextPresenter (the required template parts), so none of
            // the Fluent pseudo-class backgrounds/borders ever appear.
            var theme = new ControlTheme(typeof(TextBox));
            theme.Setters.Add(new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent));
            theme.Setters.Add(new Setter(TemplatedControl.BorderBrushProperty, Brushes.Transparent));
            theme.Setters.Add(new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(0)));
            theme.Setters.Add(new Setter(TemplatedControl.PaddingProperty, new Thickness(0)));
            theme.Setters.Add(new Setter(TemplatedControl.ForegroundProperty, Brushes.Black));
            theme.Setters.Add(new Setter(Layoutable.MinWidthProperty, 0d));
            theme.Setters.Add(new Setter(Layoutable.MinHeightProperty, 0d));
            theme.Setters.Add(new Setter(TextBox.CaretBrushProperty, Brushes.Black));
            theme.Setters.Add(new Setter(TextBox.SelectionBrushProperty, new SolidColorBrush(Color.FromArgb(0x80, 0x33, 0x99, 0xFF))));
            theme.Setters.Add(new Setter(TemplatedControl.TemplateProperty, new FuncControlTemplate<TextBox>((tb, ns) =>
            {
                var presenter = new TextPresenter
                {
                    Name = "PART_TextPresenter",
                    [!TextPresenter.TextProperty] = new TemplateBinding(TextBox.TextProperty) { Mode = BindingMode.TwoWay },
                    [!TextPresenter.CaretIndexProperty] = new TemplateBinding(TextBox.CaretIndexProperty),
                    [!TextPresenter.SelectionStartProperty] = new TemplateBinding(TextBox.SelectionStartProperty),
                    [!TextPresenter.SelectionEndProperty] = new TemplateBinding(TextBox.SelectionEndProperty),
                    [!TextPresenter.TextAlignmentProperty] = new TemplateBinding(TextBox.TextAlignmentProperty),
                    [!TextPresenter.TextWrappingProperty] = new TemplateBinding(TextBox.TextWrappingProperty),
                    [!TextPresenter.SelectionBrushProperty] = new TemplateBinding(TextBox.SelectionBrushProperty),
                    [!TextPresenter.SelectionForegroundBrushProperty] = new TemplateBinding(TextBox.SelectionForegroundBrushProperty),
                    [!TextPresenter.CaretBrushProperty] = new TemplateBinding(TextBox.CaretBrushProperty),
                    [!Layoutable.MarginProperty] = new TemplateBinding(TemplatedControl.PaddingProperty),
                };
                presenter.RegisterInNameScope(ns);

                var scrollViewer = new ScrollViewer
                {
                    Name = "PART_ScrollViewer",
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                    Content = presenter,
                };
                scrollViewer.RegisterInNameScope(ns);

                return scrollViewer;
            })));

            _editTextBoxTheme = theme;
            return theme;
        }
    }
}

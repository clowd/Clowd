using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Clowd.PlatformUtil;
using Clowd.PlatformUtil.Windows;
using Brush = System.Windows.Media.Brush;
using Point = System.Windows.Point;

namespace Clowd.UI.Dialogs.LiveDraw
{
    public partial class LiveDrawWindow : Window, ILiveDrawPage
    {
        public static readonly DependencyProperty PanelOrientationProperty = DependencyProperty.Register(
            "PanelOrientation", typeof(Orientation), typeof(LiveDrawWindow), new PropertyMetadata(Orientation.Horizontal));

        public Orientation PanelOrientation
        {
            get { return (Orientation)GetValue(PanelOrientationProperty); }
            set { SetValue(PanelOrientationProperty, value); }
        }

        public static readonly DependencyProperty PanelReversedProperty = DependencyProperty.Register(
            "PanelReversed", typeof(bool), typeof(LiveDrawWindow), new PropertyMetadata(false));

        public bool PanelReversed
        {
            get { return (bool)GetValue(PanelReversedProperty); }
            set { SetValue(PanelReversedProperty, value); }
        }

        public LiveDrawWindow()
        {
            _history = new Stack<StrokesHistoryNode>();
            _redoHistory = new Stack<StrokesHistoryNode>();

            InitializeComponent();

            SetColor(DefaultColorPicker);
            SetEnable(false, _mode);
            SetBrushSize(_brushSizes[_brushIndex]);

            // FontReduceButton.Opacity = 0;
            // FontIncreaseButton.Opacity = 0;

            MainInkCanvas.Strokes.StrokesChanged += StrokesChanged;

            MainInkCanvas.MouseLeftButtonDown += MainInkCanvas_MouseLeftButtonDown;
            MainInkCanvas.MouseLeftButtonUp += MainInkCanvas_MouseLeftButtonUp;
            MainInkCanvas.MouseRightButtonUp += MainInkCanvas_MouseRightButtonUp;
            MainInkCanvas.MouseMove += MainInkCanvas_MouseMove;

            _drawerTextBox.FontSize = 24.0;
            _drawerTextBox.Background = this.Resources["TrueTransparent"] as Brush;
            _drawerTextBox.AcceptsReturn = true;
            _drawerTextBox.TextWrapping = TextWrapping.Wrap;
            _drawerTextBox.LostFocus += _drawerTextBox_LostFocus;
        }

        public void Open()
        {
            var vbounds = Platform.Current.VirtualScreen.Bounds;
            var helper = new WindowInteropHelper(this);
            helper.EnsureHandle();

            var wnd = User32Window.FromHandle(helper.Handle);
            wnd.WindowBounds = vbounds;

            this.Show();

            // position Palette on monitor with cursor and scale for dpi
            _dpi = this.ClientAreaToDpiContext();
            var screen = Platform.Current.GetScreenFromPoint(Platform.Current.GetMousePosition());
            Palette.LayoutTransform = new ScaleTransform(screen.PixelDensity, screen.PixelDensity);
            SetPaletteLocation(new ScreenPoint(screen.WorkingArea.X + 150, screen.WorkingArea.Y + 150));
        }

        private void MainInkCanvas_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            SetEnable(false, _mode);
        }

        private DpiContext _dpi;
        private ColorPicker _selectedColor;
        private bool _inkVisibility = true;
        private bool _enable = false;
        private readonly int[] _brushSizes = { 4, 6, 8, 10, 14 };
        private int _brushIndex = 0;
        private bool _displayOrientation;
        private DrawMode _mode = DrawMode.Pen;

        private void SetInkVisibility(bool v)
        {
            MainInkCanvas.Opacity = v ? 1 : 0;
            HideButton.IsActivated = !v;

            if (v == false)
                _tempEnable = _enable;

            SetEnable(v == false ? false : _tempEnable, _mode);
            _inkVisibility = v;
        }

        private void SetEnable(bool enable, DrawMode mode)
        {
            _enable = enable;
            _mode = mode;

            InkCanvasEditingMode editingMode = InkCanvasEditingMode.Ink;
            bool bUseCustomCursor = true;

            switch (_mode)
            {
                case DrawMode.Select:
                    bUseCustomCursor = false;
                    editingMode = InkCanvasEditingMode.Select;
                    break;
                case DrawMode.Pen:
                    editingMode = InkCanvasEditingMode.Ink;
                    break;
                case DrawMode.Text:
                    editingMode = InkCanvasEditingMode.None;
                    break;
                case DrawMode.Line:
                    editingMode = InkCanvasEditingMode.None;
                    break;
                case DrawMode.Arrow:
                    editingMode = InkCanvasEditingMode.None;
                    break;
                case DrawMode.Rectangle:
                    editingMode = InkCanvasEditingMode.None;
                    break;
                case DrawMode.Circle:
                    editingMode = InkCanvasEditingMode.None;
                    break;
                case DrawMode.Erase:
                    bUseCustomCursor = false;
                    editingMode = InkCanvasEditingMode.EraseByStroke;
                    break;
                default:
                    _mode = DrawMode.Select;
                    break;
            }

            MainInkCanvas.Cursor = Cursors.Cross;

            // if (_mode == DrawMode.Text)
            // {
            //     FontIncreaseButton.Opacity = 1;
            //     FontReduceButton.Opacity = 1;
            // }
            // else
            // {
            //     FontIncreaseButton.Opacity = 0;
            //     FontReduceButton.Opacity = 0;
            // }

            MainInkCanvas.UseCustomCursor = bUseCustomCursor;
            MainInkCanvas.EditingMode = editingMode;

            EnableButton.IsActivated = !enable;
            Background = this.Resources[enable ? "FakeTransparent" : "TrueTransparent"] as Brush;

            SelectButton.IsActivated = _enable == true && _mode == DrawMode.Select;
            PenButton.IsActivated = _enable == true && _mode == DrawMode.Pen;
            // TextButton.IsActivated = _enable == true && _mode == DrawMode.Text;
            LineButton.IsActivated = _enable == true && _mode == DrawMode.Line;
            ArrowButton.IsActivated = _enable == true && _mode == DrawMode.Arrow;
            RectangleButton.IsActivated = _enable == true && _mode == DrawMode.Rectangle;
            CircleButton.IsActivated = _enable == true && _mode == DrawMode.Circle;
            EraserButton.IsActivated = _enable == true && _mode == DrawMode.Erase;
        }

        private void SetColor(ColorPicker b)
        {
            if (ReferenceEquals(_selectedColor, b)) return;
            var solidColorBrush = b.Background as SolidColorBrush;
            if (solidColorBrush == null)
                return;

            MainInkCanvas.DefaultDrawingAttributes.Color = solidColorBrush.Color;
            brushPreview.Background = solidColorBrush;
            b.IsActivated = true;
            if (_selectedColor != null)
                _selectedColor.IsActivated = false;
            _selectedColor = b;

            _drawerTextBox.Foreground = solidColorBrush;
        }

        private void SetBrushSize(double s)
        {
            MainInkCanvas.DefaultDrawingAttributes.Height = s;
            MainInkCanvas.DefaultDrawingAttributes.Width = s;
            brushPreview.Height = s;
            brushPreview.Width = s;
        }

        private ScreenRect GetPaletteLocation()
        {
            Palette.Measure(new Size(999, 999));
            var logicalPos = new LogicalRect(Canvas.GetLeft(Palette), Canvas.GetTop(Palette), Palette.DesiredSize.Width, Palette.DesiredSize.Height);
            return _dpi.ToScreenRect(logicalPos);
        }

        private void SetPaletteLocation(ScreenPoint newPos)
        {
            var newLogicalPos = _dpi.ToWorldPoint(newPos);
            Canvas.SetLeft(Palette, newLogicalPos.X);
            Canvas.SetTop(Palette, newLogicalPos.Y);
        }

        private void SetOrientation(bool v)
        {
            // find current monitor palette is on
            var origPos = GetPaletteLocation();
            var screen = Platform.Current.GetScreenFromRect(origPos);

            // update orientation of palette component
            if (v)
            {
                PanelOrientation = Orientation.Vertical;
                PanelReversed = true;
                Palette.Orientation = Orientation.Horizontal;
                PaletteGrip.LayoutTransform = new RotateTransform(-90);
                OrientationButtonTransform.Angle = 0;
                OrientationButton.IsActivated = true;
            }
            else
            {
                PanelOrientation = Orientation.Horizontal;
                PanelReversed = false;
                Palette.Orientation = Orientation.Vertical;
                PaletteGrip.LayoutTransform = null;
                OrientationButtonTransform.Angle = 90;
                OrientationButton.IsActivated = false;
            }

            // shift palette so it is rotated along the center point
            var rotatedPos = GetPaletteLocation();
            var newPt = new ScreenPoint(
                (origPos.X + (origPos.Width / 2)) - (rotatedPos.Width / 2),
                (origPos.Y + (origPos.Height / 2)) - (rotatedPos.Height / 2));

            rotatedPos = new ScreenRect(newPt, rotatedPos.Size);

            // make sure it is fully contained within the original monitor
            rotatedPos = screen.FitRectToScreen(rotatedPos);
            SetPaletteLocation(rotatedPos.TopLeft);

            _displayOrientation = v;
        }

        private List<Point> GenerateEclipseGeometry(Point st, Point ed)
        {
            double a = 0.5 * (ed.X - st.X);
            double b = 0.5 * (ed.Y - st.Y);
            List<Point> pointList = new List<Point>();
            for (double r = 0; r <= 2 * Math.PI; r = r + 0.01)
            {
                pointList.Add(new Point(0.5 * (st.X + ed.X) + a * Math.Cos(r), 0.5 * (st.Y + ed.Y) + b * Math.Sin(r)));
            }

            return pointList;
        }

        private Point _drawerIntPos;
        private bool _drawerIsMove = false;
        private Stroke _drawerLastStroke;
        private TextBox _drawerTextBox = new TextBox();

        private void _drawerTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            String text = _drawerTextBox.Text;

            if (text.Length > 0)
            {
                var textBlock = new TextBlock();
                textBlock.Text = text;

                MainInkCanvas.Children.Add(textBlock);

                textBlock.Visibility = Visibility.Visible;
                textBlock.Foreground = _drawerTextBox.Foreground;
                textBlock.FontSize = _drawerTextBox.FontSize;
                textBlock.TextWrapping = _drawerTextBox.TextWrapping;

                InkCanvas.SetLeft(textBlock, InkCanvas.GetLeft(_drawerTextBox));
                InkCanvas.SetTop(textBlock, InkCanvas.GetTop(_drawerTextBox));
            }

            MainInkCanvas.Children.Remove(_drawerTextBox);
        }

        private void MainInkCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_enable == false || _mode == DrawMode.Select || _mode == DrawMode.Pen || _mode == DrawMode.None || e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            _ignoreStrokesChange = true;
            _drawerIsMove = true;
            _drawerIntPos = e.GetPosition(MainInkCanvas);
            _drawerLastStroke = null;

            if (_mode == DrawMode.Text)
            {
                _drawerTextBox.Text = "";

                if (MainInkCanvas.Children.Contains(_drawerTextBox) == false)
                    MainInkCanvas.Children.Add(_drawerTextBox);

                _drawerTextBox.Visibility = Visibility.Visible;
                InkCanvas.SetLeft(_drawerTextBox, _drawerIntPos.X);
                InkCanvas.SetTop(_drawerTextBox, _drawerIntPos.Y);
                _drawerTextBox.Width = 0;
                _drawerTextBox.Height = 0;
            }
        }

        private void MainInkCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_drawerIsMove == true)
            {
                Point endP = e.GetPosition(MainInkCanvas);

                if (_drawerLastStroke != null && _mode != DrawMode.Text)
                {
                    StrokeCollection collection = new StrokeCollection();
                    collection.Add(_drawerLastStroke);
                    Push(_history, new StrokesHistoryNode(collection, StrokesHistoryNodeType.Added));
                }

                if (_drawerLastStroke != null && _mode == DrawMode.Text)
                {
                    MainInkCanvas.Strokes.Remove(_drawerLastStroke);
                }

                if (_mode == DrawMode.Text)
                {
                    //resize drawer text box
                    _drawerTextBox.Width = Math.Abs(endP.X - _drawerIntPos.X);
                    _drawerTextBox.Height = Math.Abs(endP.Y - _drawerIntPos.Y);

                    if (_drawerTextBox.Width <= 100 || _drawerTextBox.Height <= 40)
                    {
                        _drawerTextBox.Width = 100;
                        _drawerTextBox.Height = 40;
                    }

                    InkCanvas.SetLeft(_drawerTextBox, Math.Min(_drawerIntPos.X, endP.X));
                    InkCanvas.SetTop(_drawerTextBox, Math.Min(_drawerIntPos.Y, endP.Y));

                    _drawerTextBox.Focus();
                }

                _drawerIsMove = false;
                _ignoreStrokesChange = false;
            }
        }

        private void MainInkCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (_drawerIsMove == false)
                return;

            DrawingAttributes drawingAttributes = MainInkCanvas.DefaultDrawingAttributes.Clone();
            Stroke stroke = null;

            drawingAttributes.StylusTip = StylusTip.Ellipse;
            drawingAttributes.IgnorePressure = true;

            Point endP = e.GetPosition(MainInkCanvas);

            if (_mode == DrawMode.Text)
            {
                List<Point> pointList = new List<Point>
                {
                    new Point(_drawerIntPos.X, _drawerIntPos.Y),
                    new Point(_drawerIntPos.X, endP.Y),
                    new Point(endP.X, endP.Y),
                    new Point(endP.X, _drawerIntPos.Y),
                    new Point(_drawerIntPos.X, _drawerIntPos.Y),
                };

                drawingAttributes.Width = 2;
                drawingAttributes.Height = 2;
                drawingAttributes.FitToCurve = false; //must be false,other wise rectangle can not be drawed correct

                StylusPointCollection point = new StylusPointCollection(pointList);
                stroke = new Stroke(point) { DrawingAttributes = drawingAttributes, };
            }
            else if (_mode == DrawMode.Line)
            {
                List<Point> pointList = new List<Point> { new Point(_drawerIntPos.X, _drawerIntPos.Y), new Point(endP.X, endP.Y), };

                StylusPointCollection point = new StylusPointCollection(pointList);
                stroke = new Stroke(point) { DrawingAttributes = drawingAttributes, };
            }
            else if (_mode == DrawMode.Arrow)
            {
                //FUCK THE MATH!!!!!!!!!!!!!!!FUCK !FUCK~!
                double w = 15, h = 15;
                double theta = Math.Atan2(_drawerIntPos.Y - endP.Y, _drawerIntPos.X - endP.X);
                double sint = Math.Sin(theta);
                double cost = Math.Cos(theta);

                List<Point> pointList = new List<Point>
                {
                    new Point(_drawerIntPos.X, _drawerIntPos.Y),
                    new Point(endP.X, endP.Y),
                    new Point(endP.X + (w * cost - h * sint), endP.Y + (w * sint + h * cost)),
                    new Point(endP.X, endP.Y),
                    new Point(endP.X + (w * cost + h * sint), endP.Y - (h * cost - w * sint)),
                };

                StylusPointCollection point = new StylusPointCollection(pointList);

                drawingAttributes.FitToCurve = false; //must be false,other wise rectangle can not be drawed correct

                stroke = new Stroke(point) { DrawingAttributes = drawingAttributes, };
            }
            else if (_mode == DrawMode.Rectangle)
            {
                List<Point> pointList = new List<Point>
                {
                    new Point(_drawerIntPos.X, _drawerIntPos.Y),
                    new Point(_drawerIntPos.X, endP.Y),
                    new Point(endP.X, endP.Y),
                    new Point(endP.X, _drawerIntPos.Y),
                    new Point(_drawerIntPos.X, _drawerIntPos.Y),
                };

                drawingAttributes.FitToCurve = false; //must be false,other wise rectangle can not be drawed correct

                StylusPointCollection point = new StylusPointCollection(pointList);
                stroke = new Stroke(point) { DrawingAttributes = drawingAttributes, };
            }
            else if (_mode == DrawMode.Circle)
            {
                List<Point> pointList = GenerateEclipseGeometry(_drawerIntPos, endP);
                StylusPointCollection point = new StylusPointCollection(pointList);
                stroke = new Stroke(point) { DrawingAttributes = drawingAttributes };
            }

            if (_drawerLastStroke != null)
                MainInkCanvas.Strokes.Remove(_drawerLastStroke);

            if (stroke != null)
                MainInkCanvas.Strokes.Add(stroke);

            _drawerLastStroke = stroke;
        }

        private string _staticInfo = "";

        private async void Display(string info)
        {
            InfoBox.Text = info;
            await InfoDisplayTimeUp(new Progress<string>(box => InfoBox.Text = box));
        }

        private Task InfoDisplayTimeUp(IProgress<string> box)
        {
            return Task.Run(() =>
            {
                Task.Delay(2000).Wait();
                box.Report(_staticInfo);
            });
        }

        private readonly Stack<StrokesHistoryNode> _history;
        private readonly Stack<StrokesHistoryNode> _redoHistory;
        private bool _ignoreStrokesChange;

        private void Undo()
        {
            if (!CanUndo()) return;
            var last = Pop(_history);
            _ignoreStrokesChange = true;
            if (last.Type == StrokesHistoryNodeType.Added)
                MainInkCanvas.Strokes.Remove(last.Strokes);
            else
                MainInkCanvas.Strokes.Add(last.Strokes);
            _ignoreStrokesChange = false;
            Push(_redoHistory, last);
        }

        private void Redo()
        {
            if (!CanRedo()) return;
            var last = Pop(_redoHistory);
            _ignoreStrokesChange = true;
            if (last.Type == StrokesHistoryNodeType.Removed)
                MainInkCanvas.Strokes.Remove(last.Strokes);
            else
                MainInkCanvas.Strokes.Add(last.Strokes);
            _ignoreStrokesChange = false;
            Push(_history, last);
        }

        private static void Push(Stack<StrokesHistoryNode> collection, StrokesHistoryNode node)
        {
            collection.Push(node);
        }

        private static StrokesHistoryNode Pop(Stack<StrokesHistoryNode> collection)
        {
            return collection.Count == 0 ? null : collection.Pop();
        }

        private bool CanUndo()
        {
            return _history.Count != 0;
        }

        private bool CanRedo()
        {
            return _redoHistory.Count != 0;
        }

        private void StrokesChanged(object sender, StrokeCollectionChangedEventArgs e)
        {
            if (_ignoreStrokesChange) return;
            //_saved = false;
            if (e.Added.Count != 0)
                Push(_history, new StrokesHistoryNode(e.Added, StrokesHistoryNodeType.Added));
            if (e.Removed.Count != 0)
                Push(_history, new StrokesHistoryNode(e.Removed, StrokesHistoryNodeType.Removed));
            ClearHistory(_redoHistory);
        }

        private void ClearHistory()
        {
            ClearHistory(_history);
            ClearHistory(_redoHistory);
        }

        private static void ClearHistory(Stack<StrokesHistoryNode> collection)
        {
            collection?.Clear();
        }

        private void Clear()
        {
            MainInkCanvas.Children.Clear();
            MainInkCanvas.Strokes.Clear();
            ClearHistory();
            Display("Cleared");
        }

        private void ColorPickers_Click(object sender, RoutedEventArgs e)
        {
            var border = sender as ColorPicker;
            if (border == null) return;
            SetColor(border);
        }

        private void BrushSwitchButton_Click(object sender, RoutedEventArgs e)
        {
            _brushIndex++;
            if (_brushIndex > _brushSizes.Count() - 1) _brushIndex = 0;
            SetBrushSize(_brushSizes[_brushIndex]);
        }

        private void EnableButton_Click(object sender, RoutedEventArgs e)
        {
            SetEnable(false, _mode);
        }

        private void SelectButton_Click(object sender, RoutedEventArgs e)
        {
            SetEnable(true, DrawMode.Select);
        }

        private void PenButton_Click(object sender, RoutedEventArgs e)
        {
            SetEnable(true, DrawMode.Pen);
        }

        private void TextButton_Click(object sender, RoutedEventArgs e)
        {
            SetEnable(true, DrawMode.Text);
        }

        private void LineButton_Click(object sender, RoutedEventArgs e)
        {
            SetEnable(true, DrawMode.Line);
        }

        private void ArrowButton_Click(object sender, RoutedEventArgs e)
        {
            SetEnable(true, DrawMode.Arrow);
        }

        private void RectangleButton_Click(object sender, RoutedEventArgs e)
        {
            SetEnable(true, DrawMode.Rectangle);
        }

        private void CircleButton_Click(object sender, RoutedEventArgs e)
        {
            SetEnable(true, DrawMode.Circle);
        }

        private void UndoButton_Click(object sender, RoutedEventArgs e)
        {
            Undo();
        }

        private void RedoButton_Click(object sender, RoutedEventArgs e)
        {
            Redo();
        }

        private void EraserButton_Click(object sender, RoutedEventArgs e)
        {
            SetEnable(true, DrawMode.Erase);
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void HideButton_Click(object sender, RoutedEventArgs e)
        {
            SetInkVisibility(!_inkVisibility);
        }

        private void OrientationButton_Click(object sender, RoutedEventArgs e)
        {
            SetOrientation(!_displayOrientation);
        }

        private Point _lastMousePosition;
        private bool _tempEnable;

        private void PaletteGrip_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _lastMousePosition = Mouse.GetPosition(this);
            Palette.CaptureMouse();
            Palette.Background = new SolidColorBrush(Colors.Transparent);
            _tempEnable = _enable;
            SetEnable(true, _mode);
        }

        private void Palette_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (Palette.IsMouseCaptured)
            {
                SetEnable(_tempEnable, _mode);
            }

            Palette.Background = null;
            Palette.ReleaseMouseCapture();
        }

        private void Palette_MouseMove(object sender, MouseEventArgs e)
        {
            if (!Palette.IsMouseCaptured) return;

            var currentMousePosition = Mouse.GetPosition(this);
            var offset = currentMousePosition - _lastMousePosition;

            Canvas.SetTop(Palette, Canvas.GetTop(Palette) + offset.Y);
            Canvas.SetLeft(Palette, Canvas.GetLeft(Palette) + offset.X);

            // update DPI of component if dragged to a different monitor
            var currentTransform = Palette.LayoutTransform as ScaleTransform;
            var screen = Platform.Current.GetScreenFromRect(GetPaletteLocation());

            if (currentTransform.ScaleX != screen.PixelDensity)
            {
                var adjRatio = screen.PixelDensity / currentTransform.ScaleX;

                // calculate the new top left position, centering the scaling on the current mouse pos
                var matrix = Matrix.Identity;
                matrix.ScaleAt(adjRatio, adjRatio, currentMousePosition.X, currentMousePosition.Y);
                var topLeft = matrix.Transform(new Point(Canvas.GetLeft(Palette), Canvas.GetTop(Palette)));

                // only want to rescale if this does not change the 'GetScreenFromRect' calculation
                // this avoids "jitters" where resizing the area causes the center point to jump
                // back to the previous screen.
                var newLogicalPos = new LogicalRect(topLeft.X, topLeft.Y, Palette.ActualWidth * adjRatio, Palette.ActualHeight * adjRatio);
                var newScreen = Platform.Current.GetScreenFromRect(_dpi.ToScreenRect(newLogicalPos));
                if (newScreen.Handle.Equals(screen.Handle))
                {
                    Canvas.SetLeft(Palette, topLeft.X);
                    Canvas.SetTop(Palette, topLeft.Y);
                    Palette.LayoutTransform = new ScaleTransform(screen.PixelDensity, screen.PixelDensity);
                }
            }

            _lastMousePosition = currentMousePosition;
        }

        // private void FontReduceButton_Click(object sender, RoutedEventArgs e)
        // {
        //     _drawerTextBox.FontSize = _drawerTextBox.FontSize - 2;
        //     _drawerTextBox.FontSize = Math.Max(14, _drawerTextBox.FontSize);
        //     Display("Reduce Font -2");
        // }
        //
        // private void FontIncreaseButton_Click(object sender, RoutedEventArgs e)
        // {
        //     _drawerTextBox.FontSize = _drawerTextBox.FontSize + 2;
        //     _drawerTextBox.FontSize = Math.Min(60, _drawerTextBox.FontSize);
        //     Display("Increase Font +2");
        // }
    }
}

using Clowd;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Point = System.Windows.Point;

namespace AntFu7.LiveDraw
{
    public partial class LiveDrawWindow : Window, ILiveDrawPage
    {
        //    private static readonly Duration Duration1 = (Duration)Application.Current.Resources["Duration1"];
        //    private static readonly Duration Duration2 = (Duration)Application.Current.Resources["Duration2"];
        private static readonly Duration Duration3 = new Duration(TimeSpan.FromMilliseconds(300)); // (Duration)Application.Current.Resources["Duration3"];
        private static readonly Duration Duration4 = new Duration(TimeSpan.FromMilliseconds(400)); // (Duration)Application.Current.Resources["Duration4"];
        //private static readonly Duration Duration5 = new Duration(TimeSpan.FromMilliseconds(500)); // (Duration)Application.Current.Resources["Duration5"];
        //private static readonly Duration Duration7 = new Duration(TimeSpan.FromMilliseconds(700)); // (Duration)Application.Current.Resources["Duration7"];
        //private static readonly Duration Duration10 = (Duration)Application.Current.Resources["Duration10"];

        //private static Mutex mutex = new Mutex(true, "alldream-livedraw");

        /*#region Mouse Throught

        private const int WsExTransparent = 0x20;
        private const int GwlExstyle = (-20);

        [DllImport("user32", EntryPoint = "SetWindowLong")]
        private static extern uint SetWindowLong(IntPtr hwnd, int nIndex, uint dwNewLong);

        [DllImport("user32", EntryPoint = "GetWindowLong")]
        private static extern uint GetWindowLong(IntPtr hwnd, int nIndex);

        private void SetThrought(bool t)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var extendedStyle = GetWindowLong(hwnd, GwlExstyle);
            if (t)
                SetWindowLong(hwnd, GwlExstyle, extendedStyle | WsExTransparent);
            else
                SetWindowLong(hwnd, GwlExstyle, extendedStyle & ~(uint)WsExTransparent);
        }


        #endregion*/

        #region /---------Lifetime---------/
        public LiveDrawWindow()
        {
            //if (mutex.WaitOne(TimeSpan.Zero, true))
            //{

            _history = new Stack<StrokesHistoryNode>();
            _redoHistory = new Stack<StrokesHistoryNode>();

            //if (!Directory.Exists("Save"))
            //    Directory.CreateDirectory("Save");

            InitializeComponent();

            SetColor(DefaultColorPicker);

            SetEnable(false, _mode);

            SetTopMost(true);


            SetBrushSize(_brushSizes[_brushIndex]);

            ExtraToolPanel.Opacity = 0;
            FontReduceButton.Opacity = 0;
            FontIncreaseButton.Opacity = 0;

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

            //this.Closed += LiveDrawWindow_Closed;

            //}
            //else
            //{
            //    Application.Current.Shutdown(0);
            //}
        }

        //private static LiveDrawWindow _instance;

        //private void LiveDrawWindow_Closed(object sender, EventArgs e)
        //{
        //    _instance = null;
        //}

        //public static void ShowNewOrExisting()
        //{
        //    if (_instance != null)
        //    {
        //        _instance.Activate();
        //    }
        //    else
        //    {
        //        _instance = new LiveDrawWindow();
        //        _instance.Show();
        //    }
        //}

        public void Dispose()
        {
            this.Close();
        }

        public void Open()
        {
            this.Show();
        }

        private void MainInkCanvas_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            SetEnable(false, _mode);
        }

        //private void Exit(object sender, EventArgs e)
        //{
        //    //if (IsUnsaved())
        //    //    QuickSave("ExitingAutoSave_");

        //    Application.Current.Shutdown(0);
        //}

        #endregion


        #region /---------Judge--------/

        //private bool _saved;

        //private bool IsUnsaved()
        //{
        //    return MainInkCanvas.Strokes.Count != 0 && !_saved;
        //}

        //private bool PromptToSave()
        //{
        //    if (!IsUnsaved())
        //        return true;
        //    var r = MessageBox.Show("You have unsaved work, do you want to save it now?", "Unsaved data",
        //        MessageBoxButton.YesNoCancel);
        //    if (r == MessageBoxResult.Yes || r == MessageBoxResult.OK)
        //    {
        //        QuickSave();
        //        return true;
        //    }
        //    if (r == MessageBoxResult.No || r == MessageBoxResult.None)
        //        return true;
        //    return false;
        //}

        #endregion


        #region /---------Setter---------/

        private ColorPicker _selectedColor;
        private bool _inkVisibility = true;
        private bool _displayExtraToolPanel = false;
        private bool _enable = false;
        private readonly int[] _brushSizes = { 4, 6, 8, 10, 14 };
        private int _brushIndex = 0;
        private bool _displayOrientation;
        private DrawMode _mode = DrawMode.Pen;

        //private void SetExtralToolPanel(bool v)
        //{
        //    if (v)
        //    {
        //        DetailTogglerRotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(180, Duration5));
        //        //DefaultColorPicker.Size = ColorPickerButtonSize.Middle;
        //        ExtraToolPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Duration4));
        //        //PaletteGrip.BeginAnimation(WidthProperty, new DoubleAnimation(130, Duration3));
        //        //MinimizeButton.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Duration3));
        //        //MinimizeButton.BeginAnimation(HeightProperty, new DoubleAnimation(0, 25, Duration3));
        //    }
        //    else
        //    {
        //        DetailTogglerRotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, Duration5));
        //        //DefaultColorPicker.Size = ColorPickerButtonSize.Small;
        //        ExtraToolPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, Duration4));
        //        //PaletteGrip.BeginAnimation(WidthProperty, new DoubleAnimation(80, Duration3));
        //        //MinimizeButton.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, Duration3));
        //        //MinimizeButton.BeginAnimation(HeightProperty, new DoubleAnimation(25, 0, Duration3));
        //    }
        //    _displayExtraToolPanel = v;
        //}
        private void SetInkVisibility(bool v)
        {
            MainInkCanvas.BeginAnimation(OpacityProperty,
                v ? new DoubleAnimation(0, 1, Duration3) : new DoubleAnimation(1, 0, Duration3));
            HideButton.IsActived = !v;

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
                //case DrawMode.Ray:
                //    editingMode = InkCanvasEditingMode.None;
                //    break;
                case DrawMode.Erase:
                    bUseCustomCursor = false;
                    editingMode = InkCanvasEditingMode.EraseByStroke;
                    break;
                default:
                    _mode = DrawMode.Select;
                    break;
            }

            //if (_mode == DrawMode.Ray)
            //{
            //    MainInkCanvas.Cursor = new Cursor(new MemoryStream(AntFu7.LiveDraw.Properties.Resources.raycursor));
            //}
            //else
            //{
            MainInkCanvas.Cursor = Cursors.Cross;
            //}

            if (_mode == DrawMode.Text)
            {
                if (FontIncreaseButton.Opacity == 0)
                    FontIncreaseButton.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Duration3));

                if (FontReduceButton.Opacity == 0)
                    FontReduceButton.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Duration3));
            }
            else
            {
                if (FontIncreaseButton.Opacity == 1)
                    FontIncreaseButton.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, Duration3));

                if (FontReduceButton.Opacity == 1)
                    FontReduceButton.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, Duration3));
            }

            MainInkCanvas.UseCustomCursor = bUseCustomCursor;
            MainInkCanvas.EditingMode = editingMode;

            EnableButton.IsActived = !enable;
            Background = this.Resources[enable ? "FakeTransparent" : "TrueTransparent"] as Brush;

            SelectButton.IsActived = _enable == true && _mode == DrawMode.Select;
            PenButton.IsActived = _enable == true && _mode == DrawMode.Pen;
            TextButton.IsActived = _enable == true && _mode == DrawMode.Text;
            LineButton.IsActived = _enable == true && _mode == DrawMode.Line;
            ArrowButton.IsActived = _enable == true && _mode == DrawMode.Arrow;
            RectangleButton.IsActived = _enable == true && _mode == DrawMode.Rectangle;
            CircleButton.IsActived = _enable == true && _mode == DrawMode.Circle;
            //RayButton.IsActived = _enable == true && _mode == DrawMode.Ray;
            EraserButton.IsActived = _enable == true && _mode == DrawMode.Erase;
        }
        private void SetColor(ColorPicker b)
        {
            if (ReferenceEquals(_selectedColor, b)) return;
            var solidColorBrush = b.Background as SolidColorBrush;
            if (solidColorBrush == null)
                return;

            var ani = new ColorAnimation(solidColorBrush.Color, Duration3);

            MainInkCanvas.DefaultDrawingAttributes.Color = solidColorBrush.Color;
            brushPreview.Background.BeginAnimation(SolidColorBrush.ColorProperty, ani);
            b.IsActived = true;
            if (_selectedColor != null)
                _selectedColor.IsActived = false;
            _selectedColor = b;

            _drawerTextBox.Foreground = solidColorBrush;
        }
        private void SetBrushSize(double s)
        {
            MainInkCanvas.DefaultDrawingAttributes.Height = s;
            MainInkCanvas.DefaultDrawingAttributes.Width = s;
            brushPreview?.BeginAnimation(HeightProperty, new DoubleAnimation(s, Duration4));
            brushPreview?.BeginAnimation(WidthProperty, new DoubleAnimation(s, Duration4));
        }
        private void SetOrientation(bool v)
        {
            PaletteRotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(v ? -90 : 0, Duration4));
            Palette.BeginAnimation(MinWidthProperty, new DoubleAnimation(v ? 90 : 0, Duration4));
            //PaletteGrip.BeginAnimation(WidthProperty, new DoubleAnimation((double)Application.Current.Resources[v ? "VerticalModeGrip" : "HorizontalModeGrip"], Duration3));
            //BasicButtonPanel.BeginAnimation(WidthProperty, new DoubleAnimation((double)Application.Current.Resources[v ? "VerticalModeFlowPanel" : "HorizontalModeFlowPanel"], Duration3));
            //PaletteFlowPanel.BeginAnimation(WidthProperty, new DoubleAnimation((double)Application.Current.Resources[v ? "VerticalModeFlowPanel" : "HorizontalModeFlowPanel"], Duration3));
            //ColorPickersPanel.BeginAnimation(WidthProperty, new DoubleAnimation((double)Application.Current.Resources[v ? "VerticalModeColorPickersPanel" : "HorizontalModeColorPickersPanel"], Duration3));
            _displayOrientation = v;
        }
        private void SetTopMost(bool v)
        {
            //PinButton.IsActived = v;
            Topmost = v;
        }
        #endregion


        #region /---------IO---------/
        //private StrokeCollection _preLoadStrokes = null;
        //private void QuickSave(string filename = "QuickSave_")
        //{
        //    Save(new FileStream("Save\\" + filename + GenerateFileName(), FileMode.OpenOrCreate));
        //}
        //private void Save(Stream fs)
        //{
        //    try
        //    {
        //        if (fs == Stream.Null) return;
        //        MainInkCanvas.Strokes.Save(fs);
        //        _saved = true;
        //        Display("画板保存成功");
        //        fs.Close();
        //    }
        //    catch (Exception ex)
        //    {
        //        MessageBox.Show(ex.ToString());
        //        Display("画板保存失败");
        //    }
        //}
        //private StrokeCollection Load(Stream fs)
        //{
        //    try
        //    {
        //        return new StrokeCollection(fs);
        //    }
        //    catch (Exception ex)
        //    {
        //        MessageBox.Show(ex.ToString());
        //        Display("画板加载失败");
        //    }
        //    return new StrokeCollection();
        //}
        //private void AnimatedReload(StrokeCollection sc)
        //{
        //    _preLoadStrokes = sc;
        //    var ani = new DoubleAnimation(0, Duration3);
        //    ani.Completed += LoadAniCompleted;
        //    MainInkCanvas.BeginAnimation(OpacityProperty, ani);
        //}
        //private void LoadAniCompleted(object sender, EventArgs e)
        //{
        //    if (_preLoadStrokes == null) return;
        //    MainInkCanvas.Strokes = _preLoadStrokes;
        //    Display("Loaded");
        //    _saved = true;
        //    ClearHistory();
        //    MainInkCanvas.BeginAnimation(OpacityProperty, new DoubleAnimation(1, Duration3));
        //}

        //private static string[] GetSavePathList()
        //{
        //    return Directory.GetFiles("Save", "*.fdw");
        //}
        //private static string GetFileNameFromPath(string path)
        //{
        //    return Path.GetFileName(path);
        //}
        #endregion


        #region /---------Generator---------/
        //private static string GenerateFileName(string fileExt = ".fdw")
        //{
        //    return DateTime.Now.ToString("yyyyMMdd-HHmmss") + fileExt;
        //}

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
        #endregion

        #region Shape Drawer
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
                //textBlock.TextWrapping = TextWrapping.NoWrap;
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

            //throw new NotImplementedException();
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

                if (_drawerLastStroke != null /*&& _mode != DrawMode.Ray*/ && _mode != DrawMode.Text)
                {
                    StrokeCollection collection = new StrokeCollection();
                    collection.Add(_drawerLastStroke);
                    Push(_history, new StrokesHistoryNode(collection, StrokesHistoryNodeType.Added));
                }

                if (_drawerLastStroke != null && (/*_mode == DrawMode.Ray ||*/ _mode == DrawMode.Text))
                {
                    //us animation?
                    /*
                    var ani = new DoubleAnimation(1, 1, Duration4);
                    ani.Completed += (obj,arg)=> { MainInkCanvas.Strokes.Remove(_drawerLastStroke); };
                    MainInkCanvas.BeginAnimation(OpacityProperty, ani);
                    */
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
                drawingAttributes.FitToCurve = false;//must be false,other wise rectangle can not be drawed correct

                StylusPointCollection point = new StylusPointCollection(pointList);
                stroke = new Stroke(point)
                {
                    DrawingAttributes = drawingAttributes,
                };
            }
            //else if (_mode == DrawMode.Ray)
            //{
            //    //high lighter is ray line
            //    drawingAttributes.IsHighlighter = true;

            //    List<Point> pointList = new List<Point>
            //    {
            //        new Point(_drawerIntPos.X, _drawerIntPos.Y),
            //        new Point(endP.X, endP.Y),
            //    };

            //    StylusPointCollection point = new StylusPointCollection(pointList);
            //    stroke = new Stroke(point)
            //    {
            //        DrawingAttributes = drawingAttributes,
            //    };
            //}
            else if (_mode == DrawMode.Line)
            {
                List<Point> pointList = new List<Point>
                {
                    new Point(_drawerIntPos.X, _drawerIntPos.Y),
                    new Point(endP.X, endP.Y),
                };

                StylusPointCollection point = new StylusPointCollection(pointList);
                stroke = new Stroke(point)
                {
                    DrawingAttributes = drawingAttributes,
                };
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
                    new Point(endP.X , endP.Y),
                    new Point(endP.X + (w*cost - h*sint), endP.Y + (w*sint + h*cost)),
                    new Point(endP.X,endP.Y),
                    new Point(endP.X + (w*cost + h*sint), endP.Y - (h*cost - w*sint)),
                };

                StylusPointCollection point = new StylusPointCollection(pointList);

                drawingAttributes.FitToCurve = false;//must be false,other wise rectangle can not be drawed correct

                stroke = new Stroke(point)
                {
                    DrawingAttributes = drawingAttributes,
                };
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

                drawingAttributes.FitToCurve = false;//must be false,other wise rectangle can not be drawed correct

                StylusPointCollection point = new StylusPointCollection(pointList);
                stroke = new Stroke(point)
                {
                    DrawingAttributes = drawingAttributes,
                };
            }
            else if (_mode == DrawMode.Circle)
            {
                List<Point> pointList = GenerateEclipseGeometry(_drawerIntPos, endP);
                StylusPointCollection point = new StylusPointCollection(pointList);
                stroke = new Stroke(point)
                {
                    DrawingAttributes = drawingAttributes
                };
            }

            if (_drawerLastStroke != null)
                MainInkCanvas.Strokes.Remove(_drawerLastStroke);

            if (stroke != null)
                MainInkCanvas.Strokes.Add(stroke);

            _drawerLastStroke = stroke;
        }

        #endregion


        #region /---------Helper---------/
        private string _staticInfo = "";
        private bool _displayingInfo;

        private async void Display(string info)
        {
            InfoBox.Text = info;
            _displayingInfo = true;
            await InfoDisplayTimeUp(new Progress<string>(box => InfoBox.Text = box));
        }
        private Task InfoDisplayTimeUp(IProgress<string> box)
        {
            return Task.Run(() =>
            {
                Task.Delay(2000).Wait();
                box.Report(_staticInfo);
                _displayingInfo = false;
            });
        }
        //private void SetStaticInfo(string info)
        //{
        //    _staticInfo = info;
        //    if (!_displayingInfo)
        //        InfoBox.Text = _staticInfo;
        //}

        //private static Stream SaveDialog(string initFileName, string fileExt = ".fdw", string filter = "Free Draw Save (*.fdw)|*fdw")
        //{
        //    var dialog = new Microsoft.Win32.SaveFileDialog()
        //    {
        //        DefaultExt = fileExt,
        //        Filter = filter,
        //        FileName = initFileName,
        //        InitialDirectory = Directory.GetCurrentDirectory() + "Save"
        //    };
        //    return dialog.ShowDialog() == true ? dialog.OpenFile() : Stream.Null;
        //}
        //private static Stream OpenDialog(string fileExt = ".fdw", string filter = "Free Draw Save (*.fdw)|*fdw")
        //{
        //    var dialog = new Microsoft.Win32.OpenFileDialog()
        //    {
        //        DefaultExt = fileExt,
        //        Filter = filter,
        //    };
        //    return dialog.ShowDialog() == true ? dialog.OpenFile() : Stream.Null;
        //}
        #endregion


        #region /---------Ink---------/
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
        }

        private void AnimatedClear()
        {
            //no need any more
            //if (!PromptToSave()) return;
            var ani = new DoubleAnimation(0, Duration3);
            ani.Completed += ClearAniComplete; ;
            MainInkCanvas.BeginAnimation(OpacityProperty, ani);
        }
        private void ClearAniComplete(object sender, EventArgs e)
        {
            Clear();
            Display("Cleared");
            MainInkCanvas.BeginAnimation(OpacityProperty, new DoubleAnimation(1, Duration3));
        }
        #endregion

        #region /---------Color Picker------/
        private void ColorPickers_Click(object sender, RoutedEventArgs e)
        {
            var border = sender as ColorPicker;
            if (border == null) return;
            SetColor(border);
        }
        #endregion

        #region /---------Extra Button---------/
        //private void PinButton_Click(object sender, RoutedEventArgs e)
        //{
        //    SetTopMost(!Topmost);
        //}
        //private void SaveButton_Click(object sender, RoutedEventArgs e)
        //{
        //    if (MainInkCanvas.Strokes.Count == 0)
        //    {
        //        Display("没有笔迹要保存");
        //        return;
        //    }
        //    QuickSave();
        //}
        //private void SaveButton_RightClick(object sender, MouseButtonEventArgs e)
        //{
        //    if (MainInkCanvas.Strokes.Count == 0)
        //    {
        //        Display("没有笔迹要保存");
        //        return;
        //    }
        //    Save(SaveDialog(GenerateFileName()));
        //}
        //private void LoadButton_Click(object sender, RoutedEventArgs e)
        //{
        //    if (!PromptToSave()) return;
        //    var s = OpenDialog();
        //    if (s == Stream.Null) return;
        //    AnimatedReload(Load(s));
        //}
        //private void ExportButton_Click(object sender, RoutedEventArgs e)
        //{
        //    if (MainInkCanvas.Strokes.Count == 0)
        //    {
        //        Display("没有笔迹要保存");
        //        return;
        //    }
        //    try
        //    {
        //        var s = SaveDialog("ImageExport_" + GenerateFileName(".png"), ".png",
        //            "Portable Network Graphics (*png)|*png");
        //        if (s == Stream.Null) return;
        //        var rtb = new RenderTargetBitmap((int)MainInkCanvas.ActualWidth, (int)MainInkCanvas.ActualHeight, 96d,
        //            96d, PixelFormats.Pbgra32);
        //        rtb.Render(MainInkCanvas);
        //        var encoder = new PngBitmapEncoder();
        //        encoder.Frames.Add(BitmapFrame.Create(rtb));
        //        encoder.Save(s);
        //        s.Close();
        //        Display("图片导出完成");
        //    }
        //    catch (Exception ex)
        //    {
        //        MessageBox.Show(ex.ToString());
        //        Display("图片导出失败");
        //    }
        //}
        //private delegate void NoArgDelegate();
        //private void ExportButton_RightClick(object sender, MouseButtonEventArgs e)
        //{
        //    if (MainInkCanvas.Strokes.Count == 0)
        //    {
        //        Display("没有笔迹要保存");
        //        return;
        //    }
        //    try
        //    {
        //        var s = SaveDialog("ImageExportWithBackground_" + GenerateFileName(".png"), ".png", "Portable Network Graphics (*png)|*png");
        //        if (s == Stream.Null) return;
        //        Palette.Opacity = 0;
        //        Palette.Dispatcher.Invoke(DispatcherPriority.Render, (NoArgDelegate)delegate { });
        //        Thread.Sleep(100);
        //        var fromHwnd = Graphics.FromHwnd(IntPtr.Zero);
        //        var w = (int)(SystemParameters.PrimaryScreenWidth * fromHwnd.DpiX / 96.0);
        //        var h = (int)(SystemParameters.PrimaryScreenHeight * fromHwnd.DpiY / 96.0);
        //        var image = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        //        Graphics.FromImage(image).CopyFromScreen(0, 0, 0, 0, new System.Drawing.Size(w, h), CopyPixelOperation.SourceCopy);
        //        image.Save(s, ImageFormat.Png);
        //        Palette.Opacity = 1;
        //        s.Close();
        //        Display("图片导出成功");
        //    }
        //    catch (Exception ex)
        //    {
        //        MessageBox.Show(ex.ToString());
        //        Display("图片导出失败");
        //    }
        //}
        #endregion

        #region /---------Tool Button------/
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

        //private void RayButton_Click(object sender, RoutedEventArgs e)
        //{
        //    SetEnable(true, DrawMode.Ray);
        //}

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
        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            AnimatedClear();
        }

        #endregion

        #region /---------UI---------/
        //private void DetailToggler_Click(object sender, RoutedEventArgs e)
        //{
        //    SetExtralToolPanel(!_displayExtraToolPanel);
        //}
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            //Topmost = false;
            //var anim = new DoubleAnimation(0, Duration3);
            //anim.Completed += Exit;
            //BeginAnimation(OpacityProperty, anim);
            this.Close();
        }

        private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            //SetBrushSize(e.NewValue);
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }
        private void HideButton_Click(object sender, RoutedEventArgs e)
        {
            SetInkVisibility(!_inkVisibility);
        }

        private void OrientationButton_Click(object sender, RoutedEventArgs e)
        {
            SetOrientation(!_displayOrientation);
        }
        #endregion


        #region  /---------Docking---------/

        //enum DockingDirection
        //{
        //    None,
        //    Top,
        //    Left,
        //    Right
        //}
        //private int _dockingEdgeThreshold = 30;
        //private int _dockingAwaitTime = 10000;
        //private int _dockingSideIndent = 290;
        //private void AnimatedCanvasMoving(UIElement ctr, Point to, Duration dur)
        //{
        //    ctr.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(Canvas.GetTop(ctr), to.Y, dur));
        //    ctr.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(Canvas.GetLeft(ctr), to.X, dur));
        //}

        //private DockingDirection CheckDocking()
        //{
        //    var left = Canvas.GetLeft(Palette);
        //    var right = Canvas.GetRight(Palette);
        //    var top = Canvas.GetTop(Palette);

        //    if (left > 0 && left < _dockingEdgeThreshold)
        //        return DockingDirection.Left;
        //    if (right > 0 && right < _dockingEdgeThreshold)
        //        return DockingDirection.Right;
        //    if (top > 0 && top < _dockingEdgeThreshold)
        //        return DockingDirection.Top;
        //    return DockingDirection.None;
        //}

        //private void RightDocking()
        //{
        //    AnimatedCanvasMoving(Palette, new Point(ActualWidth + _dockingSideIndent, Canvas.GetTop(Palette)), Duration5);
        //}
        //private void LeftDocking()
        //{
        //    AnimatedCanvasMoving(Palette, new Point(0 - _dockingSideIndent, Canvas.GetTop(Palette)), Duration5);
        //}
        //private void TopDocking()
        //{

        //}

        //private async void AwaitDocking()
        //{

        //    await Docking();
        //}

        //private Task Docking()
        //{
        //    return Task.Run(() =>
        //    {
        //        Thread.Sleep(_dockingAwaitTime);
        //        var direction = CheckDocking();
        //        if (direction == DockingDirection.Left) LeftDocking();
        //        if (direction == DockingDirection.Right) RightDocking();
        //        if (direction == DockingDirection.Top) TopDocking();
        //    });
        //}
        #endregion


        #region /---------Dragging---------/
        private Point _lastMousePosition;
        private bool _isDraging;
        private bool _tempEnable;

        private void StartDrag()
        {
            _lastMousePosition = Mouse.GetPosition(this);
            _isDraging = true;
            Palette.Background = new SolidColorBrush(Colors.Transparent);
            _tempEnable = _enable;
            SetEnable(true, _mode);
        }
        private void EndDrag()
        {
            if (_isDraging == true)
            {
                SetEnable(_tempEnable, _mode);
            }

            _isDraging = false;
            Palette.Background = null;
        }
        private void PaletteGrip_MouseDown(object sender, MouseButtonEventArgs e)
        {
            StartDrag();
        }
        private void Palette_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDraging) return;
            var currentMousePosition = Mouse.GetPosition(this);
            var offset = currentMousePosition - _lastMousePosition;

            Canvas.SetTop(Palette, Canvas.GetTop(Palette) + offset.Y);
            Canvas.SetLeft(Palette, Canvas.GetLeft(Palette) + offset.X);

            _lastMousePosition = currentMousePosition;
        }
        private void Palette_MouseUp(object sender, MouseButtonEventArgs e)
        {
            EndDrag();
        }
        private void Palette_MouseLeave(object sender, MouseEventArgs e)
        {
            EndDrag();
        }



        #endregion

        private void FontReduceButton_Click(object sender, RoutedEventArgs e)
        {
            _drawerTextBox.FontSize = _drawerTextBox.FontSize - 2;

            _drawerTextBox.FontSize = Math.Max(14, _drawerTextBox.FontSize);

            Display("Reduce Font -2");
        }

        private void FontIncreaseButton_Click(object sender, RoutedEventArgs e)
        {
            _drawerTextBox.FontSize = _drawerTextBox.FontSize + 2;

            _drawerTextBox.FontSize = Math.Min(60, _drawerTextBox.FontSize);

            Display("Increase Font +2");
        }
    }
}

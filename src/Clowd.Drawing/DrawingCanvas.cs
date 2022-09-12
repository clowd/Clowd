using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Clowd.Drawing.Tools;
using Clowd.Drawing.Graphics;
using Clowd.UI.Helpers;
using DependencyPropertyGenerator;
using RT.Util.ExtensionMethods;

namespace Clowd.Drawing
{
    [DependencyProperty<ToolType>("Tool", DefaultValue = ToolType.Pointer)]
    [DependencyProperty<Color>("ArtworkBackground")]
    [DependencyProperty<double>("LineWidth", DefaultValue = 2d)]
    [DependencyProperty<Color>("ObjectColor")]
    [DependencyProperty<Color>("HandleColor")]
    [DependencyProperty<string>("TextFontFamilyName", DefaultValue = "Tahoma")]
    [DependencyProperty<FontStyle>("TextFontStyle")]
    [DependencyProperty<FontWeight>("TextFontWeight")]
    [DependencyProperty<FontStretch>("TextFontStretch")]
    [DependencyProperty<double>("TextFontSize", DefaultValue = 12d)]
    [DependencyProperty<bool>("IsPanning")]
    [DependencyProperty<Point>("ContentOffset")]
    [DependencyProperty<double>("ContentScale", DefaultValue = 1d)]
    public partial class DrawingCanvas : Canvas
    {
        public GraphicBase this[int index]
        {
            get
            {
                if (index >= 0 && index < Count)
                    return GraphicsList[index];
                return null;
            }
        }

        public int SelectionCount => SelectedItems.Count();

        public GraphicCollection GraphicsList
        {
            get => _graphicsList;
            set
            {
                if (_graphicsList != null)
                {
                    RemoveVisualChild(_graphicsList.BackgroundVisual);
                    _graphicsList.Clear();
                }

                _graphicsList = value;
                AddVisualChild(_graphicsList.BackgroundVisual);
            }
        }

        public int Count => GraphicsList.Count;

        public IEnumerable<GraphicBase> SelectedItems => GraphicsList.Where(g => g.IsSelected);

        internal ToolPointer ToolPointer;

        private GraphicCollection _graphicsList;
        private ToolBase[] _tools;
        private Border _clickable;
        private ContextMenu _contextMenu;
        private UndoManager _undoManager;

        public RelayCommand CommandSelectAll { get; }
        public RelayCommand CommandUnselectAll { get; }
        public RelayCommand CommandDelete { get; }
        public RelayCommand CommandDeleteAll { get; }
        public RelayCommand CommandMoveToFront { get; }
        public RelayCommand CommandMoveToBack { get; }
        public RelayCommand CommandMoveForward { get; }
        public RelayCommand CommandMoveBackward { get; }
        public RelayCommand CommandResetRotation { get; }
        public RelayCommand CommandUndo { get; }
        public RelayCommand CommandRedo { get; }
        public RelayCommand CommandZoomPanAuto { get; }
        public RelayCommand CommandZoomPanActualSize { get; }

        public DrawingCanvas()
        {
            _graphicsList = new GraphicCollection(this);

            // create array of drawing tools
            _tools = new ToolBase[(int)ToolType.Max];

            ToolPointer = new ToolPointer();
            _tools[(int)ToolType.None] = ToolPointer;
            _tools[(int)ToolType.Pointer] = ToolPointer;

            _tools[(int)ToolType.Rectangle] = new ToolDraggable<GraphicRectangle>(
                Resource.CursorRectangle,
                point => new GraphicRectangle(ObjectColor, LineWidth, new Rect(point, new Size(1, 1))),
                (point, g) => g.MoveHandleTo(point, 5),
                snapMode: SnapMode.Diagonal);

            _tools[(int)ToolType.FilledRectangle] = new ToolDraggable<GraphicFilledRectangle>(
                Resource.CursorRectangle,
                point => new GraphicFilledRectangle(ObjectColor, new Rect(point, new Size(1, 1))),
                (point, g) => g.MoveHandleTo(point, 5),
                snapMode: SnapMode.Diagonal);

            _tools[(int)ToolType.Ellipse] = new ToolDraggable<GraphicEllipse>(
                Resource.CursorEllipse,
                point => new GraphicEllipse(ObjectColor, LineWidth, new Rect(point, new Size(1, 1))),
                (point, g) => g.MoveHandleTo(point, 5),
                snapMode: SnapMode.Diagonal);

            _tools[(int)ToolType.Line] = new ToolDraggable<GraphicLine>(
                Resource.CursorLine,
                point => new GraphicLine(ObjectColor, LineWidth, point, point),
                (point, g) => g.MoveHandleTo(point, 2),
                snapMode: SnapMode.All);

            _tools[(int)ToolType.Arrow] = new ToolDraggable<GraphicArrow>(
                Resource.CursorArrow,
                point => new GraphicArrow(ObjectColor, LineWidth, point, point),
                (point, g) => g.MoveHandleTo(point, 2),
                snapMode: SnapMode.All);

            _tools[(int)ToolType.PolyLine] = new ToolPolyLine();
            _tools[(int)ToolType.Text] = new ToolText();
            _tools[(int)ToolType.Count] = new ToolCount();
            _tools[(int)ToolType.Pixelate] = new ToolPixelate();

            _undoManager = new UndoManager(this);
            _undoManager.StateChanged += (_, _) => UpdateState();

            CommandSelectAll = new RelayCommand(this)
            {
                Executed = (obj) => SelectAll(),
                CanExecute = (obj) => Count > 0,
                Text = "_Select all",
                Gesture = new SimpleKeyGesture(Key.A, ModifierKeys.Control),
            };
            CommandUnselectAll = new RelayCommand(this)
            {
                Executed = (obj) => UnselectAll(),
                CanExecute = (obj) => SelectedItems.Any(),
                Text = "Unselect all",
                Gesture = new SimpleKeyGesture(Key.Escape),
            };
            CommandDelete = new RelayCommand(this)
            {
                Executed = (obj) => Delete(),
                CanExecute = (obj) => SelectedItems.Any(),
                Text = "_Delete",
                Gesture = new SimpleKeyGesture(Key.Delete),
            };
            CommandDeleteAll = new RelayCommand(this)
            {
                Executed = (obj) => DeleteAll(),
                CanExecute = (obj) => Count > 0,
                Text = "Delete all",
            };
            CommandMoveToFront = new RelayCommand(this)
            {
                Executed = (obj) => MoveToFront(),
                CanExecute = (obj) => SelectedItems.Any(),
                Text = "Move to front",
                Gesture = new SimpleKeyGesture(Key.Home),
            };
            CommandMoveToBack = new RelayCommand(this)
            {
                Executed = (obj) => MoveToBack(),
                CanExecute = (obj) => SelectedItems.Any(),
                Text = "Move to back",
                Gesture = new SimpleKeyGesture(Key.End),
            };
            CommandMoveForward = new RelayCommand(this)
            {
                Executed = (obj) => MoveForward(),
                CanExecute = (obj) => SelectedItems.Any(),
                Text = "Move forward",
                Gesture = new SimpleKeyGesture(Key.Home, ModifierKeys.Control),
            };
            CommandMoveBackward = new RelayCommand(this)
            {
                Executed = (obj) => MoveBackward(),
                CanExecute = (obj) => SelectedItems.Any(),
                Text = "Move backward",
                Gesture = new SimpleKeyGesture(Key.End, ModifierKeys.Control),
            };
            CommandResetRotation = new RelayCommand(this)
            {
                Executed = (obj) => ResetRotation(),
                CanExecute = (obj) => SelectedItems.Any(),
                Text = "Reset rotation",
            };
            CommandZoomPanAuto = new RelayCommand(this)
            {
                Executed = (obj) => ZoomPanAuto(),
                CanExecute = (obj) => Count > 0,
                Text = "Zoom to fit content",
                GestureText = "Ctrl+0",
            };
            CommandZoomPanActualSize = new RelayCommand(this)
            {
                Executed = (obj) => ZoomPanActualSize(),
                CanExecute = (obj) => Count > 0,
                Text = "Zoom to actual size",
                GestureText = "Ctrl+1"
            };
            CommandUndo = new RelayCommand(this)
            {
                Executed = (obj) => _undoManager.Undo(),
                CanExecute = (obj) => _undoManager.CanUndo,
                Text = "_Undo",
                Gesture = new SimpleKeyGesture(Key.Z, ModifierKeys.Control),
            };
            CommandRedo = new RelayCommand(this)
            {
                Executed = (obj) => _undoManager.Redo(),
                CanExecute = (obj) => _undoManager.CanRedo,
                Text = "_Redo",
                Gesture = new SimpleKeyGesture(Key.Y, ModifierKeys.Control),
            };

            this.InputBindings.Add(new KeyBinding(CommandZoomPanAuto, Key.D0, ModifierKeys.Control));
            this.InputBindings.Add(new KeyBinding(CommandZoomPanAuto, Key.NumPad0, ModifierKeys.Control));
            this.InputBindings.Add(new KeyBinding(CommandZoomPanActualSize, Key.D1, ModifierKeys.Control));
            this.InputBindings.Add(new KeyBinding(CommandZoomPanActualSize, Key.NumPad1, ModifierKeys.Control));

            ContextMenu = new ContextMenu();
            ContextMenu.PlacementTarget = this;
            ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            ContextMenu.Items.Add(CommandSelectAll.CreateMenuItem());
            ContextMenu.Items.Add(CommandUnselectAll.CreateMenuItem());
            ContextMenu.Items.Add(CommandDelete.CreateMenuItem());
            ContextMenu.Items.Add(CommandDeleteAll.CreateMenuItem());
            ContextMenu.Items.Add(new Separator());
            ContextMenu.Items.Add(CommandMoveToFront.CreateMenuItem());
            ContextMenu.Items.Add(CommandMoveForward.CreateMenuItem());
            ContextMenu.Items.Add(CommandMoveToBack.CreateMenuItem());
            ContextMenu.Items.Add(CommandMoveBackward.CreateMenuItem());
            ContextMenu.Items.Add(new Separator());
            ContextMenu.Items.Add(CommandResetRotation.CreateMenuItem());
            ContextMenu.Items.Add(new Separator());
            ContextMenu.Items.Add(CommandZoomPanAuto.CreateMenuItem());
            ContextMenu.Items.Add(CommandZoomPanActualSize.CreateMenuItem());

            this.FocusVisualStyle = null;

            this.Loaded += DrawingCanvas_Loaded;
            this.MouseDown += DrawingCanvas_MouseDown;
            this.MouseMove += DrawingCanvas_MouseMove;
            this.MouseUp += DrawingCanvas_MouseUp;
            this.KeyDown += DrawingCanvas_KeyDown;
            this.KeyUp += DrawingCanvas_KeyUp;
            this.LostMouseCapture += DrawingCanvas_LostMouseCapture;
            this.MouseWheel += DrawingCanvas_MouseWheel;

            InitializeZoom();

            _clickable = new Border();
            _clickable.Background = (Brush)FindResource("CheckeredLargeLightWhiteBackgroundBrush");
            Children.Add(_clickable);

            SnapsToDevicePixels = false;
            UseLayoutRounding = false;
        }

        partial void OnToolChanged(ToolType newValue)
        {
            // Set cursor immediately - important when tool is selected from the menu
            var tmp = _tools[(int)newValue];
            if (tmp == null)
                Cursor = Cursors.SizeAll;
            else
                tmp.SetCursor(this);
        }

        partial void OnArtworkBackgroundChanged(Color newValue)
        {
            GraphicsList.BackgroundBrush = new SolidColorBrush(newValue);
        }
        partial void OnLineWidthChanged(double newValue)
        {
            ApplyGraphicPropertyChange<GraphicBase, double>(newValue, t => t.LineWidth, (t, v) => t.LineWidth = v);
        }

        partial void OnObjectColorChanged(Color newValue)
        {
            ApplyGraphicPropertyChange<GraphicBase, Color>(newValue, t => t.ObjectColor, (t, v) => t.ObjectColor = v);
        }

        partial void OnHandleColorChanged(Color newValue)
        {
            GraphicBase.HandleBrush = new SolidColorBrush(newValue);
        }

        partial void OnTextFontFamilyNameChanged(string newValue)
        {
            ApplyGraphicPropertyChange<GraphicText, string>(newValue, t => t.FontName, (t, v) => t.FontName = v);
        }

        partial void OnTextFontStyleChanged(FontStyle newValue)
        {
            ApplyGraphicPropertyChange<GraphicText, FontStyle>(newValue, t => t.FontStyle, (t, v) => t.FontStyle = v);
        }

        partial void OnTextFontWeightChanged(FontWeight newValue)
        {
            ApplyGraphicPropertyChange<GraphicText, FontWeight>(newValue, t => t.FontWeight, (t, v) => t.FontWeight = v);
        }

        partial void OnTextFontStretchChanged(FontStretch newValue)
        {
            ApplyGraphicPropertyChange<GraphicText, FontStretch>(newValue, t => t.FontStretch, (t, v) => t.FontStretch = v);
        }

        partial void OnTextFontSizeChanged(double newValue)
        {
            ApplyGraphicPropertyChange<GraphicText, double>(newValue, t => t.FontSize, (t, v) => t.FontSize = v);
        }

        private void ApplyGraphicPropertyChange<TType, T>(T newValue, Func<TType, T> getTextProp, Action<TType, T> setTextProp) where TType : GraphicBase
        {
            bool wasChange = false;

            foreach (GraphicBase g in SelectedItems)
            {
                if (g is TType obj)
                {
                    if (!Equals(getTextProp(obj), newValue))
                    {
                        setTextProp(obj, newValue);
                        wasChange = true;
                    }
                }
            }

            if (wasChange)
            {
                AddCommandToHistory();
            }
        }

        public BitmapSource DrawGraphicsToBitmap() => GraphicsList.DrawGraphicsToBitmap();

        public DrawingVisual DrawGraphicsToVisual() => GraphicsList.DrawGraphicsToVisual();

        public byte[] SerializeGraphics(bool selectedOnly) => GraphicsList.SerializeObjects(selectedOnly);

        public void DeserializeGraphics(byte[] graphics)
        {
            GraphicsList.DeserializeObjectsInto(graphics);
            _undoManager.AddCommandStep();
        }

        public void AddGraphic(GraphicBase g)
        {
            // center the object in the current viewport
            var itemBounds = g.Bounds;
            var transformX = (-itemBounds.Left - itemBounds.Width / 2) + ((ActualWidth / 2 - ContentOffset.X) / ContentScale);
            var transformY = (-itemBounds.Top - itemBounds.Height / 2) + ((ActualHeight / 2 - ContentOffset.Y) / ContentScale);
            g.Move(transformX, transformY);

            // only the newly added item should be selected
            this.UnselectAll();
            g.IsSelected = true;
            g.Normalize();
            this.GraphicsList.Add(g);

            AddCommandToHistory();
            UpdateState();
        }

        public void SelectAll()
        {
            for (int i = 0; i < this.Count; i++)
            {
                this[i].IsSelected = true;
            }

            UpdateState();
        }

        public void UnselectAll()
        {
            for (int i = 0; i < this.Count; i++)
            {
                this[i].IsSelected = false;
            }

            UpdateState();
        }

        public void UnselectAllExcept(params GraphicBase[] excluded)
        {
            foreach (var ob in this.SelectedItems.Except(excluded.Where(ex => ex != null)))
            {
                ob.IsSelected = false;
            }

            UpdateState();
        }

        public void Delete()
        {
            bool wasChange = false;

            for (int i = this.Count - 1; i >= 0; i--)
            {
                if (this[i].IsSelected)
                {
                    this.GraphicsList.RemoveAt(i);
                    wasChange = true;
                }
            }

            if (wasChange)
            {
                AddCommandToHistory();
            }

            UpdateState();
        }

        public void DeleteAll()
        {
            if (GraphicsList.Count > 0)
            {
                GraphicsList.Clear();
                AddCommandToHistory();
            }

            UpdateState();
        }

        public void MoveToFront()
        {
            MoveToIndex(int.MaxValue);
        }

        public void MoveForward()
        {
            int idx = GraphicsList.IndexOf(b => b.IsSelected);
            if (idx >= 0)
            {
                MoveToIndex(idx + 1);
            }
        }

        public void MoveBackward()
        {
            int idx = GraphicsList.IndexOf(b => b.IsSelected);
            if (idx >= 0)
            {
                MoveToIndex(idx == 0 ? 0 : idx - 1);
            }
        }

        public void MoveToBack()
        {
            MoveToIndex(0);
        }

        private void MoveToIndex(int idx)
        {
            List<GraphicBase> list = new List<GraphicBase>();

            for (int i = Count - 1; i >= 0; i--)
            {
                if (this[i].IsSelected)
                {
                    list.Add(this[i]);
                    GraphicsList.RemoveAt(i);
                }
            }

            var shouldAdd = idx > GraphicsList.Count;

            if (list.Count > 0)
            {
                foreach (GraphicBase g in list)
                {
                    if (shouldAdd)
                    {
                        GraphicsList.Add(g);
                    }
                    else
                    {
                        GraphicsList.Insert(idx, g);
                    }
                }
                AddCommandToHistory();
            }

            UpdateState();
        }

        public void ResetRotation()
        {
            ApplyGraphicPropertyChange<GraphicRectangle, double>(0, t => t.Angle, (t, v) => t.Angle = v);
        }

        public void Undo()
        {
            _undoManager.Undo();
            UpdateState();
        }

        public void Redo()
        {
            _undoManager.Redo();
            UpdateState();
        }

        protected override int VisualChildrenCount => (GraphicsList?.VisualCount ?? 0) + Children.Count;

        internal void InternalAddVisualChild(Visual child) => AddVisualChild(child);

        internal void InternalRemoveVisualChild(Visual child) => RemoveVisualChild(child);

        protected override Visual GetVisualChild(int index)
        {
            // _clickable and _artworkbounds come first,
            // any other children come after.

            if (index == 0)
            {
                return _clickable;
            }
            // else if (index == 1)
            // {
            //     return _artworkRectangle;
            // }
            else if (index - 1 < GraphicsList.VisualCount)
            {
                return GraphicsList.GetVisual(index - 1);
            }
            else if (index - 1 - GraphicsList.VisualCount < Children.Count)
            {
                // any other children.
                return Children[index - GraphicsList.VisualCount];
            }

            throw new ArgumentOutOfRangeException("index");
        }

        void DrawingCanvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (IsPanning)
                return;

            this.Focus();

            if (e.ChangedButton == MouseButton.Left)
            {
                if (e.ClickCount == 2)
                {
                    // on double click, execute GraphicBase.Activate().
                    // this allows GraphicText to launch an editor etc.
                    Point point = e.GetPosition(this);
                    var clicked = ToolPointer.MakeHitTest(this, point, out var handleNum);
                    if (clicked != null)
                        clicked.Activate(this);
                }
                else if (Tool == ToolType.None || _tools[(int)Tool] == null)
                {
                    StartPanning(e);
                }
                else
                {
                    _tools[(int)Tool].OnMouseDown(this, e);
                }

                UpdateState();
            }
            else if (e.ChangedButton == MouseButton.Right)
            {
                // fake a mouse up for left mouse button if user is in the middle of an operation
                var newArgs = new MouseButtonEventArgs(e.MouseDevice, e.Timestamp, MouseButton.Left, e.StylusDevice);
                DrawingCanvas_MouseUp(sender, newArgs);
                Tool = ToolType.Pointer;

                // Change current selection if necessary
                Point point = e.GetPosition(this);
                var hitObject = ToolPointer.MakeHitTest(this, point, out var _hn);
                if (hitObject == null)
                {
                    UnselectAll();
                }
                else if (!hitObject.IsSelected)
                {
                    UnselectAll();
                    hitObject.IsSelected = true;
                }

                // we must update the state as we may have changed the selection
                UpdateState();
            }
        }

        void DrawingCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (IsPanning)
            {
                ContinuePanning(e);
                return;
            }

            if (_tools[(int)Tool] == null)
            {
                return;
            }

            if (e.MiddleButton == MouseButtonState.Released && e.RightButton == MouseButtonState.Released)
            {
                _tools[(int)Tool].OnMouseMove(this, e);
                UpdateState();
            }
            else
            {
                this.Cursor = HelperFunctions.DefaultCursor;
            }
        }

        void DrawingCanvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (IsPanning)
            {
                StopPanning(e);
                return;
            }

            if (_tools[(int)Tool] == null)
            {
                return;
            }

            if (e.ChangedButton == MouseButton.Left)
            {
                _tools[(int)Tool].OnMouseUp(this, e);
                UpdateState();
            }
        }

        void DrawingCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (IsPanning)
                return;

            double[] zoomStops = { 0.1, 0.25, 0.50, 0.75, 1, 1.5, 2, 3 };

            double newZoom = 0;

            if (ContentScale > 2.99)
            {
                newZoom = ContentScale + (e.Delta > 0 ? 1 : -1);
                if (newZoom > 10) newZoom = 0; // max zoom
            }
            else if (e.Delta > 0)
            {
                newZoom = zoomStops.Where(z => z > ContentScale).Min();
            }
            else if (e.Delta < 0 && ContentScale > 0.1)
            {
                newZoom = zoomStops.Where(z => z < ContentScale).Max();
            }

            if (newZoom == 0)
                return;

            Point relativeMouse = e.GetPosition(this);
            double absoluteX = relativeMouse.X * ContentScale + _translateTransform.X;
            double absoluteY = relativeMouse.Y * ContentScale + _translateTransform.Y;

            ContentScale = newZoom;
            ContentOffset = new Point(absoluteX - relativeMouse.X * ContentScale, absoluteY - relativeMouse.Y * ContentScale);
        }

        void DrawingCanvas_Loaded(object sender, RoutedEventArgs e)
        {
            AddVisualChild(_graphicsList.BackgroundVisual);
            this.Focusable = true; // to handle keyboard messages
            UpdateScaleTransform();
            UpdateClickableSurface();
        }

        void DrawingCanvas_LostMouseCapture(object sender, MouseEventArgs e)
        {
            if (this.IsMouseCaptured)
            {
                CancelCurrentOperation();
                UpdateState();
            }
        }

        void DrawingCanvas_KeyDown(object sender, KeyEventArgs e)
        {
            // Esc key stops currently active operation
            if (e.Key == Key.Escape)
            {
                if (this.IsMouseCaptured)
                {
                    CancelCurrentOperation();
                    UpdateState();
                }
            }

            // Shift key causes a MouseMove, so any drag-based snapping will be updated
            if (e.Key == Key.LeftShift || e.Key == Key.RightShift)
            {
                if (this.IsMouseCaptured)
                {
                    DrawingCanvas_MouseMove(this, new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount));
                }
            }
        }

        void DrawingCanvas_KeyUp(object sender, KeyEventArgs e)
        {
            // Shift key causes a MouseMove, so any drag-based snapping will be updated
            if (e.Key == Key.LeftShift || e.Key == Key.RightShift)
            {
                if (this.IsMouseCaptured)
                {
                    DrawingCanvas_MouseMove(this, new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount));
                }
            }
        }

        void CancelCurrentOperation()
        {
            if (Tool == ToolType.Pointer)
            {
                if (GraphicsList.Count > 0)
                {
                    if (GraphicsList[GraphicsList.Count - 1] is GraphicSelectionRectangle sel)
                    {
                        // Delete selection rectangle if it exists
                        GraphicsList.Remove(sel);
                    }
                    else
                    {
                        // Pointer tool moved or resized graphics object.
                        // Add this action to the history
                        AddCommandToHistory();
                    }
                }
            }
            else if (Tool > ToolType.Pointer && Tool < ToolType.Max)
            {
                // Delete last graphics object which is currently drawn
                GetTool(Tool).AbortOperation(this);
            }

            Tool = ToolType.Pointer;

            this.ReleaseMouseCapture();
            this.Cursor = HelperFunctions.DefaultCursor;
        }

        internal void AddCommandToHistory()
        {
            _undoManager.AddCommandStep();
        }

        void UpdateState()
        {
            CommandManager.InvalidateRequerySuggested();
        }

        internal ToolBase GetTool(ToolType type)
        {
            return _tools[(int)type];
        }

        partial void OnContentScaleChanged(double newValue)
        {
            UpdateScaleTransform();
            UpdateClickableSurface();
        }

        partial void OnContentOffsetChanged(Point newValue)
        {
            double dpiZoom = DpiZoom;
            _translateTransform.X = Math.Floor(newValue.X * dpiZoom) / dpiZoom;
            _translateTransform.Y = Math.Floor(newValue.Y * dpiZoom) / dpiZoom;
            UpdateClickableSurface();
        }

        // public Point WorldOffset => new Point((ActualWidth / 2 - ContentOffset.X) / ContentScale,
        //     (ActualHeight / 2 - ContentOffset.Y) / ContentScale);

        public DpiScale CanvasUiElementScale
        {
            get
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                return new DpiScale(dpi.DpiScaleX * (1 / ContentScale), dpi.DpiScaleY * (1 / ContentScale));
            }
        }

        private double DpiZoom => PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1;

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            bool isAutoFit = _isAutoFit;
            ContentOffset = new Point(
                ContentOffset.X + sizeInfo.NewSize.Width / 2 - sizeInfo.PreviousSize.Width / 2,
                ContentOffset.Y + sizeInfo.NewSize.Height / 2 - sizeInfo.PreviousSize.Height / 2);
            if (isAutoFit)
                ZoomPanAuto();
        }

        public void UpdateClickableSurface()
        {
            // _clickable is an element that simply spans the entire visible canvas area
            // this is necessary because the "real" canvas element may actually not even be on screen
            // (for example, if the current translation is large) and if that's the case, WPF will not
            // handle any mouse events.

            // the parallax calculation here is to give the effect that the background is moving when the 
            // canvas is being dragged (despite it actually being stationary and fixed to the viewport)
            double parallaxSize = 100 * _scaleTransform2.ScaleX;
            var xp = ((_translateTransform.X % parallaxSize) - parallaxSize) / _scaleTransform2.ScaleX;
            var yp = ((_translateTransform.Y % parallaxSize) - parallaxSize) / _scaleTransform2.ScaleY;

            // this is to "undo" the current zoom/pan transform on the canvas
            Canvas.SetLeft(_clickable, -_translateTransform.X / _scaleTransform2.ScaleX + xp);
            Canvas.SetTop(_clickable, -_translateTransform.Y / _scaleTransform2.ScaleY + yp);
            _clickable.Width = ActualWidth / _scaleTransform2.ScaleX + Math.Abs(xp);
            _clickable.Height = ActualHeight / _scaleTransform2.ScaleY + Math.Abs(yp);
        }

        public void UpdateScaleTransform()
        {
            double adjustment = 1 / DpiZoom; // undo the current dpi zoom so screenshots appear sharp

            _scaleTransform2.ScaleX = ContentScale * adjustment;
            _scaleTransform2.ScaleY = ContentScale * adjustment;

            // ui controls (resize handles) scale with canvas zoom + dpi
            GraphicsList.Dpi = CanvasUiElementScale;
        }

        private ScaleTransform _scaleTransform2;
        private TranslateTransform _translateTransform;

        private void InitializeZoom()
        {
            TransformGroup group = new TransformGroup();
            _scaleTransform2 = new ScaleTransform();
            group.Children.Add(_scaleTransform2);
            _translateTransform = new TranslateTransform();
            group.Children.Add(_translateTransform);
            RenderTransform = group;
            RenderTransformOrigin = new Point(0.0, 0.0);
        }

        private Point panStart;

        private void StartPanning(MouseEventArgs e)
        {
            IsPanning = true;
            panStart = e.GetPosition(this);
            CaptureMouse();
        }

        private void ContinuePanning(MouseEventArgs e)
        {
            ContentOffset += (e.GetPosition(this) - panStart) * ContentScale;
            panStart = e.GetPosition(this);
        }

        private void StopPanning(MouseEventArgs e)
        {
            IsPanning = false;
            ReleaseMouseCapture();
        }

        public void ZoomPanFit()
        {
            var rect = GraphicsList.ContentBounds;
            var dpiZoom = DpiZoom;
            ContentScale = Math.Min(ActualWidth / rect.Width * dpiZoom, ActualHeight / rect.Height * dpiZoom);
            ZoomPanCenter();
        }

        public void ZoomPanActualSize(double zoom = 1d)
        {
            ContentScale = zoom;
            ZoomPanCenter();
        }

        public void ZoomPanCenter()
        {
            var rect = GraphicsList.ContentBounds;
            var scale = ContentScale / DpiZoom;
            var x = ActualWidth / 2 - rect.Width * scale / 2 - rect.Left * scale;
            var y = ActualHeight / 2 - rect.Height * scale / 2 - rect.Top * scale;
            ContentOffset = new Point(x, y);
        }

        public void ZoomPanAuto()
        {
            var artBounds = GraphicsList.ContentBounds;
            var dpiZoom = DpiZoom;
            if (ActualHeight * dpiZoom > artBounds.Height && ActualWidth * dpiZoom > artBounds.Width)
                ZoomPanActualSize();
            else
                ZoomPanFit();
            _isAutoFit = true;
        }

        private bool _isAutoFit = false;
    }
}

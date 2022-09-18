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
using System.Reflection;
using System.ComponentModel;
using System.Windows.Data;
using Clowd.Config;

namespace Clowd.Drawing
{
    [DependencyProperty<ToolType>("Tool")]
    [DependencyProperty<Color>("ArtworkBackground")]
    [DependencyProperty<double>("LineWidth", DefaultValue = 2d, DefaultBindingMode = DefaultBindingMode.TwoWay)]
    [DependencyProperty<Color>("ObjectColor", DefaultBindingMode = DefaultBindingMode.TwoWay)]
    [DependencyProperty<double>("ObjectAngle", DefaultBindingMode = DefaultBindingMode.TwoWay)]
    [DependencyProperty<bool>("ObjectColorAuto", DefaultBindingMode = DefaultBindingMode.TwoWay)]
    [DependencyProperty<Color>("HandleColor")]
    [DependencyProperty<string>("TextFontFamilyName", DefaultValue = "Tahoma", DefaultBindingMode = DefaultBindingMode.TwoWay)]
    [DependencyProperty<FontStyle>("TextFontStyle", DefaultBindingMode = DefaultBindingMode.TwoWay)]
    [DependencyProperty<FontWeight>("TextFontWeight", DefaultBindingMode = DefaultBindingMode.TwoWay)]
    [DependencyProperty<FontStretch>("TextFontStretch", DefaultBindingMode = DefaultBindingMode.TwoWay)]
    [DependencyProperty<double>("TextFontSize", DefaultValue = 12d, DefaultBindingMode = DefaultBindingMode.TwoWay)]
    [DependencyProperty<bool>("IsPanning")]
    [DependencyProperty<Point>("ContentOffset")]
    [DependencyProperty<double>("ContentScale", DefaultValue = 1d)]
    [DependencyProperty<GraphicCollection>("GraphicsList")]
    [DependencyProperty<Skill>("SubjectSkill")]
    [DependencyProperty<string>("SubjectType")]
    [DependencyProperty<string>("SubjectName")]
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

        public int SelectedCount => GraphicsList.SelectedItems.Length;

        public int Count => GraphicsList.Count;

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
        public RelayCommand CommandCropImage { get; }

        internal ToolPointer ToolPointer;
        internal ToolText ToolText;
        private ToolDesc CurrentTool;

        private Dictionary<ToolType, ToolDesc> _toolStore;
        private Border _clickable;
        private UndoManager _undoManager;
        private bool _isToolMouseDown;
        private bool _isAutoFit;

        private record struct ToolDesc(string Name, ToolBase Instance, Type ObjectType = null, Skill Skills = Skill.None);

        public DrawingCanvas()
        {
            GraphicsList = new GraphicCollection(this);

            // create array of drawing tools
            ToolPointer = new ToolPointer();
            ToolText = new ToolText();

            var toolRectangle = new ToolDraggable<GraphicRectangle>(
                Resource.CursorRectangle,
                point => new GraphicRectangle(ObjectColor, LineWidth, new Rect(point, new Size(1, 1))),
                (point, g) => g.MoveHandleTo(point, 5),
                snapMode: SnapMode.Diagonal);

            var toolFilledRectangle = new ToolDraggable<GraphicFilledRectangle>(
                Resource.CursorRectangle,
                point => new GraphicFilledRectangle(ObjectColor, new Rect(point, new Size(1, 1))),
                (point, g) => g.MoveHandleTo(point, 5),
                snapMode: SnapMode.Diagonal);

            var toolEllipse = new ToolDraggable<GraphicEllipse>(
                Resource.CursorEllipse,
                point => new GraphicEllipse(ObjectColor, LineWidth, new Rect(point, new Size(1, 1))),
                (point, g) => g.MoveHandleTo(point, 5),
                snapMode: SnapMode.Diagonal);

            var toolLine = new ToolDraggable<GraphicLine>(
                Resource.CursorLine,
                point => new GraphicLine(ObjectColor, LineWidth, point, point),
                (point, g) => g.MoveHandleTo(point, 2),
                snapMode: SnapMode.All);

            var toolArrow = new ToolDraggable<GraphicArrow>(
                Resource.CursorArrow,
                point => new GraphicArrow(ObjectColor, LineWidth, point, point),
                (point, g) => g.MoveHandleTo(point, 2),
                snapMode: SnapMode.All);

            _toolStore = new Dictionary<ToolType, ToolDesc>();
            _toolStore[ToolType.None] = new ToolDesc("Panning", new ToolPanning());
            _toolStore[ToolType.Pointer] = new ToolDesc("Pointer", ToolPointer, Skills: Skill.CanvasBackground);
            _toolStore[ToolType.Rectangle] = new ToolDesc("Rectangle", toolRectangle, ObjectType: typeof(GraphicRectangle));
            _toolStore[ToolType.FilledRectangle] = new ToolDesc("Filled Rectangle", toolFilledRectangle, ObjectType: typeof(GraphicFilledRectangle));
            _toolStore[ToolType.Ellipse] = new ToolDesc("Ellipse", toolEllipse, ObjectType: typeof(GraphicEllipse));
            _toolStore[ToolType.Line] = new ToolDesc("Line", toolLine, ObjectType: typeof(GraphicLine));
            _toolStore[ToolType.Arrow] = new ToolDesc("Arrow", toolArrow, ObjectType: typeof(GraphicArrow));
            _toolStore[ToolType.PolyLine] = new ToolDesc("Pencil", new ToolPolyLine(), ObjectType: typeof(GraphicPolyLine));
            _toolStore[ToolType.Text] = new ToolDesc("Text", ToolText, ObjectType: typeof(GraphicText), Skills: Skill.AutoColor);
            _toolStore[ToolType.Count] = new ToolDesc("Numeric Step", new ToolCount(), ObjectType: typeof(GraphicCount));
            _toolStore[ToolType.Pixelate] = new ToolDesc("Pixelate", new ToolPixelate());

            _undoManager = new UndoManager(this);
            _undoManager.StateChanged += UndoManagerStateChanged;

            double parseDoubleOrDefault(object obj, double def)
            {
                if (obj == null) return def;
                if (obj is string str)
                    if (double.TryParse(str, out var i))
                        return i;
                try { return Convert.ToDouble(obj); }
                catch { return def; }
            }

            CommandSelectAll = new RelayCommand()
            {
                Executed = (obj) => SelectAll(),
                CanExecute = (obj) => Count > 0,
                Text = "_Select all",
                Gesture = new SimpleKeyGesture(Key.A, ModifierKeys.Control),
            };
            CommandUnselectAll = new RelayCommand()
            {
                Executed = (obj) => CancelCurrentOperation(), // this resets the tool, unselects all, etc
                CanExecute = (obj) => SelectedCount > 0,
                Text = "Unselect all",
                Gesture = new SimpleKeyGesture(Key.Escape),
            };
            CommandDelete = new RelayCommand()
            {
                Executed = (obj) => Delete(),
                CanExecute = (obj) => SelectedCount > 0,
                Text = "_Delete",
                Gesture = new SimpleKeyGesture(Key.Delete),
            };
            CommandDeleteAll = new RelayCommand()
            {
                Executed = (obj) => DeleteAll(),
                CanExecute = (obj) => Count > 0,
                Text = "Delete all",
            };
            CommandMoveToFront = new RelayCommand()
            {
                Executed = (obj) => MoveToFront(),
                CanExecute = (obj) => SelectedCount > 0,
                Text = "Move to front",
                Gesture = new SimpleKeyGesture(Key.Home),
            };
            CommandMoveToBack = new RelayCommand()
            {
                Executed = (obj) => MoveToBack(),
                CanExecute = (obj) => SelectedCount > 0,
                Text = "Move to back",
                Gesture = new SimpleKeyGesture(Key.End),
            };
            CommandMoveForward = new RelayCommand()
            {
                Executed = (obj) => MoveForward(),
                CanExecute = (obj) => SelectedCount > 0,
                Text = "Move forward",
                Gesture = new SimpleKeyGesture(Key.Home, ModifierKeys.Control),
            };
            CommandMoveBackward = new RelayCommand()
            {
                Executed = (obj) => MoveBackward(),
                CanExecute = (obj) => SelectedCount > 0,
                Text = "Move backward",
                Gesture = new SimpleKeyGesture(Key.End, ModifierKeys.Control),
            };
            CommandResetRotation = new RelayCommand()
            {
                Executed = (obj) => ResetRotation(),
                CanExecute = (obj) => SelectedCount > 0,
                Text = "Reset rotation",
            };
            CommandZoomPanAuto = new RelayCommand()
            {
                Executed = (obj) => ZoomPanAuto(),
                CanExecute = (obj) => Count > 0,
                Text = "Zoom to fit content",
                GestureText = "Ctrl+0",
            };
            CommandZoomPanActualSize = new RelayCommand()
            {
                Executed = (obj) => ZoomPanActualSize(parseDoubleOrDefault(obj, 1)),
                CanExecute = (obj) => Count > 0,
                Text = "Zoom to actual size",
                GestureText = "Ctrl+1"
            };
            CommandUndo = new RelayCommand()
            {
                Executed = (obj) => _undoManager.Undo(),
                CanExecute = (obj) => _undoManager.CanUndo,
                Text = "_Undo",
                Gesture = new SimpleKeyGesture(Key.Z, ModifierKeys.Control),
            };
            CommandRedo = new RelayCommand()
            {
                Executed = (obj) => _undoManager.Redo(),
                CanExecute = (obj) => _undoManager.CanRedo,
                Text = "_Redo",
                Gesture = new SimpleKeyGesture(Key.Y, ModifierKeys.Control),
            };
            CommandCropImage = new RelayCommand()
            {
                Executed = (obj) => CropSelectedImage(),
                CanExecute = (obj) => SelectedCount == 1 && GraphicsList.SelectedItems[0] is GraphicImage,
                Text = "Crop",
            };

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
            this.SourceUpdated += DrawingCanvas_SourceUpdated;

            InitializeZoom();

            _clickable = new Border();
            _clickable.Background = (Brush)FindResource("CheckeredLargeLightWhiteBackgroundBrush");
            Children.Add(_clickable);

            SnapsToDevicePixels = false;
            UseLayoutRounding = false;

            Tool = ToolType.Pointer;
        }

        private void CropSelectedImage()
        {
            if (SelectedCount != 1) return;
            var obj = GraphicsList.SelectedItems[0];
            if (obj is not GraphicImage img) return;
            img.Activate(this);
        }

        partial void OnToolChanged(ToolType newValue)
        {
            if (_isToolMouseDown)
            {
                // if there is an operation in progress while the tool changes, try to abort it
                CurrentTool.Instance.AbortOperation(this);
                _isToolMouseDown = false;
            }

            CurrentTool = _toolStore[newValue];
            CurrentTool.Instance.SetCursor(this);

            SyncObjectState();
        }

        partial void OnGraphicsListChanged(GraphicCollection oldValue, GraphicCollection newValue)
        {
            if (oldValue != null)
            {
                RemoveVisualChild(oldValue.BackgroundVisual);
                oldValue.PropertyChanged -= GraphicsListPropertyChanged;
                oldValue.Clear();
            }

            newValue.PropertyChanged += GraphicsListPropertyChanged;
            AddVisualChild(newValue.BackgroundVisual);
        }

        partial void OnArtworkBackgroundChanged(Color newValue)
        {
            GraphicsList.BackgroundBrush = new SolidColorBrush(newValue);
        }

        partial void OnHandleColorChanged(Color newValue)
        {
            GraphicBase.HandleBrush = new SolidColorBrush(newValue);
        }

        private void ApplyGraphicPropertyChange<TType, T>(T newValue, Func<TType, T> getTextProp, Action<TType, T> setTextProp) where TType : GraphicBase
        {
            bool wasChange = false;

            foreach (GraphicBase g in GraphicsList.SelectedItems)
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
        }

        public void SelectAll()
        {
            for (int i = 0; i < Count; i++)
            {
                this[i].IsSelected = true;
            }
        }

        public void UnselectAll()
        {
            for (int i = 0; i < this.Count; i++)
            {
                this[i].IsSelected = false;
            }
        }

        public void UnselectAllExcept(params GraphicBase[] excluded)
        {
            foreach (var ob in GraphicsList.SelectedItems.Except(excluded.Where(ex => ex != null)))
            {
                ob.IsSelected = false;
            }
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
        }

        public void DeleteAll()
        {
            if (GraphicsList.Count > 0)
            {
                GraphicsList.Clear();
                AddCommandToHistory();
            }
        }

        public void Nudge(int offsetX, int offsetY)
        {
            if (SelectedCount > 0 && (offsetX != 0 || offsetY != 0))
            {
                foreach (var obj in GraphicsList.SelectedItems)
                {
                    obj.Move(offsetX, offsetY);
                }
                _undoManager.AddCommandStepNudge();
            }
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
        }

        public void ResetRotation()
        {
            ApplyGraphicPropertyChange<GraphicRectangle, double>(0, t => t.Angle, (t, v) => t.Angle = v);
        }

        public void Undo()
        {
            _undoManager.Undo();
        }

        public void Redo()
        {
            _undoManager.Redo();
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

        private void GraphicsListPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(GraphicCollection.SelectedItems))
            {
                SyncObjectState();
                CommandManager.InvalidateRequerySuggested();
            }
            else if (e.PropertyName == nameof(GraphicCollection.Count))
            {
                CommandManager.InvalidateRequerySuggested();
            }
            else if (e.PropertyName == nameof(GraphicCollection.ContentBounds))
            {
                _isAutoFit = false;
            }
        }

        private void SyncObjectState()
        {
            // this is only triggered when the current tool or the current selection changes.
            // we connect the current "object config" properties on this class to the relevant object.

            BindingOperations.ClearBinding(this, LineWidthProperty);
            BindingOperations.ClearBinding(this, ObjectColorAutoProperty);
            BindingOperations.ClearBinding(this, ObjectColorProperty);
            BindingOperations.ClearBinding(this, ObjectAngleProperty);
            BindingOperations.ClearBinding(this, TextFontFamilyNameProperty);
            BindingOperations.ClearBinding(this, TextFontWeightProperty);
            BindingOperations.ClearBinding(this, TextFontStretchProperty);
            BindingOperations.ClearBinding(this, TextFontSizeProperty);
            BindingOperations.ClearBinding(this, TextFontStyleProperty);

            if (IsPanning)
            {
                SubjectType = "Mode";
                SubjectName = "Panning";
                SubjectSkill = Skill.None;
                return;
            }

            var selected = GraphicsList.SelectedItems;

            // if we are not using the pointer, or if there are no objects selected, use tool skills
            if (selected.Length == 0 || Tool != ToolType.Pointer)
            {
                Skill skills = CurrentTool.Skills;
                if (CurrentTool.ObjectType != null)
                {
                    var attr = CurrentTool.ObjectType.GetCustomAttribute<GraphicDescAttribute>();
                    if (attr != null)
                    {
                        skills |= attr.Skills;
                    }
                }

                // we do not allow the angle to be set in the tool.
                skills &= ~Skill.Angle;

                var settings = SettingsRoot.Current.Editor.Tools[Tool];
                void AddSettingBinding(Skill sk, DependencyProperty prop, string path)
                {
                    if (skills.HasFlag(sk))
                    {
                        this.SetBinding(prop, new Binding(path) { Source = settings });
                    }
                }

                AddSettingBinding(Skill.AutoColor, ObjectColorAutoProperty, nameof(SavedToolSettings.AutoColor));
                AddSettingBinding(Skill.Color, ObjectColorProperty, nameof(SavedToolSettings.ObjectColor));
                AddSettingBinding(Skill.Stroke, LineWidthProperty, nameof(SavedToolSettings.LineWidth));
                AddSettingBinding(Skill.Font, TextFontFamilyNameProperty, nameof(SavedToolSettings.FontFamily));
                AddSettingBinding(Skill.Font, TextFontWeightProperty, nameof(SavedToolSettings.FontWeight));
                AddSettingBinding(Skill.Font, TextFontStretchProperty, nameof(SavedToolSettings.FontStretch));
                AddSettingBinding(Skill.Font, TextFontSizeProperty, nameof(SavedToolSettings.FontSize));
                AddSettingBinding(Skill.Font, TextFontStyleProperty, nameof(SavedToolSettings.FontStyle));

                SubjectType = "Tool";
                SubjectName = CurrentTool.Name;
                SubjectSkill = skills;
            }
            // if there is precisely 1 object selected, use the object skills
            else if (selected.Length == 1 && Tool == ToolType.Pointer)
            {
                var obj = selected[0];
                var attr = obj.GetType().GetCustomAttribute<GraphicDescAttribute>();
                var skills = attr?.Skills ?? Skill.None;

                void AddObjectBinding<T>(Skill sk, DependencyProperty prop, Func<T, string> getPath)
                {
                    if (skills.HasFlag(sk) && obj is T x)
                    {
                        this.SetBinding(prop, new Binding(getPath(x)) { Source = obj, NotifyOnSourceUpdated = true });
                    }
                }

                AddObjectBinding<GraphicBase>(Skill.Color, ObjectColorProperty, x => nameof(x.ObjectColor));
                AddObjectBinding<GraphicBase>(Skill.Stroke, LineWidthProperty, x => nameof(x.LineWidth));
                AddObjectBinding<GraphicRectangle>(Skill.Angle, ObjectAngleProperty, x => nameof(x.Angle));
                AddObjectBinding<GraphicText>(Skill.Font, TextFontFamilyNameProperty, x => nameof(x.FontName));
                AddObjectBinding<GraphicText>(Skill.Font, TextFontWeightProperty, x => nameof(x.FontWeight));
                AddObjectBinding<GraphicText>(Skill.Font, TextFontStretchProperty, x => nameof(x.FontStretch));
                AddObjectBinding<GraphicText>(Skill.Font, TextFontSizeProperty, x => nameof(x.FontSize));
                AddObjectBinding<GraphicText>(Skill.Font, TextFontStyleProperty, x => nameof(x.FontStyle));

                SubjectType = "Selection";
                SubjectName = attr?.Name ?? "Unknown";
                SubjectSkill = skills;
            }
            // if there are multiple objects selected
            else
            {
                SubjectType = "Selection";
                SubjectName = "Multiple";
                SubjectSkill = Skill.None;
            }
        }

        void UndoManagerStateChanged(object sender, EventArgs e)
        {
            CommandManager.InvalidateRequerySuggested();
        }

        private void DrawingCanvas_SourceUpdated(object sender, DataTransferEventArgs e)
        {
            // this is only triggered on bindings with NotifyOnSourceUpdated, and 
            // that is only enabled on our object property bindings in SyncObjectState.
            AddCommandToHistory();
        }

        void DrawingCanvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
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
                else
                {
                    _isToolMouseDown = true;
                    CurrentTool.Instance.OnMouseDown(this, e);
                }
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
            }
        }

        void DrawingCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.MiddleButton == MouseButtonState.Released && e.RightButton == MouseButtonState.Released)
            {
                CurrentTool.Instance.OnMouseMove(this, e);
            }
        }

        void DrawingCanvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                _isToolMouseDown = false;
                CurrentTool.Instance.OnMouseUp(this, e);
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
            this.Focusable = true; // to handle keyboard messages
            UpdateScaleTransform();
            UpdateClickableSurface();
        }

        void DrawingCanvas_LostMouseCapture(object sender, MouseEventArgs e)
        {
            if (_isToolMouseDown)
            {
                CancelCurrentOperation();
            }
        }

        void DrawingCanvas_KeyDown(object sender, KeyEventArgs e)
        {
            // Shift key causes a MouseMove, so any drag-based snapping will be updated
            if (this.IsMouseCaptured && (e.Key == Key.LeftShift || e.Key == Key.RightShift))
            {
                DrawingCanvas_MouseMove(this, new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount));
            }
        }

        void DrawingCanvas_KeyUp(object sender, KeyEventArgs e)
        {
            // Shift key causes a MouseMove, so any drag-based snapping will be updated
            if (this.IsMouseCaptured && (e.Key == Key.LeftShift || e.Key == Key.RightShift))
            {
                DrawingCanvas_MouseMove(this, new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount));
            }
        }

        public void CancelCurrentOperation()
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
            else
            {
                // Delete last graphics object which is currently drawn
                CurrentTool.Instance.AbortOperation(this);
            }

            Tool = ToolType.Pointer;
            _isToolMouseDown = false;

            this.ReleaseMouseCapture();
            this.Cursor = HelperFunctions.DefaultCursor;
            UnselectAll();
        }

        internal void AddCommandToHistory()
        {
            _undoManager.AddCommandStep();
        }

        partial void OnContentScaleChanged(double newValue)
        {
            UpdateScaleTransform();
            UpdateClickableSurface();
            _isAutoFit = false;
        }

        partial void OnContentOffsetChanged(Point newValue)
        {
            double dpiZoom = DpiZoom;
            _translateTransform.X = Math.Floor(newValue.X * dpiZoom) / dpiZoom;
            _translateTransform.Y = Math.Floor(newValue.Y * dpiZoom) / dpiZoom;
            UpdateClickableSurface();
            _isAutoFit = false;
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
    }
}

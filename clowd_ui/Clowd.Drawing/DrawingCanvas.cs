using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Clowd.Config;
using Clowd.Drawing.Graphics;
using Clowd.Drawing.Tools;
using Clowd.UI.Helpers;

namespace Clowd.Drawing
{
    public class DrawingCanvas : Canvas
    {
        // ====================================================================
        // Styled properties (decision table #2: hand-written, exact names/defaults,
        // TwoWay default binding mode where the WPF DependencyProperty declared it)
        // ====================================================================

        public static readonly StyledProperty<ToolType> ToolProperty =
            AvaloniaProperty.Register<DrawingCanvas, ToolType>(nameof(Tool));

        public static readonly StyledProperty<Color> ArtworkBackgroundProperty =
            AvaloniaProperty.Register<DrawingCanvas, Color>(nameof(ArtworkBackground));

        public static readonly StyledProperty<double> LineWidthProperty =
            AvaloniaProperty.Register<DrawingCanvas, double>(nameof(LineWidth), 2d, defaultBindingMode: BindingMode.TwoWay);

        public static readonly StyledProperty<Color> ObjectColorProperty =
            AvaloniaProperty.Register<DrawingCanvas, Color>(nameof(ObjectColor), defaultBindingMode: BindingMode.TwoWay);

        public static readonly StyledProperty<double> ObjectAngleProperty =
            AvaloniaProperty.Register<DrawingCanvas, double>(nameof(ObjectAngle), defaultBindingMode: BindingMode.TwoWay);

        public static readonly StyledProperty<bool> ObjectColorAutoProperty =
            AvaloniaProperty.Register<DrawingCanvas, bool>(nameof(ObjectColorAuto), defaultBindingMode: BindingMode.TwoWay);

        public static readonly StyledProperty<bool> ObjectCursorVisibleProperty =
            AvaloniaProperty.Register<DrawingCanvas, bool>(nameof(ObjectCursorVisible), defaultBindingMode: BindingMode.TwoWay);

        public static readonly StyledProperty<Color> HandleColorProperty =
            AvaloniaProperty.Register<DrawingCanvas, Color>(nameof(HandleColor));

        public static readonly StyledProperty<string> TextFontFamilyNameProperty =
            AvaloniaProperty.Register<DrawingCanvas, string>(nameof(TextFontFamilyName), "Tahoma", defaultBindingMode: BindingMode.TwoWay);

        public static readonly StyledProperty<FontStyle> TextFontStyleProperty =
            AvaloniaProperty.Register<DrawingCanvas, FontStyle>(nameof(TextFontStyle), FontStyle.Normal, defaultBindingMode: BindingMode.TwoWay);

        public static readonly StyledProperty<FontWeight> TextFontWeightProperty =
            AvaloniaProperty.Register<DrawingCanvas, FontWeight>(nameof(TextFontWeight), FontWeight.Normal, defaultBindingMode: BindingMode.TwoWay);

        public static readonly StyledProperty<FontStretch> TextFontStretchProperty =
            AvaloniaProperty.Register<DrawingCanvas, FontStretch>(nameof(TextFontStretch), FontStretch.Normal,
                                                                  defaultBindingMode: BindingMode.TwoWay);

        public static readonly StyledProperty<double> TextFontSizeProperty =
            AvaloniaProperty.Register<DrawingCanvas, double>(nameof(TextFontSize), 12d, defaultBindingMode: BindingMode.TwoWay);

        public static readonly StyledProperty<double> BlurRadiusProperty =
            AvaloniaProperty.Register<DrawingCanvas, double>(nameof(BlurRadius), 8d, defaultBindingMode: BindingMode.TwoWay);

        public static readonly StyledProperty<bool> IsPanningProperty =
            AvaloniaProperty.Register<DrawingCanvas, bool>(nameof(IsPanning));

        public static readonly StyledProperty<Point> ContentOffsetProperty =
            AvaloniaProperty.Register<DrawingCanvas, Point>(nameof(ContentOffset));

        public static readonly StyledProperty<double> ContentScaleProperty =
            AvaloniaProperty.Register<DrawingCanvas, double>(nameof(ContentScale), 1d);

        public static readonly StyledProperty<GraphicCollection> GraphicsListProperty =
            AvaloniaProperty.Register<DrawingCanvas, GraphicCollection>(nameof(GraphicsList));

        public static readonly StyledProperty<Skill> SubjectSkillProperty =
            AvaloniaProperty.Register<DrawingCanvas, Skill>(nameof(SubjectSkill));

        public static readonly StyledProperty<string> SubjectTypeProperty =
            AvaloniaProperty.Register<DrawingCanvas, string>(nameof(SubjectType));

        public static readonly StyledProperty<string> SubjectNameProperty =
            AvaloniaProperty.Register<DrawingCanvas, string>(nameof(SubjectName));

        public ToolType Tool
        {
            get => GetValue(ToolProperty);
            set => SetValue(ToolProperty, value);
        }

        public Color ArtworkBackground
        {
            get => GetValue(ArtworkBackgroundProperty);
            set => SetValue(ArtworkBackgroundProperty, value);
        }

        public double LineWidth
        {
            get => GetValue(LineWidthProperty);
            set => SetValue(LineWidthProperty, value);
        }

        public Color ObjectColor
        {
            get => GetValue(ObjectColorProperty);
            set => SetValue(ObjectColorProperty, value);
        }

        public double ObjectAngle
        {
            get => GetValue(ObjectAngleProperty);
            set => SetValue(ObjectAngleProperty, value);
        }

        public bool ObjectColorAuto
        {
            get => GetValue(ObjectColorAutoProperty);
            set => SetValue(ObjectColorAutoProperty, value);
        }

        public bool ObjectCursorVisible
        {
            get => GetValue(ObjectCursorVisibleProperty);
            set => SetValue(ObjectCursorVisibleProperty, value);
        }

        public Color HandleColor
        {
            get => GetValue(HandleColorProperty);
            set => SetValue(HandleColorProperty, value);
        }

        public string TextFontFamilyName
        {
            get => GetValue(TextFontFamilyNameProperty);
            set => SetValue(TextFontFamilyNameProperty, value);
        }

        public FontStyle TextFontStyle
        {
            get => GetValue(TextFontStyleProperty);
            set => SetValue(TextFontStyleProperty, value);
        }

        public FontWeight TextFontWeight
        {
            get => GetValue(TextFontWeightProperty);
            set => SetValue(TextFontWeightProperty, value);
        }

        public FontStretch TextFontStretch
        {
            get => GetValue(TextFontStretchProperty);
            set => SetValue(TextFontStretchProperty, value);
        }

        public double TextFontSize
        {
            get => GetValue(TextFontSizeProperty);
            set => SetValue(TextFontSizeProperty, value);
        }

        public double BlurRadius
        {
            get => GetValue(BlurRadiusProperty);
            set => SetValue(BlurRadiusProperty, value);
        }

        public bool IsPanning
        {
            get => GetValue(IsPanningProperty);
            set => SetValue(IsPanningProperty, value);
        }

        public Point ContentOffset
        {
            get => GetValue(ContentOffsetProperty);
            set => SetValue(ContentOffsetProperty, value);
        }

        public double ContentScale
        {
            get => GetValue(ContentScaleProperty);
            set => SetValue(ContentScaleProperty, value);
        }

        public GraphicCollection GraphicsList
        {
            get => GetValue(GraphicsListProperty);
            set => SetValue(GraphicsListProperty, value);
        }

        public Skill SubjectSkill
        {
            get => GetValue(SubjectSkillProperty);
            private set => SetValue(SubjectSkillProperty, value);
        }

        public string SubjectType
        {
            get => GetValue(SubjectTypeProperty);
            private set => SetValue(SubjectTypeProperty, value);
        }

        public string SubjectName
        {
            get => GetValue(SubjectNameProperty);
            private set => SetValue(SubjectNameProperty, value);
        }

        // ====================================================================
        // Public surface
        // ====================================================================

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

        public event EventHandler<StateChangedEventArgs> StateUpdated;

        /// <summary>True while a tool drag operation is in progress (EditorWindow guards on this).</summary>
        public bool IsToolDragActive => _isToolMouseDown;

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

        internal ToolPointer ToolPointer { get; }
        internal ToolText ToolText { get; }

        private ToolDesc CurrentTool;

        private readonly Dictionary<ToolType, ToolDesc> _toolStore;
        private readonly CheckeredBackground _clickable;
        private readonly ArtworkBackgroundVisual _artworkBackground;
        private readonly UndoManager _undoManager;
        private readonly RelayCommand[] _allCommands;
        private bool _isToolMouseDown;
        private bool _isAutoFit;

        // pointer / capture state (decision table #5/#6/#8)
        private IPointer _capturedPointer;
        private PointerState? _lastPointerState;
        private double _wheelDeltaAccumulator;

        // SyncObjectState bindings (decision table #11/#12)
        private readonly List<IDisposable> _skillBindings = new List<IDisposable>();
        private readonly HashSet<string> _boundGraphicProps = new HashSet<string>();
        private GraphicBase _boundGraphic;
        private bool _syncingState;

        // graphic visuals currently attached to VisualChildren (indices 2..2+N)
        private readonly List<GraphicVisual> _attachedGraphicVisuals = new List<GraphicVisual>();

        private ScaleTransform _scaleTransform2;
        private TranslateTransform _translateTransform;

        private record struct ToolDesc(string Name, ToolBase Instance, Type ObjectType = null, Skill Skills = Skill.None);

        public DrawingCanvas()
        {
            Focusable = true; // to handle keyboard messages
            UseLayoutRounding = false;

            InitializeZoom();

            // visual tree order (bottom→top, §2.2): _clickable (Children[0] → VisualChildren[0]),
            // _artworkBackground (VisualChildren[1]), graphic visuals (VisualChildren[2 + i]),
            // then any remaining Children (e.g. ToolText's TextBox overlay) appended at the end.
            _clickable = new CheckeredBackground();
            Children.Add(_clickable);

            _artworkBackground = new ArtworkBackgroundVisual(this);
            VisualChildren.Add(_artworkBackground);

            GraphicsList = new GraphicCollection(this);

            // create array of drawing tools
            ToolPointer = new ToolPointer();
            ToolText = new ToolText();

            var toolRectangle = new ToolDraggable<GraphicRectangle>(
                () => CursorResources.Rect,
                point => new GraphicRectangle(ObjectColor, LineWidth, new Rect(point, new Size(1, 1))),
                (point, g) => g.MoveHandleTo(point, 5),
                snapMode: SnapMode.Diagonal);

            var toolFilledRectangle = new ToolDraggable<GraphicFilledRectangle>(
                () => CursorResources.Rect,
                point => new GraphicFilledRectangle(ObjectColor, new Rect(point, new Size(1, 1))),
                (point, g) => g.MoveHandleTo(point, 5),
                snapMode: SnapMode.Diagonal);

            var toolEllipse = new ToolDraggable<GraphicEllipse>(
                () => CursorResources.Ellipse,
                point => new GraphicEllipse(ObjectColor, LineWidth, new Rect(point, new Size(1, 1))),
                (point, g) => g.MoveHandleTo(point, 5),
                snapMode: SnapMode.Diagonal);

            var toolLine = new ToolDraggable<GraphicLine>(
                () => CursorResources.Line,
                point => new GraphicLine(ObjectColor, LineWidth, point, point),
                (point, g) => g.MoveHandleTo(point, 2),
                snapMode: SnapMode.All);

            var toolArrow = new ToolDraggable<GraphicArrow>(
                () => CursorResources.Arrow,
                point => new GraphicArrow(ObjectColor, LineWidth, point, point),
                (point, g) => g.MoveHandleTo(point, 2),
                snapMode: SnapMode.All);

            _toolStore = new Dictionary<ToolType, ToolDesc>();
            _toolStore[ToolType.None] = new ToolDesc("Panning", new ToolPanning());
            _toolStore[ToolType.Pointer] = new ToolDesc("Pointer", ToolPointer, Skills: Skill.CanvasBackground);
            _toolStore[ToolType.Rectangle] = new ToolDesc("Rectangle", toolRectangle, ObjectType: typeof(GraphicRectangle));
            _toolStore[ToolType.FilledRectangle] = new ToolDesc("Filled Rectangle", toolFilledRectangle,
                                                                ObjectType: typeof(GraphicFilledRectangle));
            _toolStore[ToolType.Ellipse] = new ToolDesc("Ellipse", toolEllipse, ObjectType: typeof(GraphicEllipse));
            _toolStore[ToolType.Line] = new ToolDesc("Line", toolLine, ObjectType: typeof(GraphicLine));
            _toolStore[ToolType.Arrow] = new ToolDesc("Arrow", toolArrow, ObjectType: typeof(GraphicArrow));
            _toolStore[ToolType.PolyLine] = new ToolDesc("Pencil", new ToolPolyLine(), ObjectType: typeof(GraphicPolyLine));
            _toolStore[ToolType.Text] = new ToolDesc("Text", ToolText, ObjectType: typeof(GraphicText), Skills: Skill.AutoColor);
            _toolStore[ToolType.Count] = new ToolDesc("Numeric Step", new ToolCount(), ObjectType: typeof(GraphicCount));
            _toolStore[ToolType.Pixelate] = new ToolDesc("Pixelate", new ToolPixelate(), Skills: Skill.BlurRadius);

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
                Gesture = new SimpleKeyGesture(Key.A, KeyModifiers.Control),
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
                Gesture = new SimpleKeyGesture(Key.Home, KeyModifiers.Control),
            };
            CommandMoveBackward = new RelayCommand()
            {
                Executed = (obj) => MoveBackward(),
                CanExecute = (obj) => SelectedCount > 0,
                Text = "Move backward",
                Gesture = new SimpleKeyGesture(Key.End, KeyModifiers.Control),
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
                GestureText = "Ctrl+1",
            };
            CommandUndo = new RelayCommand()
            {
                Executed = (obj) => Undo(),
                CanExecute = (obj) => _undoManager.CanUndo,
                Text = "_Undo",
                Gesture = new SimpleKeyGesture(Key.Z, KeyModifiers.Control),
            };
            CommandRedo = new RelayCommand()
            {
                Executed = (obj) => Redo(),
                CanExecute = (obj) => _undoManager.CanRedo,
                Text = "_Redo",
                Gesture = new SimpleKeyGesture(Key.Y, KeyModifiers.Control),
            };
            CommandCropImage = new RelayCommand()
            {
                Executed = (obj) => CropSelectedImage(),
                CanExecute = (obj) => SelectedCount == 1 && GraphicsList.SelectedItems[0] is GraphicImage,
                Text = "Crop",
            };

            _allCommands = new[]
            {
                CommandSelectAll, CommandUnselectAll, CommandDelete, CommandDeleteAll,
                CommandMoveToFront, CommandMoveToBack, CommandMoveForward, CommandMoveBackward,
                CommandResetRotation, CommandUndo, CommandRedo, CommandZoomPanAuto,
                CommandZoomPanActualSize, CommandCropImage,
            };

            // decision table #17: ContextMenu opens at the pointer on right-click release.
            var contextMenu = new ContextMenu { Placement = PlacementMode.Pointer };
            contextMenu.Items.Add(CommandSelectAll.CreateMenuItem());
            contextMenu.Items.Add(CommandUnselectAll.CreateMenuItem());
            contextMenu.Items.Add(CommandDelete.CreateMenuItem());
            contextMenu.Items.Add(CommandDeleteAll.CreateMenuItem());
            contextMenu.Items.Add(new Separator());
            contextMenu.Items.Add(CommandMoveToFront.CreateMenuItem());
            contextMenu.Items.Add(CommandMoveForward.CreateMenuItem());
            contextMenu.Items.Add(CommandMoveToBack.CreateMenuItem());
            contextMenu.Items.Add(CommandMoveBackward.CreateMenuItem());
            contextMenu.Items.Add(new Separator());
            contextMenu.Items.Add(CommandResetRotation.CreateMenuItem());
            contextMenu.Items.Add(new Separator());
            contextMenu.Items.Add(CommandZoomPanAuto.CreateMenuItem());
            contextMenu.Items.Add(CommandZoomPanActualSize.CreateMenuItem());
            ContextMenu = contextMenu;

            Tool = ToolType.Pointer;
        }

        private void CropSelectedImage()
        {
            if (SelectedCount != 1) return;
            var obj = GraphicsList.SelectedItems[0];
            if (obj is not GraphicImage img) return;
            img.Activate(this);
        }

        // ====================================================================
        // Styled property change dispatch (decision table #2)
        // ====================================================================

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == ToolProperty)
                OnToolChanged(change.GetNewValue<ToolType>());
            else if (change.Property == GraphicsListProperty)
                OnGraphicsListChanged(change.GetOldValue<GraphicCollection>(), change.GetNewValue<GraphicCollection>());
            else if (change.Property == HandleColorProperty)
                OnHandleColorChanged(change.GetNewValue<Color>());
            else if (change.Property == ArtworkBackgroundProperty)
                OnArtworkBackgroundChanged();
            else if (change.Property == ContentScaleProperty)
                OnContentScaleChanged();
            else if (change.Property == ContentOffsetProperty)
                OnContentOffsetChanged(change.GetNewValue<Point>());
        }

        private void OnToolChanged(ToolType newValue)
        {
            if (_toolStore == null)
                return; // property set during construction, before the tool store exists

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

        private void OnGraphicsListChanged(GraphicCollection oldValue, GraphicCollection newValue)
        {
            if (oldValue != null)
            {
                oldValue.PropertyChanged -= GraphicsListPropertyChanged;
                oldValue.Clear();
            }

            newValue.PropertyChanged += GraphicsListPropertyChanged;
            SyncGraphicVisuals();
            InvalidateBackground();
        }

        private void OnHandleColorChanged(Color newValue)
        {
            GraphicBase.HandleBrush = new SolidColorBrush(newValue);
        }

        private void OnArtworkBackgroundChanged()
        {
            InvalidateBackground();
        }

        private void OnContentScaleChanged()
        {
            if (_scaleTransform2 == null)
                return;
            UpdateScaleTransform();
            UpdateClickableSurface();
            _isAutoFit = false;
        }

        private void OnContentOffsetChanged(Point newValue)
        {
            if (_translateTransform == null)
                return;
            double dpiZoom = DpiZoom;
            // translate floored to whole device pixels so screenshots stay sharp
            _translateTransform.X = Math.Floor(newValue.X * dpiZoom) / dpiZoom;
            _translateTransform.Y = Math.Floor(newValue.Y * dpiZoom) / dpiZoom;
            UpdateClickableSurface();
            _isAutoFit = false;
        }

        // ====================================================================
        // Graphic operations
        // ====================================================================

        private void ApplyGraphicPropertyChange<TType, T>(T newValue, Func<TType, T> getTextProp, Action<TType, T> setTextProp)
            where TType : GraphicBase
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
                AddCommandToHistory(true);
            }
        }

        public Bitmap DrawGraphicsToBitmap() => GraphicsList.DrawGraphicsToBitmap(new ImmutableSolidColorBrush(ArtworkBackground));

        public void AddGraphic(GraphicBase g)
        {
            // center the object in the current viewport
            var itemBounds = g.Bounds;
            var transformX = (-itemBounds.Left - itemBounds.Width / 2) + ((Bounds.Width / 2 - ContentOffset.X) / ContentScale);
            var transformY = (-itemBounds.Top - itemBounds.Height / 2) + ((Bounds.Height / 2 - ContentOffset.Y) / ContentScale);
            g.Move(transformX, transformY);

            // only the newly added item should be selected
            this.UnselectAll();
            g.IsSelected = true;
            g.Normalize();
            this.GraphicsList.Add(g);
            AddCommandToHistory(false);
        }

        public void AddGraphics(GraphicBase[] graphics)
        {
            if (graphics.Length is 0 or 1)
            {
                if (graphics.Length == 1)
                {
                    AddGraphic(graphics[0]);
                }
                return;
            }

            // center the collection of items in the current viewport
            Rect bounds = graphics[0].Bounds;
            for (int i = 1; i < graphics.Length; i++)
                bounds = bounds.Union(graphics[i].Bounds);

            var transformX = (-bounds.Left - bounds.Width / 2) + ((Bounds.Width / 2 - ContentOffset.X) / ContentScale);
            var transformY = (-bounds.Top - bounds.Height / 2) + ((Bounds.Height / 2 - ContentOffset.Y) / ContentScale);

            foreach (var g in graphics)
                g.Move(transformX, transformY);

            // only the newly added items should be selected
            this.UnselectAll();
            foreach (var g in graphics)
            {
                g.IsSelected = true;
                g.Normalize();
                this.GraphicsList.Add(g);
            }
            AddCommandToHistory(false);
        }

        public void SetBackgroundColor(Color clr)
        {
            ArtworkBackground = clr;
            AddCommandToHistory(true);
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
                AddCommandToHistory(false);
            }
        }

        public void DeleteAll()
        {
            if (GraphicsList.Count > 0)
            {
                GraphicsList.Clear();
                AddCommandToHistory(false);
            }
        }

        public void RestoreState(JsonObject data)
        {
            var prev = _syncingState;
            _syncingState = true;
            try
            {
                _undoManager.ClearHistory(data);
            }
            finally
            {
                _syncingState = prev;
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
                _undoManager.AddCommandStep(true);
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
                AddCommandToHistory(false);
            }
        }

        public void ResetRotation()
        {
            ApplyGraphicPropertyChange<GraphicRectangle, double>(0, t => t.Angle, (t, v) => t.Angle = v);
        }

        public void Undo()
        {
            var prev = _syncingState;
            _syncingState = true;
            try
            {
                _undoManager.Undo();
            }
            finally
            {
                _syncingState = prev;
            }
        }

        public void Redo()
        {
            var prev = _syncingState;
            _syncingState = true;
            try
            {
                _undoManager.Redo();
            }
            finally
            {
                _syncingState = prev;
            }
        }

        // ====================================================================
        // Visual children management (decision table #1)
        // ====================================================================

        internal void InternalAddVisualChild(Visual child) => VisualChildren.Add(child);

        internal void InternalRemoveVisualChild(Visual child) => VisualChildren.Remove(child);

        /// <summary>
        /// Children added through <see cref="Panel.Children"/> (e.g. ToolText's TextBox overlay) are
        /// always appended to the END of VisualChildren so they render above the graphic visuals.
        /// The base Panel implementation inserts at the logical index, which would interleave them
        /// with our manually managed visuals (_clickable, _artworkBackground, graphic visuals).
        /// </summary>
        protected override void ChildrenChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Add:
                {
                    var controls = e.NewItems.OfType<Control>().ToList();
                    LogicalChildren.InsertRange(Math.Min(e.NewStartingIndex, LogicalChildren.Count), controls);
                    foreach (var c in controls)
                        VisualChildren.Add(c);
                    break;
                }
                case NotifyCollectionChangedAction.Remove:
                {
                    foreach (var c in e.OldItems.OfType<Control>())
                    {
                        LogicalChildren.Remove(c);
                        VisualChildren.Remove(c);
                    }
                    break;
                }
                case NotifyCollectionChangedAction.Replace:
                {
                    foreach (var c in e.OldItems.OfType<Control>())
                    {
                        LogicalChildren.Remove(c);
                        VisualChildren.Remove(c);
                    }
                    var added = e.NewItems.OfType<Control>().ToList();
                    LogicalChildren.InsertRange(Math.Min(e.NewStartingIndex, LogicalChildren.Count), added);
                    foreach (var c in added)
                        VisualChildren.Add(c);
                    break;
                }
                case NotifyCollectionChangedAction.Move:
                    // logical-only reorder; not used by Clowd
                    break;
                default:
                    throw new NotSupportedException("Reset is not supported on the Children collection.");
            }

            InvalidateMeasure();
        }

        /// <summary>
        /// Rebuilds the graphic visual segment of VisualChildren (indices 2..2+N) to match
        /// the current GraphicsList order (list order = z-order).
        /// </summary>
        private void SyncGraphicVisuals()
        {
            foreach (var v in _attachedGraphicVisuals)
                VisualChildren.Remove(v);
            _attachedGraphicVisuals.Clear();

            var list = GraphicsList;
            if (list != null)
            {
                for (int i = 0; i < list.VisualCount; i++)
                {
                    var vis = list.GetVisual(i);
                    VisualChildren.Insert(2 + i, vis);
                    _attachedGraphicVisuals.Add(vis);
                }
            }

            InvalidateArrange();
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var result = base.ArrangeOverride(finalSize);

            // _artworkBackground and the graphic visuals are visual-only children (not in Children),
            // so the Canvas layout pass does not see them; arrange them over the full canvas. Their
            // content draws at absolute canvas coordinates and is not clipped to these bounds.
            var rect = new Rect(finalSize);
            _artworkBackground.Measure(Size.Infinity);
            _artworkBackground.Arrange(rect);
            foreach (var v in _attachedGraphicVisuals)
            {
                v.Measure(Size.Infinity);
                v.Arrange(rect);
            }

            return result;
        }

        private void InvalidateBackground()
        {
            _artworkBackground.InvalidateVisual();
        }

        /// <summary>
        /// Tiny visual filling GraphicsList.ContentBounds with the ArtworkBackground color
        /// (replaces the WPF DrawingVisual at visual index 1).
        /// </summary>
        private sealed class ArtworkBackgroundVisual : Control
        {
            private readonly DrawingCanvas _canvas;

            public ArtworkBackgroundVisual(DrawingCanvas canvas)
            {
                _canvas = canvas;

                // visual-only child (no logical parent): it cannot inherit the canvas Cursor, so it
                // must not win pointer hit-tests or the cursor resets to the system default within
                // the artwork bounds. Mouse input is handled by the _clickable surface instead.
                IsHitTestVisible = false;
            }

            public override void Render(DrawingContext context)
            {
                var list = _canvas.GraphicsList;
                if (list == null)
                    return;
                context.FillRectangle(new ImmutableSolidColorBrush(_canvas.ArtworkBackground), list.ContentBounds);
            }
        }

        // ====================================================================
        // State synchronization
        // ====================================================================

        private void GraphicsListPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(GraphicCollection.SelectedItems))
            {
                SyncObjectState();
                RequeryCommands();
            }
            else if (e.PropertyName == nameof(GraphicCollection.Count))
            {
                SyncGraphicVisuals();
                RequeryCommands();
            }
            else if (e.PropertyName == nameof(GraphicCollection.ContentBounds))
            {
                _isAutoFit = false;
                InvalidateBackground();
            }
        }

        /// <summary>
        /// Replaces WPF CommandManager.InvalidateRequerySuggested (decision table #10); called at the
        /// 3 WPF invalidation sites (GraphicsList PropertyChanged ×2, UndoManager StateChanged).
        /// </summary>
        internal void RequeryCommands()
        {
            if (_allCommands == null)
                return;
            foreach (var command in _allCommands)
                command.RaiseCanExecuteChanged();
        }

        private void SyncObjectState()
        {
            // this is only triggered when the current tool or the current selection changes.
            // we connect the current "object config" properties on this class to the relevant object.
            // decision table #11/#12: WPF SetBinding/ClearBinding becomes this.Bind + disposables, and
            // NotifyOnSourceUpdated becomes a PropertyChanged whitelist on the bound graphic, with the
            // _syncingState re-entrancy guard active for the duration of this method.
            var prev = _syncingState;
            _syncingState = true;
            try
            {
                foreach (var binding in _skillBindings)
                    binding.Dispose();
                _skillBindings.Clear();
                DetachBoundGraphic();

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

                    var settings = SettingsRoot.Current.Editor.GetToolSettings(Tool);
                    void AddSettingBinding(Skill sk, AvaloniaProperty prop, string path)
                    {
                        if (skills.HasFlag(sk))
                        {
                            _skillBindings.Add(this.Bind(prop, new Binding(path) { Source = settings }));
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
                    AddSettingBinding(Skill.BlurRadius, BlurRadiusProperty, nameof(SavedToolSettings.BlurRadius));

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

                    // this is less than ideal. need to hide Cursor button if it was not captured.
                    if (obj is GraphicImage img && !img.HasCursor) skills &= ~Skill.Cursor;

                    void AddObjectBinding<T>(Skill sk, AvaloniaProperty prop, Func<T, string> getPath) where T : GraphicBase
                    {
                        if (skills.HasFlag(sk) && obj is T x)
                        {
                            var path = getPath(x);
                            _skillBindings.Add(this.Bind(prop, new Binding(path) { Source = obj }));
                            _boundGraphicProps.Add(path);
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
                    AddObjectBinding<GraphicImage>(Skill.Cursor, ObjectCursorVisibleProperty, x => nameof(x.CursorVisible));

                    if (_boundGraphicProps.Count > 0)
                    {
                        _boundGraphic = obj;
                        _boundGraphic.PropertyChanged += BoundGraphicPropertyChanged;
                    }

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
            finally
            {
                _syncingState = prev;
            }
        }

        private void DetachBoundGraphic()
        {
            if (_boundGraphic != null)
                _boundGraphic.PropertyChanged -= BoundGraphicPropertyChanged;
            _boundGraphic = null;
            _boundGraphicProps.Clear();
        }

        private void BoundGraphicPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // replaces WPF NotifyOnSourceUpdated/SourceUpdated (decision table #12): a bound object
            // property was written through (or alongside) the binding, so add a mergable undo step.
            if (_syncingState)
                return;
            if (e.PropertyName != null && _boundGraphicProps.Contains(e.PropertyName))
                AddCommandToHistory(true);
        }

        private void UndoManagerStateChanged(object sender, StateChangedEventArgs e)
        {
            RequeryCommands();
            StateUpdated?.Invoke(this, e);
            SyncObjectState();
        }

        // ====================================================================
        // Pointer / keyboard handling
        // ====================================================================

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);

            this.Focus();

            var s = PointerState.From(e, this);
            _lastPointerState = s;

            var kind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
            if (kind == PointerUpdateKind.LeftButtonPressed)
            {
                if (e.ClickCount == 2)
                {
                    // on double click, execute GraphicBase.Activate().
                    // this allows GraphicText to launch an editor etc.
                    Point point = s.Position;
                    var clicked = ToolPointer.MakeHitTest(this, point, out var handleNum);
                    if (clicked != null)
                        clicked.Activate(this);
                }
                else
                {
                    _isToolMouseDown = true;
                    CurrentTool.Instance.OnMouseDown(this, s, e.ClickCount);
                }
            }
            else if (kind == PointerUpdateKind.RightButtonPressed)
            {
                // fake a mouse up for left mouse button if user is in the middle of an operation
                _isToolMouseDown = false;
                CurrentTool.Instance.OnMouseUp(this, s with { LeftPressed = false });
                Tool = ToolType.Pointer;

                // Change current selection if necessary
                Point point = s.Position;
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

                // ContextMenu (Placement = Pointer) opens automatically on right-click release.
            }
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);

            var s = PointerState.From(e, this);
            _lastPointerState = s;

            if (!s.MiddlePressed && !s.RightPressed)
            {
                CurrentTool.Instance.OnMouseMove(this, s);
            }
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);

            var s = PointerState.From(e, this);
            _lastPointerState = s;

            if (e.InitialPressMouseButton == MouseButton.Left)
            {
                _isToolMouseDown = false;
                CurrentTool.Instance.OnMouseUp(this, s);
            }
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);

            _capturedPointer = null;
            if (_isToolMouseDown)
            {
                CancelCurrentOperation();
            }
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);

            if (IsPanning)
                return;

            // decision table #16: accumulate fractional deltas (trackpads); one zoom stop per ±1.0,
            // remainder kept for the next event.
            _wheelDeltaAccumulator += e.Delta.Y;
            while (Math.Abs(_wheelDeltaAccumulator) >= 1.0)
            {
                var direction = _wheelDeltaAccumulator > 0 ? 1 : -1;
                _wheelDeltaAccumulator -= direction;
                ZoomStep(direction, e.GetPosition(this));
            }
        }

        private static readonly double[] _zoomStops = { 0.1, 0.25, 0.50, 0.75, 1, 1.5, 2, 3 };

        private void ZoomStep(int direction, Point relativeMouse)
        {
            double newZoom = 0;

            if (ContentScale > 2.99)
            {
                newZoom = ContentScale + (direction > 0 ? 1 : -1);
                if (newZoom > 10) newZoom = 0; // max zoom
            }
            else if (direction > 0)
            {
                newZoom = _zoomStops.Where(z => z > ContentScale).Min();
            }
            else if (direction < 0 && ContentScale > 0.1)
            {
                newZoom = _zoomStops.Where(z => z < ContentScale).Max();
            }

            if (newZoom == 0)
                return;

            // wheel zoom is anchored at the pointer position
            double absoluteX = relativeMouse.X * ContentScale + _translateTransform.X;
            double absoluteY = relativeMouse.Y * ContentScale + _translateTransform.Y;

            ContentScale = newZoom;
            ContentOffset = new Point(absoluteX - relativeMouse.X * ContentScale, absoluteY - relativeMouse.Y * ContentScale);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            // Shift key replays a synthetic MouseMove, so any drag-based snapping will be updated
            if (IsMouseCaptured && (e.Key == Key.LeftShift || e.Key == Key.RightShift))
            {
                ReplayCachedPointerMove(addShift: true);
            }
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);

            // Shift key replays a synthetic MouseMove, so any drag-based snapping will be updated
            if (IsMouseCaptured && (e.Key == Key.LeftShift || e.Key == Key.RightShift))
            {
                ReplayCachedPointerMove(addShift: false);
            }
        }

        /// <summary>
        /// Replays the cached PointerState as a synthetic move with updated modifiers (decision
        /// table #8 — replaces WPF's synthesized MouseMove on Shift press/release mid-drag).
        /// The synthetic state carries a null Pointer so capture state is unchanged.
        /// </summary>
        private void ReplayCachedPointerMove(bool addShift)
        {
            if (_lastPointerState is not { } last)
                return;

            var modifiers = addShift ? last.Modifiers | KeyModifiers.Shift : last.Modifiers & ~KeyModifiers.Shift;
            var synthetic = new PointerState(last.Position, modifiers, last.LeftPressed, last.MiddlePressed, last.RightPressed, null);
            _lastPointerState = synthetic;

            if (!synthetic.MiddlePressed && !synthetic.RightPressed)
            {
                CurrentTool.Instance.OnMouseMove(this, synthetic);
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
                        AddCommandToHistory(false);
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

        internal void AddCommandToHistory(bool mergable)
        {
            _undoManager.AddCommandStep(mergable);
        }

        // ====================================================================
        // Mouse capture (decision table #6)
        // ====================================================================

        internal bool IsMouseCaptured => _capturedPointer != null;

        internal void CaptureMouse(IPointer pointer)
        {
            if (pointer == null)
                return; // synthetic replay — capture state is unchanged
            _capturedPointer = pointer;
            pointer.Capture(this);
        }

        internal void ReleaseMouseCapture()
        {
            var pointer = _capturedPointer;
            _capturedPointer = null;
            if (pointer != null && ReferenceEquals(pointer.Captured, this))
            {
                pointer.Capture(null);
            }
        }

        // ====================================================================
        // Zoom / pan
        // ====================================================================

        public DpiScale CanvasUiElementScale
        {
            get
            {
                var dpi = DpiZoom;
                return new DpiScale(dpi * (1 / ContentScale), dpi * (1 / ContentScale));
            }
        }

        internal double DpiZoom => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;

        protected override void OnLoaded(RoutedEventArgs e)
        {
            base.OnLoaded(e);
            UpdateScaleTransform();
            UpdateClickableSurface();
        }

        protected override void OnSizeChanged(SizeChangedEventArgs e)
        {
            base.OnSizeChanged(e);
            bool isAutoFit = _isAutoFit;
            ContentOffset = new Point(
                ContentOffset.X + e.NewSize.Width / 2 - e.PreviousSize.Width / 2,
                ContentOffset.Y + e.NewSize.Height / 2 - e.PreviousSize.Height / 2);
            if (isAutoFit)
                ZoomPanAuto();
        }

        public void UpdateClickableSurface()
        {
            if (_scaleTransform2 == null)
                return;

            // _clickable is an element that simply spans the entire visible canvas area
            // this is necessary because the "real" canvas element may actually not even be on screen
            // (for example, if the current translation is large) and if that's the case, we would not
            // receive any mouse events.

            // the parallax calculation here is to give the effect that the background is moving when the
            // canvas is being dragged (despite it actually being stationary and fixed to the viewport)
            double parallaxSize = 100 * _scaleTransform2.ScaleX;
            var xp = ((_translateTransform.X % parallaxSize) - parallaxSize) / _scaleTransform2.ScaleX;
            var yp = ((_translateTransform.Y % parallaxSize) - parallaxSize) / _scaleTransform2.ScaleY;

            // this is to "undo" the current zoom/pan transform on the canvas
            Canvas.SetLeft(_clickable, -_translateTransform.X / _scaleTransform2.ScaleX + xp);
            Canvas.SetTop(_clickable, -_translateTransform.Y / _scaleTransform2.ScaleY + yp);
            _clickable.Width = Bounds.Width / _scaleTransform2.ScaleX + Math.Abs(xp);
            _clickable.Height = Bounds.Height / _scaleTransform2.ScaleY + Math.Abs(yp);
        }

        public void UpdateScaleTransform()
        {
            if (_scaleTransform2 == null)
                return;

            double adjustment = 1 / DpiZoom; // undo the current dpi zoom so screenshots appear sharp

            _scaleTransform2.ScaleX = ContentScale * adjustment;
            _scaleTransform2.ScaleY = ContentScale * adjustment;

            // ui controls (resize handles) scale with canvas zoom + dpi
            GraphicsList.Dpi = CanvasUiElementScale;
        }

        private void InitializeZoom()
        {
            TransformGroup group = new TransformGroup();
            _scaleTransform2 = new ScaleTransform();
            group.Children.Add(_scaleTransform2);
            _translateTransform = new TranslateTransform();
            group.Children.Add(_translateTransform);
            RenderTransform = group;
            // decision table #15: Avalonia defaults to center; WPF implicitly used the top-left
            RenderTransformOrigin = new RelativePoint(0.0, 0.0, RelativeUnit.Relative);
        }

        public void ZoomPanFit()
        {
            var rect = GraphicsList.ContentBounds;
            var dpiZoom = DpiZoom;
            ContentScale = Math.Min(Bounds.Width / rect.Width * dpiZoom, Bounds.Height / rect.Height * dpiZoom);
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
            var x = Bounds.Width / 2 - rect.Width * scale / 2 - rect.Left * scale;
            var y = Bounds.Height / 2 - rect.Height * scale / 2 - rect.Top * scale;
            ContentOffset = new Point(x, y);
        }

        public void ZoomPanAuto()
        {
            var artBounds = GraphicsList.ContentBounds;
            var dpiZoom = DpiZoom;
            if (Bounds.Height * dpiZoom > artBounds.Height && Bounds.Width * dpiZoom > artBounds.Width)
                ZoomPanActualSize();
            else
                ZoomPanFit();
            _isAutoFit = true;
        }
    }
}

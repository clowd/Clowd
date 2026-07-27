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
using Clowd.Drawing.History;
using Clowd.Drawing.Rendering;
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

        public static readonly StyledProperty<double> CornerRadiusProperty =
            AvaloniaProperty.Register<DrawingCanvas, double>(nameof(CornerRadius), 0d, defaultBindingMode: BindingMode.TwoWay);

        public static readonly StyledProperty<LineDashStyle> DashStyleProperty =
            AvaloniaProperty.Register<DrawingCanvas, LineDashStyle>(nameof(DashStyle), LineDashStyle.Solid,
                                                                    defaultBindingMode: BindingMode.TwoWay);

        public static readonly StyledProperty<ObscureMode> ObscureModeProperty =
            AvaloniaProperty.Register<DrawingCanvas, ObscureMode>(nameof(ObscureMode), ObscureMode.Mosaic,
                                                                  defaultBindingMode: BindingMode.TwoWay);

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

        public double CornerRadius
        {
            get => GetValue(CornerRadiusProperty);
            set => SetValue(CornerRadiusProperty, value);
        }

        public LineDashStyle DashStyle
        {
            get => GetValue(DashStyleProperty);
            set => SetValue(DashStyleProperty, value);
        }

        /// <summary>Item source for the property bar's dash-style picker.</summary>
        public static readonly LineDashStyle[] DashStyleValues =
            (LineDashStyle[])Enum.GetValues(typeof(LineDashStyle));

        public ObscureMode ObscureMode
        {
            get => GetValue(ObscureModeProperty);
            set => SetValue(ObscureModeProperty, value);
        }

        /// <summary>Item source for the property-bar mode selector — XAML cannot enumerate an enum.</summary>
        public static ObscureMode[] ObscureModeValues { get; } = (ObscureMode[])Enum.GetValues(typeof(ObscureMode));

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

        /// <summary>
        /// True while a property-bar scrub's merge tail is armed (AutosaveThrottle debounce
        /// pending). The frame validator treats it like a tool drag: shadow bakes are capped at
        /// interactive resolution and re-baked full-res on the scrub's trailing edge (Flush).
        /// </summary>
        internal bool IsInteractiveScrubActive => _autosaveThrottle != null && _autosaveThrottle.IsScrubActive;

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
        private readonly ArtworkView _artworkView;
        private readonly UndoManager _undoManager;
        private readonly RelayCommand[] _allCommands;
        private bool _isToolMouseDown;
        private bool _isAutoFit;

        // pointer / capture state (decision table #5/#6/#8)
        private IPointer _capturedPointer;
        private PointerState? _lastPointerState;

        // The cached pointer position for synthetic replays, kept in ROOT-WINDOW space rather than
        // canvas space. A replay resolves it back through the CURRENT canvas transform, so a
        // transform change between caching and replaying (Ctrl+0..3 recenter, or a window resize
        // shifting ContentOffset) can no longer manufacture a delta out of the epoch mismatch and
        // teleport the dragged geometry — the same reason ToolPointer keeps its own drag
        // bookkeeping in root space.
        private Point? _lastPointerRoot;
        private double _wheelDeltaAccumulator;

        // guards the synthetic move replay against key auto-repeat (see OnKeyDown)
        private bool _shiftReplayed;

        // SyncObjectState bindings (decision table #11/#12)
        private readonly List<IDisposable> _skillBindings = new List<IDisposable>();
        private readonly HashSet<string> _boundGraphicProps = new HashSet<string>();
        private GraphicBase _boundGraphic;
        private bool _syncingState;
        private bool _backgroundDirtySinceCommit;

        // persistence boundary (final-design §B.6): immediate StateUpdated for discrete history
        // actions, 150ms trailing-edge debounce for merge-in-place rewrites (the scrub path)
        private readonly AutosaveThrottle _autosaveThrottle;

        // SyncObjectState inputs-changed early-out (final-design §B.6): the rebind (dispose +
        // recreate every skill binding) only runs when what it depends on actually changed —
        // history raises StateChanged once per merged scrub step, and rebinding per pointer
        // event was part of the R2 storm. SelectedItems identity is a valid input because the
        // collection keeps the same array instance when membership+order are unchanged.
        private GraphicBase[] _lastSyncSelection;
        private ToolType _lastSyncTool;
        private bool _lastSyncPanning;
        private bool _syncStateForced = true; // force the first run + after undo/redo/restore

        private ScaleTransform _scaleTransform2;
        private TranslateTransform _translateTransform;

        private record struct ToolDesc(string Name, ToolBase Instance, Type ObjectType = null, Skill Skills = Skill.None);

        public DrawingCanvas()
        {
            Focusable = true; // to handle keyboard messages
            UseLayoutRounding = false;

            InitializeZoom();

            // visual tree order (bottom→top, final-design §A.1): _clickable (Children[0] →
            // VisualChildren[0], the ONLY hit-testable surface), _artworkView (VisualChildren[1],
            // the whole document in one SceneRenderer pass), then any remaining Children (e.g.
            // ToolText's TextBox overlay) appended at the end so they render above the artwork.
            _clickable = new CheckeredBackground();
            Children.Add(_clickable);

            _artworkView = new ArtworkView(this);
            VisualChildren.Add(_artworkView);

            GraphicsList = new GraphicCollection(this);

            // create array of drawing tools
            ToolPointer = new ToolPointer();
            ToolText = new ToolText();

            // the create lambdas close over this canvas, so a new graphic starts out with the
            // property-bar values for every skill its type declares
            var toolRectangle = new ToolDraggable<GraphicRectangle>(
                () => CursorResources.Rect,
                point => new GraphicRectangle(ObjectColor, LineWidth, new Rect(point, new Size(1, 1)))
                {
                    CornerRadius = CornerRadius,
                    DashStyle = DashStyle,
                },
                (point, g) => g.MoveHandleTo(point, 5),
                snapMode: SnapMode.Diagonal);

            var toolFilledRectangle = new ToolDraggable<GraphicFilledRectangle>(
                () => CursorResources.Rect,
                point => new GraphicFilledRectangle(ObjectColor, new Rect(point, new Size(1, 1)))
                {
                    CornerRadius = CornerRadius,
                },
                (point, g) => g.MoveHandleTo(point, 5),
                snapMode: SnapMode.Diagonal);

            var toolEllipse = new ToolDraggable<GraphicEllipse>(
                () => CursorResources.Ellipse,
                point => new GraphicEllipse(ObjectColor, LineWidth, new Rect(point, new Size(1, 1)))
                {
                    DashStyle = DashStyle,
                },
                (point, g) => g.MoveHandleTo(point, 5),
                snapMode: SnapMode.Diagonal);

            var toolLine = new ToolDraggable<GraphicLine>(
                () => CursorResources.Line,
                point => new GraphicLine(ObjectColor, LineWidth, point, point)
                {
                    DashStyle = DashStyle,
                },
                (point, g) => g.MoveHandleTo(point, 2),
                snapMode: SnapMode.All);

            var toolArrow = new ToolDraggable<GraphicArrow>(
                () => CursorResources.Arrow,
                point => new GraphicArrow(ObjectColor, LineWidth, point, point)
                {
                    DashStyle = DashStyle,
                },
                (point, g) => g.MoveHandleTo(point, 2),
                snapMode: SnapMode.All);

            var toolMeasure = new ToolDraggable<GraphicMeasure>(
                () => CursorResources.Measure,
                point => new GraphicMeasure(ObjectColor, LineWidth, point, point),
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
            _toolStore[ToolType.Pixelate] = new ToolDesc("Pixelate", new ToolPixelate(), Skills: Skill.BlurRadius | Skill.ObscureMode);
            _toolStore[ToolType.Measure] = new ToolDesc("Measure", toolMeasure, ObjectType: typeof(GraphicMeasure));

            _autosaveThrottle = new AutosaveThrottle(this);
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
                // drag over: re-bake any interactively-capped shadow sprites at rest (§A.3)
                GraphicsList?.RequestValidation();
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
            _artworkView.InvalidateVisual();
        }

        private void OnHandleColorChanged(Color newValue)
        {
            GraphicBase.HandleBrush = RenderResources.GetBrush(newValue);
            _artworkView?.InvalidateVisual();
        }

        private void OnArtworkBackgroundChanged()
        {
            // history dirt for the commit path (final-design §B.2): the undo engine consumes this
            // alongside GraphicCollection.ConsumeDirty() to know a background compare is needed
            _backgroundDirtySinceCommit = true;
            _artworkView?.InvalidateVisual();
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
            // center the object in the current viewport. The viewport→canvas inverse is
            // (v - offset) / s with s = ContentScale/DpiZoom (UpdateScaleTransform) — dividing by
            // ContentScale alone lands the graphic at 1/DpiZoom of the intended spot, i.e. dragged
            // toward the origin rather than centred, on any monitor above 100%.
            var itemBounds = g.Bounds;
            var scale = ContentScale / DpiZoom;
            var transformX = (-itemBounds.Left - itemBounds.Width / 2) + ((Bounds.Width / 2 - ContentOffset.X) / scale);
            var transformY = (-itemBounds.Top - itemBounds.Height / 2) + ((Bounds.Height / 2 - ContentOffset.Y) / scale);
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

            // same viewport→canvas inverse as AddGraphic: s = ContentScale/DpiZoom, not ContentScale
            var scale = ContentScale / DpiZoom;
            var transformX = (-bounds.Left - bounds.Width / 2) + ((Bounds.Width / 2 - ContentOffset.X) / scale);
            var transformY = (-bounds.Top - bounds.Height / 2) + ((Bounds.Height / 2 - ContentOffset.Y) / scale);

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
            // batch path (final-design §A.4): the collection flips IsSelected in one O(n) sweep
            // with a single deferred SelectedItems rebuild instead of a rebuild per item
            GraphicsList.SelectAll();
        }

        public void UnselectAll()
        {
            GraphicsList.UnselectAll();
        }

        public void UnselectAllExcept(params GraphicBase[] excluded)
        {
            GraphicsList.UnselectAllExcept(excluded);
        }

        // ====================================================================
        // Layers-panel mutation seam (opt-in editor features): explicit per-graphic operations
        // the panel drives regardless of the current canvas selection.
        // ====================================================================

        /// <summary>Toggles a graphic's Hidden flag. If it becomes hidden while selected it is
        /// unselected first (a hidden graphic is not canvas-interactive).</summary>
        public void ToggleHidden(GraphicBase g)
        {
            if (g == null || !GraphicsList.Contains(g))
                return;

            if (!g.Hidden && g.IsSelected)
                g.IsSelected = false; // becoming hidden: drop it from the canvas selection first

            g.Hidden = !g.Hidden;
            AddCommandToHistory(false);
            GraphicsList.RequestValidation();
        }

        /// <summary>Toggles a graphic's Locked flag. Any existing (panel-driven) selection is left
        /// as-is — a locked graphic remains a valid panel selection.</summary>
        public void ToggleLocked(GraphicBase g)
        {
            if (g == null || !GraphicsList.Contains(g))
                return;

            g.Locked = !g.Locked;
            AddCommandToHistory(false);
            GraphicsList.RequestValidation();
        }

        /// <summary>
        /// Panel-driven selection: additive toggles the graphic's selection, non-additive makes it
        /// the sole selection. Deliberately bypasses the canvas hit-test rules so the panel can
        /// select even a locked graphic. No history commit — selection is transient; the change
        /// flows through the SelectedItems validation funnel, which raises PropertyChanged and drives
        /// the property-bar SyncObjectState.
        /// </summary>
        public void SetPanelSelection(GraphicBase g, bool additive)
        {
            if (g == null || !GraphicsList.Contains(g))
                return;

            if (additive)
            {
                g.IsSelected = !g.IsSelected;
            }
            else
            {
                UnselectAllExcept(g);
                g.IsSelected = true;
            }
        }

        /// <summary>
        /// Reorders a single graphic to <paramref name="newIndex"/> (clamped) regardless of the
        /// current selection. Uses the same RemoveAt+Insert approach as <see cref="MoveToIndex"/>.
        /// </summary>
        public void MoveGraphicToIndex(GraphicBase g, int newIndex)
        {
            if (g == null || !GraphicsList.Contains(g))
                return;

            int currentIndex = GraphicsList.IndexOf(g);
            if (newIndex < 0)
                newIndex = 0;
            if (newIndex > Count - 1)
                newIndex = Count - 1;
            if (newIndex == currentIndex)
                return;

            GraphicsList.RemoveAt(currentIndex);
            if (newIndex > GraphicsList.Count)
                GraphicsList.Add(g);
            else
                GraphicsList.Insert(newIndex, g);

            // RemoveAt→DisconnectFromParent cleared g's PropertyChanged delegates (skill bindings +
            // BoundGraphicPropertyChanged); force the StateChanged-driven SyncObjectState to rebind
            // even though the selection array instance is unchanged.
            _syncStateForced = true;
            AddCommandToHistory(false);
            GraphicsList.RequestValidation();
            // a no-op commit raises no StateChanged, so resync here to guarantee the forced rebind
            // still happens (early-outs to O(1) when the commit already ran it).
            SyncObjectState();
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
                // any armed autosave tail belongs to the document being replaced — drop it
                // (contract #23: no StateUpdated raise on restore-load)
                _autosaveThrottle.Cancel();

                // in-place load (final-design §B.4): deserialize, Normalize each, Clear+AddRange
                // into the SAME collection, seed the committed shadow, no StateChanged raise
                _undoManager.ClearHistory(data);

                // release escape hatch (final-design risk #5): the first commit after a restore
                // full-scans the document, so a load-time mutation that somehow bypassed the
                // PropertyChanged funnel still lands in history
                _undoManager.FullScanNextCommit = true;

                _syncStateForced = true; // property-bar bindings must resync to the loaded doc
            }
            finally
            {
                _syncingState = prev;
            }

            // ClearHistory raised Count (and requeried) while the OLD history node was still
            // current, then reset _node without a StateChanged raise (contract #23) — refresh
            // Undo/Redo CanExecute against the reset history.
            RequeryCommands();
        }

        public void Nudge(int offsetX, int offsetY)
        {
            if (SelectedCount > 0 && (offsetX != 0 || offsetY != 0))
            {
                foreach (var obj in GraphicsList.SelectedItems)
                {
                    obj.Move(offsetX, offsetY);
                }
                // route through AddCommandToHistory so the _syncingState re-entrancy guard is
                // enforced uniformly at the single commit choke point
                AddCommandToHistory(true);
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
                // RemoveAt→DisconnectFromParent cleared the selected graphics' PropertyChanged
                // delegates (skill bindings + BoundGraphicPropertyChanged); force the
                // StateChanged-driven SyncObjectState to rebind even though the selection array
                // instance is unchanged.
                _syncStateForced = true;
                AddCommandToHistory(false);
                // a no-op move (graphic already at the target index) commits an empty change set,
                // which raises no StateChanged — resync here so the forced rebind still happens
                // (early-outs to O(1) when the commit already ran it)
                SyncObjectState();
            }
        }

        public void ResetRotation()
        {
            ApplyGraphicPropertyChange<GraphicRectangle, double>(0, t => t.Angle, (t, v) => t.Angle = v);
        }

        public void Undo()
        {
            // the delta apply writes fields directly (bypassing property setters), so the
            // property bar must rebind even when the selection/tool inputs look unchanged
            _syncStateForced = true;

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
            _syncStateForced = true;

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
        /// always appended to the END of VisualChildren so they render above the artwork.
        /// The base Panel implementation inserts at the logical index, which would interleave them
        /// with our manually managed visuals (_clickable, _artworkView).
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

        protected override Size ArrangeOverride(Size finalSize)
        {
            var result = base.ArrangeOverride(finalSize);

            // _artworkView is a visual-only child (not in Children), so the Canvas layout pass
            // does not see it; arrange it over the full canvas. Its content draws at absolute
            // canvas coordinates and is not clipped to these bounds (the EditorWindow Border
            // ClipToBounds provides the viewport clip), same as the old graphic visuals.
            _artworkView.Measure(Size.Infinity);
            _artworkView.Arrange(new Rect(finalSize));

#if DEBUG
            // final-design §A.3: Visual.Effect is never used again anywhere in the canvas tree
            foreach (var child in VisualChildren)
                System.Diagnostics.Debug.Assert(child.Effect == null,
                                                "Visual.Effect must not be used in the DrawingCanvas tree (final-design §A.3)");
#endif

            return result;
        }

        /// <summary>
        /// One re-record of the whole artwork (final-design §A.4). Called by the collection's
        /// frame validator (once per frame, no matter how many changes arrived) and by the
        /// Dpi/HandleColor/ArtworkBackground retargets.
        /// </summary>
        internal void InvalidateArtwork() => _artworkView?.InvalidateVisual();

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
                // structural changes need no visual-tree work anymore — the collection's frame
                // validator re-records the artwork view; only command state depends on Count here
                RequeryCommands();
            }
            else if (e.PropertyName == nameof(GraphicCollection.ContentBounds))
            {
                _isAutoFit = false;
                _artworkView?.InvalidateVisual(); // the background fill covers the new bounds
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

        /// <summary>Rebinds the property bar to the current tool's SavedToolSettings. Call after
        /// Editor.Tools is replaced wholesale (reset to defaults) — the skill bindings hold the
        /// previous instances and would otherwise keep reading/writing orphaned settings.</summary>
        public void ResyncToolSettings()
        {
            _syncStateForced = true;
            SyncObjectState();
        }

        private void SyncObjectState()
        {
            // we connect the current "object config" properties on this class to the relevant object.
            // decision table #11/#12: WPF SetBinding/ClearBinding becomes this.Bind + disposables, and
            // NotifyOnSourceUpdated becomes a PropertyChanged whitelist on the bound graphic, with the
            // _syncingState re-entrancy guard active for the duration of this method.

            // inputs-changed early-out (final-design §B.6): this runs per history mutation, so a
            // merged scrub must not dispose+recreate every binding per step. The full rebind only
            // happens when an input changed: the selection array (same instance when membership
            // and order are unchanged), the tool, panning mode, or a forced resync (first run and
            // undo/redo/restore, which write fields directly underneath any active bindings).
            var selected = GraphicsList.SelectedItems;
            if (!_syncStateForced && ReferenceEquals(selected, _lastSyncSelection) &&
                Tool == _lastSyncTool && IsPanning == _lastSyncPanning)
                return;
            _syncStateForced = false;
            _lastSyncSelection = selected;
            _lastSyncTool = Tool;
            _lastSyncPanning = IsPanning;

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
                    AddSettingBinding(Skill.Radius, CornerRadiusProperty, nameof(SavedToolSettings.CornerRadius));
                    AddSettingBinding(Skill.DashStyle, DashStyleProperty, nameof(SavedToolSettings.DashStyle));
                    AddSettingBinding(Skill.ObscureMode, ObscureModeProperty, nameof(SavedToolSettings.ObscureMode));

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
                    AddObjectBinding<GraphicRectangle>(Skill.Radius, CornerRadiusProperty, x => nameof(x.CornerRadius));
                    AddObjectBinding<GraphicBase>(Skill.DashStyle, DashStyleProperty, x => nameof(x.DashStyle));
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
            // command state still requeries per mutation (cheap); persistence routes through the
            // autosave throttle (final-design §B.6) — discrete actions (append/undo/redo) carry
            // the serialized document in the args and raise StateUpdated immediately, merge
            // rewrites (null State, the scrub path) only re-arm its 150ms trailing edge; and
            // SyncObjectState early-outs unless its inputs actually changed.
            RequeryCommands();
            _autosaveThrottle.OnHistoryChanged(_undoManager.LastChangeKind, e);
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
            _lastPointerRoot = CanvasToRoot(s.Position);

            // Re-seed the auto-repeat latch from the real modifier state at drag start. OnKeyDown /
            // OnKeyUp only observe keys routed through the canvas, so a Shift press or release that
            // happened while focus sat in a property-bar TextBox or a popup is invisible to them and
            // can leave the latch stale — which would swallow the next legitimate mid-drag replay.
            _shiftReplayed = (s.Modifiers & KeyModifiers.Shift) != 0;

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
            _lastPointerRoot = CanvasToRoot(s.Position);

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
            _lastPointerRoot = CanvasToRoot(s.Position);

            if (e.InitialPressMouseButton == MouseButton.Left)
            {
                _isToolMouseDown = false;
                CurrentTool.Instance.OnMouseUp(this, s);
                // drag over: one more validation re-bakes any interactively-capped shadow
                // sprites at full resolution (final-design §A.3 "full-res at rest")
                GraphicsList.RequestValidation();
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

            // Wheel zoom is anchored at the pointer position: the viewport point under the cursor
            // must not move. The render transform scales canvas-local coordinates by
            // ContentScale/DpiZoom (see UpdateScaleTransform), NOT by ContentScale — anchoring with
            // ContentScale alone only happens to be correct at 100% DPI, and drifts proportionally
            // to (DpiZoom - 1) everywhere else.
            // Anchor against ContentOffset, NOT _translateTransform: the transform is the *display*
            // value, floored to a whole device pixel for sharpness. Feeding that floored value back
            // in as the new offset discards the fractional part on every step, and because it is a
            // floor rather than a round the loss is signed — so repeated wheel clicks walked the
            // anchor steadily away from the pointer instead of jittering around it.
            var dpiZoom = DpiZoom;
            double absoluteX = relativeMouse.X * (ContentScale / dpiZoom) + ContentOffset.X;
            double absoluteY = relativeMouse.Y * (ContentScale / dpiZoom) + ContentOffset.Y;

            ContentScale = newZoom;

            ContentOffset = new Point(
                absoluteX - relativeMouse.X * (ContentScale / dpiZoom),
                absoluteY - relativeMouse.Y * (ContentScale / dpiZoom));
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            // Shift key replays a synthetic MouseMove, so any drag-based snapping will be updated.
            // Only the transition matters: Windows key auto-repeat delivers OnKeyDown continuously
            // while Shift is held, and re-running a drag step is not idempotent for every tool
            // (aspect-ratio resize re-derives from the graphic's *current* bounds, and a rotated
            // rectangle un-rotates the pointer about a centre that has just moved), so a held Shift
            // made the shape creep with the pointer completely stationary.
            // The flag latches on the first key-down whether or not a drag is in progress, so that
            // Shift held from BEFORE the drag started cannot land a replay part-way through it.
            if (e.Key == Key.LeftShift || e.Key == Key.RightShift)
            {
                if (_shiftReplayed)
                    return;

                _shiftReplayed = true;

                if (IsMouseCaptured)
                    ReplayCachedPointerMove(addShift: true);
            }
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);

            // Shift key replays a synthetic MouseMove, so any drag-based snapping will be updated.
            // Releasing ONE of two held shift keys is not a transition: KeyModifiers still reports
            // Shift, so replaying un-shifted would pop the shape out of its constrained aspect for a
            // frame, and clearing the latch would re-arm the replay the next auto-repeat delivers.
            if ((e.Key == Key.LeftShift || e.Key == Key.RightShift) && (e.KeyModifiers & KeyModifiers.Shift) == 0)
            {
                _shiftReplayed = false;

                if (IsMouseCaptured)
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

            // resolve the cached point through the transform as it is NOW, not as it was when the
            // point was cached — see _lastPointerRoot
            var position = _lastPointerRoot is { } root ? RootToCanvas(root) : last.Position;

            var synthetic = new PointerState(position, modifiers, last.LeftPressed, last.MiddlePressed, last.RightPressed, null);
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
            GraphicsList.RequestValidation(); // re-bake capped shadow sprites at rest (§A.3)

            this.ReleaseMouseCapture();
            this.Cursor = HelperFunctions.DefaultCursor;
            UnselectAll();
        }

        internal void AddCommandToHistory(bool mergable)
        {
            // the guard is enforced HERE, not just at the callers (final-design §B.4): the
            // in-place restore resets selection/editing state, and side effects of that (e.g.
            // GraphicImage.IsSelected=false → EndCrop → AddCommandToHistory) must be inert
            // while an undo/redo/restore is applying — they would otherwise commit phantom
            // steps mid-restore
            if (_syncingState)
                return;
            _undoManager.AddCommandStep(mergable);
        }

        /// <summary>
        /// Fires any armed autosave trailing edge synchronously (final-design §B.6): if a merged
        /// scrub's 150ms debounce is still pending, the live document is serialized once and
        /// <see cref="StateUpdated"/> raised before this returns. Called by EditorWindow during
        /// teardown (before it flushes its background graphics.json writer) and on
        /// DetachedFromVisualTree, so the latest committed state always reaches disk.
        /// </summary>
        public void FlushPendingState() => _autosaveThrottle.Flush();

        /// <summary>Serializes the undo chain for history.json (MIGRATION.md §8.8); the autosave
        /// throttle attaches it to every <see cref="StateUpdated"/> raise that carries a
        /// document, so graphics.json and history.json always land as a consistent pair.</summary>
        internal JsonObject SerializeHistory() => _undoManager.SerializeHistory();

        /// <summary>
        /// Rehydrates undo/redo history persisted alongside a graphics.json document (session
        /// reopen — MIGRATION.md §8.8). Call immediately after <see cref="RestoreState"/> with
        /// the same state object: the history is accepted only if replaying its baseline to its
        /// saved cursor reproduces <paramref name="expectedState"/> exactly (graphics.json
        /// remains the authority; null compares against the live document), and is discarded
        /// silently otherwise — identical to opening with no history file. The load boundary is
        /// never mergable: the first commit after a successful rehydrate starts a fresh merge
        /// chain. Raises no <see cref="StateUpdated"/> (contract #23 extends to history loading).
        /// </summary>
        public bool TryRestoreHistory(JsonObject history, JsonObject expectedState = null)
        {
            var ok = _undoManager.TryRehydrateHistory(history, expectedState ?? UndoManager.SerializeDocument(this));
            if (ok)
                RequeryCommands(); // Undo/Redo CanExecute now reflect the rehydrated chain
            return ok;
        }

        /// <summary>Raises <see cref="StateUpdated"/>; the autosave throttle is the only caller
        /// (every payload funnels through the §B.6 policy).</summary>
        internal void RaiseStateUpdated(StateChangedEventArgs e) => StateUpdated?.Invoke(this, e);

        /// <summary>The throttle skips building the history payload (and its emission caches)
        /// when nothing consumes the event — e.g. headless/benchmark canvases.</summary>
        internal bool HasStateUpdatedSubscribers => StateUpdated != null;

        /// <summary>
        /// Companion to <see cref="GraphicCollection.ConsumeDirty"/> (final-design §B.2): true if
        /// ArtworkBackground changed since the last consume. Read by the history engine at commit.
        /// </summary>
        internal bool ConsumeBackgroundDirty()
        {
            var dirty = _backgroundDirtySinceCommit;
            _backgroundDirtySinceCommit = false;
            return dirty;
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

        private Point CanvasToRoot(Point canvasPt) =>
            this.TranslatePoint(canvasPt, (Visual)TopLevel.GetTopLevel(this) ?? this) ?? canvasPt;

        private Point RootToCanvas(Point rootPt) =>
            ((Visual)TopLevel.GetTopLevel(this) ?? this).TranslatePoint(rootPt, this) ?? rootPt;

        /// <summary>
        /// Re-applies every scaling-derived value after the window moves to a monitor with different
        /// DPI. UpdateScaleTransform alone is not enough: the display translate is still floored onto
        /// the OLD monitor's device-pixel grid (so raster content renders soft until the next pan or
        /// zoom happens to re-floor it), and the clickable hit surface still pairs the new scale with
        /// geometry sized for the old one, which can leave dead zones for the pointer.
        /// </summary>
        public void UpdateForScalingChange()
        {
            if (_scaleTransform2 == null)
                return;

            UpdateScaleTransform();

            var dpiZoom = DpiZoom;
            _translateTransform.X = Math.Floor(ContentOffset.X * dpiZoom) / dpiZoom;
            _translateTransform.Y = Math.Floor(ContentOffset.Y * dpiZoom) / dpiZoom;

            UpdateClickableSurface();
        }

        protected override void OnLoaded(RoutedEventArgs e)
        {
            base.OnLoaded(e);

            // Full scaling refresh, not just the scale transform: DpiZoom falls back to 1.0 while
            // detached, so a ContentOffset restored before attach was floored onto the logical grid
            // rather than the monitor's device-pixel grid. On a >100% monitor that leaves restored
            // sessions rendering soft until the first pan or zoom happens to re-floor it.
            UpdateForScalingChange();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            // teardown flush (final-design §B.6): a debounced scrub tail must not die with the
            // visual tree — serialize and raise StateUpdated now so the session writer gets it
            FlushPendingState();
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

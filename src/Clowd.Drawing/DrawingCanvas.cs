using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Serialization;
using Clowd.Drawing.Tools;
using Clowd.Drawing.Graphics;
using Clowd.Drawing.Commands;

namespace Clowd.Drawing
{
    public class DrawingCanvas : Canvas
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

        public GraphicCollection GraphicsList { get; }

        public int Count => GraphicsList.Count;

        public IEnumerable<GraphicBase> SelectedItems => GraphicsList.Where(g => g.IsSelected);

        internal ToolPointer ToolPointer;

        private ToolBase[] _tools;
        private Border _clickable;
        private Border _artworkRectangle;
        private ContextMenu _contextMenu;
        private UndoManager _undoManager;

        public DrawingCanvas()
        {
            GraphicsList = new GraphicCollection(this);

            CreateContextMenu();

            // create array of drawing tools
            _tools = new ToolBase[(int)ToolType.Max];

            ToolPointer = new ToolPointer();
            _tools[(int)ToolType.None] = ToolPointer;
            _tools[(int)ToolType.Pointer] = ToolPointer;

            _tools[(int)ToolType.Rectangle] = new ToolDraggable<GraphicRectangle>(
                Resource.CursorRectangle,
                point => new GraphicRectangle(this, new Rect(point, new Size(1, 1))),
                (point, g) => g.MoveHandleTo(point, 5));

            _tools[(int)ToolType.FilledRectangle] = new ToolDraggable<GraphicFilledRectangle>(
                Resource.CursorRectangle,
                point => new GraphicFilledRectangle(this, new Rect(point, new Size(1, 1))),
                (point, g) => g.MoveHandleTo(point, 5));

            _tools[(int)ToolType.Ellipse] = new ToolDraggable<GraphicEllipse>(
                Resource.CursorEllipse,
                point => new GraphicEllipse(this, new Rect(point, new Size(1, 1))),
                (point, g) => g.MoveHandleTo(point, 5));

            _tools[(int)ToolType.Line] = new ToolDraggable<GraphicLine>(
                Resource.CursorLine,
                point => new GraphicLine(this, point, point),
                (point, g) => g.MoveHandleTo(point, 2));

            _tools[(int)ToolType.Arrow] = new ToolDraggable<GraphicArrow>(
                Resource.CursorArrow,
                point => new GraphicArrow(this, point, point),
                (point, g) => g.MoveHandleTo(point, 2));

            _tools[(int)ToolType.PolyLine] = new ToolPolyLine();

            _tools[(int)ToolType.Text] = new ToolText(this);

            //tools[(int)ToolType.Pixelate] = new ToolFilter<FilterPixelate>();
            //tools[(int)ToolType.Erase] = new ToolFilter<FilterEraser>();

            // Create undo manager
            _undoManager = new UndoManager(this);
            _undoManager.StateChanged += new EventHandler(UndoManager_StateChanged);

            this.FocusVisualStyle = null;

            this.Loaded += new RoutedEventHandler(DrawingCanvas_Loaded);
            this.MouseDown += new MouseButtonEventHandler(DrawingCanvas_MouseDown);
            this.MouseMove += new MouseEventHandler(DrawingCanvas_MouseMove);
            this.MouseUp += new MouseButtonEventHandler(DrawingCanvas_MouseUp);
            this.MouseWheel += DrawingCanvas_MouseWheel;
            this.KeyDown += new KeyEventHandler(DrawingCanvas_KeyDown);
            this.LostMouseCapture += new MouseEventHandler(DrawingCanvas_LostMouseCapture);

            InitializeZoom();

            _clickable = new Border();
            _clickable.Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
            Children.Add(_clickable);

            _artworkRectangle = new Border();
            //_artworkRectangle.Background = new SolidColorBrush(Colors.White);

            var binding = new System.Windows.Data.Binding(nameof(ArtworkBackground));
            binding.Source = this;
            binding.Mode = System.Windows.Data.BindingMode.OneWay;
            _artworkRectangle.SetBinding(Border.BackgroundProperty, binding);

            Children.Add(_artworkRectangle);

            SnapsToDevicePixels = false;
            UseLayoutRounding = false;
        }

        static DrawingCanvas()
        {
            // **********************************************************
            // Create dependency properties
            // **********************************************************

            PropertyMetadata metaData;

            // Tool
            metaData = new PropertyMetadata(ToolType.Pointer);

            ToolProperty = DependencyProperty.Register(
                "Tool", typeof(ToolType), typeof(DrawingCanvas),
                metaData);

            // ArtworkBackground
            metaData = new PropertyMetadata(Brushes.Purple);
            ArtworkBackgroundProperty = DependencyProperty.Register(nameof(ArtworkBackground), typeof(Brush), typeof(DrawingCanvas), metaData);

            // LineWidth
            metaData = new PropertyMetadata(
                1.0,
                new PropertyChangedCallback(LineWidthChanged));

            LineWidthProperty = DependencyProperty.Register(
                "LineWidth", typeof(double), typeof(DrawingCanvas),
                metaData);

            // ObjectColor
            metaData = new PropertyMetadata(
                Colors.Black,
                new PropertyChangedCallback(ObjectColorChanged));

            ObjectColorProperty = DependencyProperty.Register(
                "ObjectColor", typeof(Color), typeof(DrawingCanvas),
                metaData);

            // HandleColor
            HandleColorProperty = DependencyProperty.Register(
                "HandleColor", typeof(Color), typeof(DrawingCanvas),
                new PropertyMetadata(Color.FromRgb(37, 97, 163), HandleColorChanged));

            // TextFontFamilyName
            metaData = new PropertyMetadata(
                "Tahoma",
                new PropertyChangedCallback(TextFontFamilyNameChanged));

            TextFontFamilyNameProperty = DependencyProperty.Register(
                "TextFontFamilyName", typeof(string), typeof(DrawingCanvas),
                metaData);

            // TextFontStyle
            metaData = new PropertyMetadata(
                FontStyles.Normal,
                new PropertyChangedCallback(TextFontStyleChanged));

            TextFontStyleProperty = DependencyProperty.Register(
                "TextFontStyle", typeof(FontStyle), typeof(DrawingCanvas),
                metaData);

            // TextFontWeight
            metaData = new PropertyMetadata(
                FontWeights.Normal,
                new PropertyChangedCallback(TextFontWeightChanged));

            TextFontWeightProperty = DependencyProperty.Register(
                "TextFontWeight", typeof(FontWeight), typeof(DrawingCanvas),
                metaData);

            // TextFontStretch
            metaData = new PropertyMetadata(
                FontStretches.Normal,
                new PropertyChangedCallback(TextFontStretchChanged));

            TextFontStretchProperty = DependencyProperty.Register(
                "TextFontStretch", typeof(FontStretch), typeof(DrawingCanvas),
                metaData);

            // TextFontSize
            metaData = new PropertyMetadata(
                12.0,
                new PropertyChangedCallback(TextFontSizeChanged));

            TextFontSizeProperty = DependencyProperty.Register(
                "TextFontSize", typeof(double), typeof(DrawingCanvas),
                metaData);

            // CanUndo
            metaData = new PropertyMetadata(false);

            CanUndoProperty = DependencyProperty.Register(
                "CanUndo", typeof(bool), typeof(DrawingCanvas),
                metaData);

            // CanRedo
            metaData = new PropertyMetadata(false);

            CanRedoProperty = DependencyProperty.Register(
                "CanRedo", typeof(bool), typeof(DrawingCanvas),
                metaData);

            // CanSelectAll
            metaData = new PropertyMetadata(false);

            CanSelectAllProperty = DependencyProperty.Register(
                "CanSelectAll", typeof(bool), typeof(DrawingCanvas),
                metaData);

            // CanUnselectAll
            metaData = new PropertyMetadata(false);

            CanUnselectAllProperty = DependencyProperty.Register(
                "CanUnselectAll", typeof(bool), typeof(DrawingCanvas),
                metaData);

            // CanDelete
            metaData = new PropertyMetadata(false);

            CanDeleteProperty = DependencyProperty.Register(
                "CanDelete", typeof(bool), typeof(DrawingCanvas),
                metaData);

            // CanDeleteAll
            metaData = new PropertyMetadata(false);

            CanDeleteAllProperty = DependencyProperty.Register(
                "CanDeleteAll", typeof(bool), typeof(DrawingCanvas),
                metaData);

            // CanMoveToFront
            metaData = new PropertyMetadata(false);

            CanMoveToFrontProperty = DependencyProperty.Register(
                "CanMoveToFront", typeof(bool), typeof(DrawingCanvas),
                metaData);

            // CanMoveToBack
            metaData = new PropertyMetadata(false);

            CanMoveToBackProperty = DependencyProperty.Register(
                "CanMoveToBack", typeof(bool), typeof(DrawingCanvas),
                metaData);

            // CanSetProperties
            metaData = new PropertyMetadata(false);

            CanSetPropertiesProperty = DependencyProperty.Register(
                "CanSetProperties", typeof(bool), typeof(DrawingCanvas),
                metaData);
        }

        #region Dependency Properties

        public static readonly DependencyProperty ToolProperty;
        public static readonly DependencyProperty ArtworkBackgroundProperty;
        public static readonly DependencyProperty LineWidthProperty;
        public static readonly DependencyProperty ObjectColorProperty;
        public static readonly DependencyProperty HandleColorProperty;
        public static readonly DependencyProperty TextFontFamilyNameProperty;
        public static readonly DependencyProperty TextFontStyleProperty;
        public static readonly DependencyProperty TextFontWeightProperty;
        public static readonly DependencyProperty TextFontStretchProperty;
        public static readonly DependencyProperty TextFontSizeProperty;
        public static readonly DependencyProperty CanUndoProperty;
        public static readonly DependencyProperty CanRedoProperty;
        public static readonly DependencyProperty CanSelectAllProperty;
        public static readonly DependencyProperty CanUnselectAllProperty;
        public static readonly DependencyProperty CanDeleteProperty;
        public static readonly DependencyProperty CanDeleteAllProperty;
        public static readonly DependencyProperty CanMoveToFrontProperty;
        public static readonly DependencyProperty CanMoveToBackProperty;
        public static readonly DependencyProperty CanSetPropertiesProperty;

        public ToolType Tool
        {
            get
            {
                return (ToolType)GetValue(ToolProperty);
            }
            set
            {
                if ((int)value >= 0 && (int)value < (int)ToolType.Max)
                {
                    SetValue(ToolProperty, value);

                    // Set cursor immediately - important when tool is selected from the menu
                    var tmp = _tools[(int)Tool];
                    if (tmp == null)
                        Cursor = Cursors.SizeAll;
                    else
                        tmp.SetCursor(this);
                }
            }
        }

        public Brush ArtworkBackground
        {
            get
            {
                return (Brush)GetValue(ArtworkBackgroundProperty);
            }
            set
            {
                SetValue(ArtworkBackgroundProperty, value);
            }
        }

        public bool CanUndo
        {
            get
            {
                return (bool)GetValue(CanUndoProperty);
            }
            internal set
            {
                SetValue(CanUndoProperty, value);
            }
        }

        public bool CanRedo
        {
            get
            {
                return (bool)GetValue(CanRedoProperty);
            }
            internal set
            {
                SetValue(CanRedoProperty, value);
            }
        }

        public bool CanSelectAll
        {
            get
            {
                return (bool)GetValue(CanSelectAllProperty);
            }
            internal set
            {
                SetValue(CanSelectAllProperty, value);
            }
        }

        public bool CanUnselectAll
        {
            get
            {
                return (bool)GetValue(CanUnselectAllProperty);
            }
            internal set
            {
                SetValue(CanUnselectAllProperty, value);
            }
        }

        public bool CanDelete
        {
            get
            {
                return (bool)GetValue(CanDeleteProperty);
            }
            internal set
            {
                SetValue(CanDeleteProperty, value);
            }
        }

        public bool CanDeleteAll
        {
            get
            {
                return (bool)GetValue(CanDeleteAllProperty);
            }
            internal set
            {
                SetValue(CanDeleteAllProperty, value);
            }
        }

        public bool CanMoveToFront
        {
            get
            {
                return (bool)GetValue(CanMoveToFrontProperty);
            }
            internal set
            {
                SetValue(CanMoveToFrontProperty, value);
            }
        }

        public bool CanMoveToBack
        {
            get
            {
                return (bool)GetValue(CanMoveToBackProperty);
            }
            internal set
            {
                SetValue(CanMoveToBackProperty, value);
            }
        }

        public bool CanSetProperties
        {
            get
            {
                return (bool)GetValue(CanSetPropertiesProperty);
            }
            internal set
            {
                SetValue(CanSetPropertiesProperty, value);
            }
        }

        public double LineWidth
        {
            get
            {
                return (double)GetValue(LineWidthProperty);
            }
            set
            {
                SetValue(LineWidthProperty, value);

            }
        }

        static void LineWidthChanged(DependencyObject property, DependencyPropertyChangedEventArgs args)
        {
            DrawingCanvas d = property as DrawingCanvas;

            CommandChangeState command = new CommandChangeState(d);
            bool wasChange = false;

            foreach (GraphicBase g in d.SelectedItems)
            {
                if (g is GraphicText || g is GraphicSelectionRectangle)
                    continue;
                if (g.LineWidth != d.LineWidth)
                {
                    g.LineWidth = d.LineWidth;
                    wasChange = true;
                }
            }

            if (wasChange)
            {
                command.NewState(d);
                d.AddCommandToHistory(command);
            }
        }

        public Color ObjectColor
        {
            get
            {
                return (Color)GetValue(ObjectColorProperty);
            }
            set
            {
                SetValue(ObjectColorProperty, value);

            }
        }

        static void ObjectColorChanged(DependencyObject property, DependencyPropertyChangedEventArgs args)
        {
            DrawingCanvas d = property as DrawingCanvas;

            CommandChangeState command = new CommandChangeState(d);
            bool wasChange = false;
            var value = d.ObjectColor;

            foreach (GraphicBase g in d.SelectedItems)
            {
                if (g.ObjectColor != value)
                {
                    g.ObjectColor = value;
                    wasChange = true;
                }
            }

            if (wasChange)
            {
                command.NewState(d);
                d.AddCommandToHistory(command);
            }
        }

        public Color HandleColor
        {
            get { return (Color)GetValue(HandleColorProperty); }
            set { SetValue(HandleColorProperty, value); }
        }

        private static void HandleColorChanged(DependencyObject property, DependencyPropertyChangedEventArgs e)
        {
            DrawingCanvas d = property as DrawingCanvas;
            GraphicBase.HandleBrush = new SolidColorBrush(d.HandleColor);
        }

        public string TextFontFamilyName
        {
            get
            {
                return (string)GetValue(TextFontFamilyNameProperty);
            }
            set
            {
                SetValue(TextFontFamilyNameProperty, value);

            }
        }

        static void TextFontFamilyNameChanged(DependencyObject property, DependencyPropertyChangedEventArgs args)
        {
            ApplyFontChange(property as DrawingCanvas, d => d.TextFontFamilyName, t => t.FontName, (t, v) => { t.FontName = v; });
        }

        private static void ApplyFontChange<TPropertyType>(DrawingCanvas d, Func<DrawingCanvas, TPropertyType> getProp, Func<GraphicText, TPropertyType> getTextProp, Action<GraphicText, TPropertyType> setTextProp)
        {
            CommandChangeState command = new CommandChangeState(d);
            bool wasChange = false;
            var value = getProp(d);

            foreach (GraphicBase g in d.SelectedItems)
            {
                var t = g as GraphicText;
                if (t == null)
                    continue;
                if (!Equals(getTextProp(t), value))
                {
                    setTextProp(t, value);
                    wasChange = true;
                }
            }

            if (wasChange)
            {
                command.NewState(d);
                d.AddCommandToHistory(command);
            }
        }

        public FontStyle TextFontStyle
        {
            get
            {
                return (FontStyle)GetValue(TextFontStyleProperty);
            }
            set
            {
                SetValue(TextFontStyleProperty, value);

            }
        }

        static void TextFontStyleChanged(DependencyObject property, DependencyPropertyChangedEventArgs args)
        {
            ApplyFontChange(property as DrawingCanvas, d => d.TextFontStyle, t => t.FontStyle, (t, v) => { t.FontStyle = v; });
        }

        public FontWeight TextFontWeight
        {
            get
            {
                return (FontWeight)GetValue(TextFontWeightProperty);
            }
            set
            {
                SetValue(TextFontWeightProperty, value);

            }
        }

        static void TextFontWeightChanged(DependencyObject property, DependencyPropertyChangedEventArgs args)
        {
            ApplyFontChange(property as DrawingCanvas, d => d.TextFontWeight, t => t.FontWeight, (t, v) => { t.FontWeight = v; });
        }

        public FontStretch TextFontStretch
        {
            get
            {
                return (FontStretch)GetValue(TextFontStretchProperty);
            }
            set
            {
                SetValue(TextFontStretchProperty, value);

            }
        }

        static void TextFontStretchChanged(DependencyObject property, DependencyPropertyChangedEventArgs args)
        {
            ApplyFontChange(property as DrawingCanvas, d => d.TextFontStretch, t => t.FontStretch, (t, v) => { t.FontStretch = v; });
        }

        public double TextFontSize
        {
            get
            {
                return (double)GetValue(TextFontSizeProperty);
            }
            set
            {
                SetValue(TextFontSizeProperty, value);

            }
        }

        static void TextFontSizeChanged(DependencyObject property, DependencyPropertyChangedEventArgs args)
        {
            ApplyFontChange(property as DrawingCanvas, d => d.TextFontSize, t => t.FontSize, (t, v) => { t.FontSize = v; });
        }

        #endregion Dependency Properties

        #region Public Functions

        public void Draw(DrawingContext drawingContext, RenderTargetBitmap bitmap, Transform transform, bool withSelection)
        {
            bool oldSelection = false;

            foreach (GraphicBase b in GraphicsList)
            {
                if (!withSelection)
                {
                    // Keep selection state and unselect
                    oldSelection = b.IsSelected;
                    b.IsSelected = false;
                }

                DrawingVisual vis = new DrawingVisual();
                vis.Effect = b.Effect;
                using (var cx = vis.RenderOpen())
                {
                    cx.PushTransform(transform);
                    b.Draw(cx);
                }
                if (drawingContext != null)
                    drawingContext.DrawRectangle(new VisualBrush(vis), null, vis.ContentBounds);
                if (bitmap != null)
                    bitmap.Render(vis);

                if (!withSelection)
                {
                    // Restore selection state
                    b.IsSelected = oldSelection;
                }
            }
        }

        public ToolActionType GetToolActionType(ToolType type)
        {
            try
            {
                return this._tools[(int)type].ActionType;
            }
            catch (IndexOutOfRangeException)
            {
                return ToolActionType.Cursor;
            }
        }

        public Rect GetArtworkBounds(bool selectedOnly = false)
        {
            var artwork = GraphicsList.Cast<GraphicBase>()
                .Where(g => !(g is GraphicSelectionRectangle));
            Rect result = new Rect(0, 0, 0, 0);
            bool first = true;
            foreach (var item in artwork)
            {
                if (selectedOnly && !item.IsSelected)
                    continue;
                var rect = item.Bounds;
                if (first)
                {
                    result = rect;
                    first = false;
                    continue;
                }
                result.Union(rect);
            }
            return result;
        }

        public void Clear()
        {
            GraphicsList.Clear();
            ClearHistory();
            UpdateState();
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

            AddCommandToHistory(new CommandAdd(g));
            UpdateState();
            InvalidateVisual();
            RefreshBounds();
        }

        public void AddGraphicsFromStream(Stream stream)
        {
            SerializationHelper helper;
            XmlSerializer xml = new XmlSerializer(typeof(SerializationHelper));

            helper = (SerializationHelper)xml.Deserialize(stream);

            var transformX = (-helper.Left - helper.Width / 2) + ((ActualWidth / 2 - ContentOffset.X) / ContentScale);
            var transformY = (-helper.Top - helper.Height / 2) + ((ActualHeight / 2 - ContentOffset.Y) / ContentScale);

            this.UnselectAll();
            foreach (var g in helper.Graphics)
            {
                g.Move(transformX, transformY);
                g.IsSelected = true;
                g.Normalize();
                GraphicsList.Add(g);
            }

            AddCommandToHistory(new CommandAdd(helper.Graphics));
            UpdateState();
            InvalidateVisual();
            RefreshBounds();
        }

        public void WriteGraphicsToStream(Stream stream, bool selectedOnly)
        {
            GraphicBase[] graphics = GraphicsList.OfType<GraphicBase>().Where(g => g.IsSelected || !selectedOnly).ToArray();
            if (!graphics.Any())
                graphics = GraphicsList.OfType<GraphicBase>().ToArray();

            var helper = new SerializationHelper(graphics.Select(g => g), GetArtworkBounds(true));

            XmlSerializer xml = new XmlSerializer(typeof(SerializationHelper));
            xml.Serialize(stream, helper);
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
            CommandDelete command = new CommandDelete(this);
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
                this.AddCommandToHistory(command);
            }

            UpdateState();
            RefreshBounds();
        }

        public void DeleteAll()
        {
            if (GraphicsList.Count > 0)
            {
                AddCommandToHistory(new CommandDeleteAll(this));
                GraphicsList.Clear();
            }

            UpdateState();
            RefreshBounds();
        }

        public void MoveToFront()
        {
            List<GraphicBase> list = new List<GraphicBase>();

            CommandChangeOrder command = new CommandChangeOrder(this);

            for (int i = this.Count - 1; i >= 0; i--)
            {
                if (this[i].IsSelected)
                {
                    list.Insert(0, this[i]);
                    this.GraphicsList.RemoveAt(i);
                }
            }

            // Add all items from temporary list to the end of GraphicsList
            foreach (GraphicBase g in list)
            {
                this.GraphicsList.Add(g);
            }

            if (list.Count > 0)
            {
                command.NewState(this);
                this.AddCommandToHistory(command);
            }

            UpdateState();
        }

        public void MoveToBack()
        {
            List<GraphicBase> list = new List<GraphicBase>();

            CommandChangeOrder command = new CommandChangeOrder(this);

            for (int i = this.Count - 1; i >= 0; i--)
            {
                if (this[i].IsSelected)
                {
                    list.Add(this[i]);
                    this.GraphicsList.RemoveAt(i);
                }
            }

            // Add all items from temporary list to the beginning of GraphicsList
            foreach (GraphicBase g in list)
            {
                this.GraphicsList.Insert(0, g);
            }

            if (list.Count > 0)
            {
                command.NewState(this);
                this.AddCommandToHistory(command);
            }

            UpdateState();
        }

        public void ResetRotation()
        {
            var changed = false;
            for (int i = this.Count - 1; i >= 0; i--)
            {
                var rect = this[i] as GraphicRectangle;
                if (rect != null && this[i].IsSelected)
                {
                    if (rect.Angle != 0)
                        changed = true;
                    rect.Angle = 0;
                }
            }

            if (changed)
            {
                CommandChangeState command = new CommandChangeState(this);
                command.NewState(this);
                AddCommandToHistory(command);
            }

            UpdateState();
            RefreshBounds();
        }

        public void SetProperties()
        {
            CommandChangeState command = new CommandChangeState(this);
            bool changed = false;

            foreach (var v in GraphicsList.Cast<GraphicBase>())
            {
                if (v.LineWidth != LineWidth)
                {
                    changed = true;
                    v.LineWidth = LineWidth;
                }
                if (v.ObjectColor != ObjectColor)
                {
                    changed = true;
                    v.ObjectColor = ObjectColor;
                }
                var t = v as GraphicText;
                if (t != null)
                {
                    if (t.FontName != TextFontFamilyName)
                    {
                        changed = true;
                        t.FontName = TextFontFamilyName;
                    }
                    if (t.FontSize != TextFontSize)
                    {
                        changed = true;
                        t.FontSize = TextFontSize;
                    }
                    if (t.FontStretch != TextFontStretch)
                    {
                        changed = true;
                        t.FontStretch = TextFontStretch;
                    }
                    if (t.FontStyle != TextFontStyle)
                    {
                        changed = true;
                        t.FontStyle = TextFontStyle;
                    }
                    if (t.FontWeight != TextFontWeight)
                    {
                        changed = true;
                        t.FontWeight = TextFontWeight;
                    }
                }
            }

            if (changed)
            {
                command.NewState(this);
                AddCommandToHistory(command);
            }

            UpdateState();
            RefreshBounds();
        }

        public void Undo()
        {
            _undoManager.Undo();
            UpdateState();
            RefreshBounds();
        }

        public void Redo()
        {
            _undoManager.Redo();
            UpdateState();
            RefreshBounds();
        }

        #endregion Public Functions

        #region Visual Children Overrides

        protected override int VisualChildrenCount => GraphicsList.VisualCount + Children.Count;

        protected override Visual GetVisualChild(int index)
        {
            // _clickable and _artworkbounds come first, 
            // any other children come after.

            if (index == 0)
            {
                if (_clickable != null && ActualWidth > 0 && ContentScale > 0)
                {
                    Canvas.SetLeft(_clickable, -_translateTransform.X / _scaleTransform2.ScaleX);
                    Canvas.SetTop(_clickable, -_translateTransform.Y / _scaleTransform2.ScaleY);
                    _clickable.Width = ActualWidth / _scaleTransform2.ScaleX;
                    _clickable.Height = ActualHeight / _scaleTransform2.ScaleY;
                }
                return _clickable;
            }
            else if (index == 1)
            {
                return _artworkRectangle;
            }
            else if (index - 2 < GraphicsList.VisualCount)
            {
                return GraphicsList.GetVisual(index - 2);
            }
            else if (index - 2 - GraphicsList.VisualCount < Children.Count)
            {
                // any other children.
                return Children[index - GraphicsList.VisualCount];
            }
            //else if (index == 2 + graphicsList.VisualCount && toolText.TextBox != null)
            //{
            //    return toolText.TextBox;
            //}

            throw new ArgumentOutOfRangeException("index");
        }

        #endregion Visual Children Overrides

        #region Event Handlers

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
                ShowContextMenu(e);
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

            if (e.LeftButton == MouseButtonState.Pressed)
                RefreshBounds();
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

            double zoom = e.Delta > 0 ? .2 : -.2;
            if (!(e.Delta > 0) && (ContentScale < .3 || ContentScale < .3))
                return;

            Point relativeMouse = e.GetPosition(this);
            double absoluteX;
            double absoluteY;

            absoluteX = relativeMouse.X * ContentScale + _translateTransform.X;
            absoluteY = relativeMouse.Y * ContentScale + _translateTransform.Y;

            ContentScale += zoom;
            ContentOffset = new Point(absoluteX - relativeMouse.X * ContentScale, absoluteY - relativeMouse.Y * ContentScale);
        }

        void DrawingCanvas_Loaded(object sender, RoutedEventArgs e)
        {
            this.Focusable = true;      // to handle keyboard messages
        }

        void ContextMenuItem_Click(object sender, RoutedEventArgs e)
        {
            MenuItem item = sender as MenuItem;

            if (item == null)
            {
                return;
            }

            ContextMenuCommand command = (ContextMenuCommand)item.Tag;

            switch (command)
            {
                case ContextMenuCommand.SelectAll:
                    SelectAll();
                    break;
                case ContextMenuCommand.UnselectAll:
                    UnselectAll();
                    break;
                case ContextMenuCommand.Delete:
                    Delete();
                    break;
                case ContextMenuCommand.DeleteAll:
                    DeleteAll();
                    break;
                case ContextMenuCommand.MoveToFront:
                    MoveToFront();
                    break;
                case ContextMenuCommand.MoveToBack:
                    MoveToBack();
                    break;
                case ContextMenuCommand.Undo:
                    Undo();
                    break;
                case ContextMenuCommand.Redo:
                    Redo();
                    break;
                case ContextMenuCommand.SetProperties:
                    SetProperties();
                    break;
                case ContextMenuCommand.ResetRotation:
                    ResetRotation();
                    break;
            }
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
        }

        void UndoManager_StateChanged(object sender, EventArgs e)
        {
            this.CanUndo = _undoManager.CanUndo;
            this.CanRedo = _undoManager.CanRedo;
        }

        #endregion Event Handlers

        #region Other Functions

        void CreateContextMenu()
        {
            _contextMenu = new ContextMenu();
            _contextMenu.PlacementTarget = this;
            _contextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            this.ContextMenu = _contextMenu;

            MenuItem menuItem;

            menuItem = new MenuItem();
            menuItem.Header = "Select all";
            menuItem.InputGestureText = "Ctrl+A";
            menuItem.Tag = ContextMenuCommand.SelectAll;
            menuItem.Click += new RoutedEventHandler(ContextMenuItem_Click);
            _contextMenu.Items.Add(menuItem);

            menuItem = new MenuItem();
            menuItem.Header = "Unselect all";
            menuItem.InputGestureText = "Esc";
            menuItem.Tag = ContextMenuCommand.UnselectAll;
            menuItem.Click += new RoutedEventHandler(ContextMenuItem_Click);
            _contextMenu.Items.Add(menuItem);

            menuItem = new MenuItem();
            menuItem.Header = "Delete";
            menuItem.InputGestureText = "Del";
            menuItem.Tag = ContextMenuCommand.Delete;
            menuItem.Click += new RoutedEventHandler(ContextMenuItem_Click);
            _contextMenu.Items.Add(menuItem);

            menuItem = new MenuItem();
            menuItem.Header = "Delete all";
            menuItem.Tag = ContextMenuCommand.DeleteAll;
            menuItem.Click += new RoutedEventHandler(ContextMenuItem_Click);
            _contextMenu.Items.Add(menuItem);

            _contextMenu.Items.Add(new Separator());

            menuItem = new MenuItem();
            menuItem.Header = "Move to front";
            menuItem.InputGestureText = "Home";
            menuItem.Tag = ContextMenuCommand.MoveToFront;
            menuItem.Click += new RoutedEventHandler(ContextMenuItem_Click);
            _contextMenu.Items.Add(menuItem);

            menuItem = new MenuItem();
            menuItem.Header = "Move to back";
            menuItem.InputGestureText = "End";
            menuItem.Tag = ContextMenuCommand.MoveToBack;
            menuItem.Click += new RoutedEventHandler(ContextMenuItem_Click);
            _contextMenu.Items.Add(menuItem);

            _contextMenu.Items.Add(new Separator());

            menuItem = new MenuItem();
            menuItem.Header = "Undo";
            menuItem.InputGestureText = "Ctrl+Z";
            menuItem.Tag = ContextMenuCommand.Undo;
            menuItem.Click += new RoutedEventHandler(ContextMenuItem_Click);
            _contextMenu.Items.Add(menuItem);

            menuItem = new MenuItem();
            menuItem.Header = "Redo";
            menuItem.InputGestureText = "Ctrl+Y";
            menuItem.Tag = ContextMenuCommand.Redo;
            menuItem.Click += new RoutedEventHandler(ContextMenuItem_Click);
            _contextMenu.Items.Add(menuItem);

            menuItem = new MenuItem();
            menuItem.Header = "Reset attributes";
            menuItem.Tag = ContextMenuCommand.SetProperties;
            menuItem.Click += new RoutedEventHandler(ContextMenuItem_Click);
            _contextMenu.Items.Add(menuItem);

            menuItem = new MenuItem();
            menuItem.Header = "Reset rotation";
            menuItem.Tag = ContextMenuCommand.ResetRotation;
            menuItem.Click += new RoutedEventHandler(ContextMenuItem_Click);
            _contextMenu.Items.Add(menuItem);

            _contextMenu.Items.Add(new Separator());

            menuItem = new MenuItem();
            menuItem.Header = "Zoom to fit content";
            menuItem.InputGestureText = "Ctrl+0";
            menuItem.Click += (s, e) => this.ZoomPanFit();
            _contextMenu.Items.Add(menuItem);

            menuItem = new MenuItem();
            menuItem.Header = "Zoom to actual size";
            menuItem.InputGestureText = "Ctrl+1";
            menuItem.Click += (s, e) => this.ZoomPanActualSize();
            _contextMenu.Items.Add(menuItem);

        }

        void ShowContextMenu(MouseButtonEventArgs e)
        {
            // Change current selection if necessary
            Point point = e.GetPosition(this);
            var hitObject = GraphicsList.FirstOrDefault(g => g.MakeHitTest(point) >= 0);
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

            // Enable/disable menu items according to state.
            foreach (object obj in _contextMenu.Items)
            {
                MenuItem item = obj as MenuItem;
                if (item != null && item.Tag is ContextMenuCommand command)
                {
                    switch (command)
                    {
                        case ContextMenuCommand.SelectAll:
                            item.IsEnabled = CanSelectAll;
                            break;
                        case ContextMenuCommand.UnselectAll:
                            item.IsEnabled = CanUnselectAll;
                            break;
                        case ContextMenuCommand.Delete:
                            item.IsEnabled = CanDelete;
                            break;
                        case ContextMenuCommand.DeleteAll:
                            item.IsEnabled = CanDeleteAll;
                            break;
                        case ContextMenuCommand.MoveToFront:
                            item.IsEnabled = CanMoveToFront;
                            break;
                        case ContextMenuCommand.MoveToBack:
                            item.IsEnabled = CanMoveToBack;
                            break;
                        case ContextMenuCommand.Undo:
                            item.IsEnabled = CanUndo;
                            break;
                        case ContextMenuCommand.Redo:
                            item.IsEnabled = CanRedo;
                            break;
                        case ContextMenuCommand.SetProperties:
                            item.IsEnabled = CanSetProperties;
                            break;
                    }
                }
            }
        }

        void CancelCurrentOperation()
        {
            if (Tool == ToolType.Pointer)
            {
                if (GraphicsList.Count > 0)
                {
                    if (GraphicsList[GraphicsList.Count - 1] is GraphicSelectionRectangle)
                    {
                        // Delete selection rectangle if it exists
                        GraphicsList.RemoveAt(GraphicsList.Count - 1);
                    }
                    else
                    {
                        // Pointer tool moved or resized graphics object.
                        // Add this action to the history
                        ToolPointer.AddChangeToHistory(this);
                    }
                }
            }
            else if (Tool > ToolType.Pointer && Tool < ToolType.Max)
            {
                // Delete last graphics object which is currently drawn
                if (GraphicsList.Count > 0)
                {
                    GraphicsList.RemoveAt(GraphicsList.Count - 1);
                }
            }

            Tool = ToolType.Pointer;

            this.ReleaseMouseCapture();
            this.Cursor = HelperFunctions.DefaultCursor;
        }

        internal void AddCommandToHistory(CommandBase command)
        {
            _undoManager.AddCommandToHistory(command);
        }

        void ClearHistory()
        {
            _undoManager.ClearHistory();
        }

        void UpdateState()
        {
            bool hasObjects = (this.Count > 0);
            bool hasSelectedObjects = (this.SelectionCount > 0);

            CanSelectAll = hasObjects;
            CanUnselectAll = hasObjects;
            CanDelete = hasSelectedObjects;
            CanDeleteAll = hasObjects;
            CanMoveToFront = hasSelectedObjects;
            CanMoveToBack = hasSelectedObjects;
            CanSetProperties = hasSelectedObjects;
        }

        void RefreshBounds()
        {
            var bounds = GetArtworkBounds();
            Canvas.SetLeft(_artworkRectangle, bounds.Left);
            Canvas.SetTop(_artworkRectangle, bounds.Top);
            _artworkRectangle.Width = bounds.Width;
            _artworkRectangle.Height = bounds.Height;
        }

        internal ToolBase GetTool(ToolType type)
        {
            return _tools[(int)type];
        }

        #endregion Other Functions

        #region Zooming and Panning

        public bool IsPanning { get; private set; }

        public Point ContentOffset
        {
            get { return (Point)GetValue(ContentOffsetProperty); }
            set { SetValue(ContentOffsetProperty, value); }
        }
        public static readonly DependencyProperty ContentOffsetProperty =
            DependencyProperty.Register("ContentOffset", typeof(Point), typeof(DrawingCanvas),
                new PropertyMetadata(new Point(0, 0), ContentOffsetChanged));

        public double ContentScale
        {
            get { return (double)GetValue(ContentScaleProperty); }
            set { SetValue(ContentScaleProperty, value); }
        }
        public static readonly DependencyProperty ContentScaleProperty =
            DependencyProperty.Register("ContentScale", typeof(double), typeof(DrawingCanvas),
                new PropertyMetadata(1d, ContentScaleChanged));

        public Point WorldOffset => new Point((ActualWidth / 2 - ContentOffset.X) / ContentScale,
           (ActualHeight / 2 - ContentOffset.Y) / ContentScale);

        private static void ContentScaleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var scale = (double)e.NewValue;

            var me = (DrawingCanvas)d;
            PresentationSource source = PresentationSource.FromVisual(me);

            double dpiZoom = source?.CompositionTarget?.TransformToDevice.M11 ?? 1;
            double adjustment = 1 / dpiZoom; // undo the current dpi zoom so screenshots appear sharp

            me._scaleTransform2.ScaleX = scale * adjustment;
            me._scaleTransform2.ScaleY = scale * adjustment;
        }
        private static void ContentOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var me = (DrawingCanvas)d;
            var pt = (Point)e.NewValue;
            me._translateTransform.X = Math.Floor(pt.X);
            me._translateTransform.Y = Math.Floor(pt.Y);
        }
        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            ContentOffset = new Point(
                ContentOffset.X + sizeInfo.NewSize.Width / 2 - sizeInfo.PreviousSize.Width / 2,
                ContentOffset.Y + sizeInfo.NewSize.Height / 2 - sizeInfo.PreviousSize.Height / 2);
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

        public void ZoomPanFit(double? widthOverride = null)
        {
            var rect = GetArtworkBounds();
            ContentScale = Math.Min((widthOverride ?? ActualWidth) / rect.Width, ActualHeight / rect.Height);
            ZoomPanCenter(widthOverride);
        }

        public void ZoomPanActualSize(double? widthOverride = null)
        {
            var rect = GetArtworkBounds();
            ContentScale = 1;
            ZoomPanCenter(widthOverride);
        }

        public void ZoomPanCenter(double? widthOverride = null)
        {
            var rect = GetArtworkBounds();
            var x = (widthOverride ?? ActualWidth) / 2 - rect.Width * ContentScale / 2 - rect.Left * ContentScale;
            var y = ActualHeight / 2 - rect.Height * ContentScale / 2 - rect.Top * ContentScale;
            ContentOffset = new Point(x, y);
        }

        #endregion Zooming and Panning
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Serialization;
using DrawToolsLib.Graphics;

namespace DrawToolsLib
{
    /// <summary>
    /// Canvas used as host for DrawingVisual objects.
    /// Allows to draw graphics objects using mouse.
    /// </summary>
    public class DrawingCanvas : Canvas
    {
        #region Class Members

        // Collection contains instances of GraphicsBase-derived classes.
        private GraphicsVisualList graphicsList;

        // Dependency properties
        public static readonly DependencyProperty ToolProperty;

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

        private Tool[] tools;                   // Array of tools

        ToolText toolText;
        ToolPointer toolPointer;

        private ContextMenu contextMenu;

        private UndoManager undoManager;

        private const string clipboardFormat = "{65475a6c-9dde-41b1-946c-663ceb4d7b15}";


        #endregion Class Members

        #region Constructors

        public DrawingCanvas()
            : base()
        {
            graphicsList = new GraphicsVisualList(this);

            CreateContextMenu();

            // create array of drawing tools
            tools = new Tool[(int)ToolType.Max];

            toolPointer = new ToolPointer();
            tools[(int)ToolType.Pointer] = toolPointer;

            tools[(int)ToolType.Rectangle] = new ToolRectangle();
            tools[(int)ToolType.Ellipse] = new ToolEllipse();
            tools[(int)ToolType.Line] = new ToolLine();
            tools[(int)ToolType.Arrow] = new ToolArrow();
            tools[(int)ToolType.PolyLine] = new ToolPolyLine();

            toolText = new ToolText(this);
            tools[(int)ToolType.Text] = toolText;   // kept as class member for in-place editing

            // Create undo manager
            undoManager = new UndoManager(this);
            undoManager.StateChanged += new EventHandler(undoManager_StateChanged);


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
            _artworkRectangle.Background = new SolidColorBrush(Colors.White);
            Children.Add(_artworkRectangle);
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
                Properties.Settings.Default.DefaultFontFamily,
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

        #endregion Constructor

        #region Dependency Properties

        #region Tool

        /// <summary>
        /// Currently active drawing tool
        /// </summary>
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
                    var tmp = tools[(int)Tool];
                    if (tmp == null)
                        Cursor = Cursors.SizeAll;
                    else
                        tmp.SetCursor(this);
                }
            }
        }

        #endregion Tool

        #region CanUndo

        /// <summary>
        /// Return True if Undo operation is possible
        /// </summary>
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

        #endregion CanUndo

        #region CanRedo

        /// <summary>
        /// Return True if Redo operation is possible
        /// </summary>
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

        #endregion CanRedo

        #region CanSelectAll

        /// <summary>
        /// Return true if Select All function is available
        /// </summary>
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

        #endregion CanSelectAll

        #region CanUnselectAll

        /// <summary>
        /// Return true if Unselect All function is available
        /// </summary>
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

        #endregion CanUnselectAll

        #region CanDelete

        /// <summary>
        /// Return true if Delete function is available
        /// </summary>
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

        #endregion CanDelete

        #region CanDeleteAll

        /// <summary>
        /// Return true if Delete All function is available
        /// </summary>
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

        #endregion CanDeleteAll

        #region CanMoveToFront

        /// <summary>
        /// Return true if Move to Front function is available
        /// </summary>
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

        #endregion CanMoveToFront

        #region CanMoveToBack

        /// <summary>
        /// Return true if Move to Back function is available
        /// </summary>
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

        #endregion CanMoveToBack

        #region CanSetProperties

        /// <summary>
        /// Return true if currently active properties (line width, color etc.)
        /// can be applied to selected objects.
        /// </summary>
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

        #endregion CanSetProperties

        #region LineWidth

        /// <summary>
        /// Line width of new graphics object.
        /// Setting this property is also applied to current selection.
        /// </summary>
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

        /// <summary>
        /// Callback function called when LineWidth dependency property is changed
        /// </summary>
        static void LineWidthChanged(DependencyObject property, DependencyPropertyChangedEventArgs args)
        {
            DrawingCanvas d = property as DrawingCanvas;

            CommandChangeState command = new CommandChangeState(d);
            bool wasChange = false;

            foreach (GraphicBase g in d.Selection)
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

        #endregion LineWidth

        #region ObjectColor

        /// <summary>
        /// Color of new graphics object.
        /// Setting this property is also applied to current selection.
        /// </summary>
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

        /// <summary>
        /// Callback function called when ObjectColor dependency property is changed
        /// </summary>
        static void ObjectColorChanged(DependencyObject property, DependencyPropertyChangedEventArgs args)
        {
            DrawingCanvas d = property as DrawingCanvas;

            CommandChangeState command = new CommandChangeState(d);
            bool wasChange = false;
            var value = d.ObjectColor;

            foreach (GraphicBase g in d.Selection)
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

        #endregion ObjectColor

        #region HandleColor
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
        #endregion

        #region TextFontFamilyName

        /// <summary>
        /// Font Family name of new graphics object.
        /// Setting this property is also applied to current selection.
        /// </summary>
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

        /// <summary>
        /// Callback function called when TextFontFamilyName dependency property is changed
        /// </summary>
        static void TextFontFamilyNameChanged(DependencyObject property, DependencyPropertyChangedEventArgs args)
        {
            ApplyFontChange(property as DrawingCanvas, d => d.TextFontFamilyName, t => t.FontName, (t, v) => { t.FontName = v; });
        }

        private static void ApplyFontChange<TPropertyType>(DrawingCanvas d, Func<DrawingCanvas, TPropertyType> getProp, Func<GraphicText, TPropertyType> getTextProp, Action<GraphicText, TPropertyType> setTextProp)
        {
            CommandChangeState command = new CommandChangeState(d);
            bool wasChange = false;
            var value = getProp(d);

            foreach (GraphicBase g in d.Selection)
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

        #endregion TextFontFamilyName

        #region TextFontStyle

        /// <summary>
        /// Font style of new graphics object.
        /// Setting this property is also applied to current selection.
        /// </summary>
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

        /// <summary>
        /// Callback function called when TextFontStyle dependency property is changed
        /// </summary>
        static void TextFontStyleChanged(DependencyObject property, DependencyPropertyChangedEventArgs args)
        {
            ApplyFontChange(property as DrawingCanvas, d => d.TextFontStyle, t => t.FontStyle, (t, v) => { t.FontStyle = v; });
        }

        #endregion TextFontStyle

        #region TextFontWeight

        /// <summary>
        /// Font weight of new graphics object.
        /// Setting this property is also applied to current selection.
        /// </summary>
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

        /// <summary>
        /// Callback function called when TextFontWeight dependency property is changed
        /// </summary>
        static void TextFontWeightChanged(DependencyObject property, DependencyPropertyChangedEventArgs args)
        {
            ApplyFontChange(property as DrawingCanvas, d => d.TextFontWeight, t => t.FontWeight, (t, v) => { t.FontWeight = v; });
        }

        #endregion TextFontWeight

        #region TextFontStretch

        /// <summary>
        /// Font stretch of new graphics object.
        /// Setting this property is also applied to current selection.
        /// </summary>
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

        /// <summary>
        /// Callback function called when TextFontStretch dependency property is changed
        /// </summary>
        static void TextFontStretchChanged(DependencyObject property, DependencyPropertyChangedEventArgs args)
        {
            ApplyFontChange(property as DrawingCanvas, d => d.TextFontStretch, t => t.FontStretch, (t, v) => { t.FontStretch = v; });
        }

        #endregion TextFontStretch

        #region TextFontSize

        /// <summary>
        /// Font size of new graphics object.
        /// Setting this property is also applied to current selection.
        /// </summary>
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

        /// <summary>
        /// Callback function called when TextFontSize dependency property is changed
        /// </summary>
        static void TextFontSizeChanged(DependencyObject property, DependencyPropertyChangedEventArgs args)
        {
            ApplyFontChange(property as DrawingCanvas, d => d.TextFontSize, t => t.FontSize, (t, v) => { t.FontSize = v; });
        }

        #endregion TextFontSize

        #endregion Dependency Properties

        #region Public Functions

        /// <summary>
        /// Draw all graphics objects to DrawingContext supplied by client.
        /// Can be used for printing or saving image together with graphics
        /// as single bitmap.
        /// 
        /// Selection tracker is not drawn.
        /// </summary>
        public void Draw(DrawingContext drawingContext)
        {
            Draw(drawingContext, false);
        }

        /// <summary>
        /// Draw all graphics objects to DrawingContext supplied by client.
        /// Can be used for printing or saving image together with graphics
        /// as single bitmap.
        /// 
        /// withSelection = true - draw selected objects with tracker.
        /// </summary>
        public void Draw(DrawingContext drawingContext, bool withSelection)
        {
            bool oldSelection = false;

            foreach (GraphicBase b in graphicsList)
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
                    b.Draw(cx);
                }
                drawingContext.DrawRectangle(new VisualBrush(vis), null, vis.ContentBounds);

                if (!withSelection)
                {
                    // Restore selection state
                    b.IsSelected = oldSelection;
                }
            }
        }

        /// <summary>
        /// Add a graphic object to the canvas from its serialized companion class.
        /// </summary>
        public void AddGraphic(GraphicBase g)
        {
            this.UnselectAll();
            g.IsSelected = true;
            this.GraphicsList.Add(g);
            AddCommandToHistory(new CommandAdd(g));
        }

        /// <summary>
        /// Gets a bounding rectangle of all of the current graphics objects (selection handles not included)
        /// </summary>
        /// <returns></returns>
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

        /// <summary>
        /// Clear graphics list
        /// </summary>
        public void Clear()
        {
            graphicsList.Clear();
            ClearHistory();
            UpdateState();
        }

        /// <summary>
        /// Save graphics to XML file.
        /// Throws: DrawingCanvasException.
        /// </summary>
        public void Save(string fileName)
        {
            try
            {
                var helper = new SerializationHelper(graphicsList.OfType<GraphicBase>().Select(g => g), GetArtworkBounds());
                XmlSerializer xml = new XmlSerializer(typeof(SerializationHelper));

                using (Stream stream = new FileStream(fileName,
                    FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    xml.Serialize(stream, helper);
                    ClearHistory();
                    UpdateState();
                }
            }
            catch (IOException e)
            {
                throw new DrawingCanvasException(e.Message, e);
            }
            catch (InvalidOperationException e)
            {
                throw new DrawingCanvasException(e.Message, e);
            }
        }

        /// <summary>
        /// Load graphics from XML file.
        /// Throws: DrawingCanvasException.
        /// </summary>
        public void Load(string fileName)
        {
            try
            {
                SerializationHelper helper;

                XmlSerializer xml = new XmlSerializer(typeof(SerializationHelper));

                using (Stream stream = new FileStream(fileName,
                    FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    helper = (SerializationHelper)xml.Deserialize(stream);
                }

                if (helper.Graphics == null)
                {
                    throw new DrawingCanvasException(Properties.Settings.Default.NoInfoInXMLFile);
                }

                graphicsList.Clear();

                foreach (var g in helper.Graphics)
                {
                    graphicsList.Add(g);
                }

                ClearHistory();
                UpdateState();
            }
            catch (IOException e)
            {
                throw new DrawingCanvasException(e.Message, e);
            }
            catch (InvalidOperationException e)
            {
                throw new DrawingCanvasException(e.Message, e);
            }
            catch (ArgumentNullException e)
            {
                throw new DrawingCanvasException(e.Message, e);
            }
        }

        /// <summary>
        /// Copies the current selected graphic objects to the clipboard
        /// </summary>
        public void Copy()
        {
            Clipboard.SetDataObject(GetClipboardObject(), false);
            UpdateState();
        }

        /// <summary>
        /// Generates a clipboard DataObject that can be added to before pushing to the clipboard.
        /// </summary>
        /// <returns></returns>
        public DataObject GetClipboardObject()
        {
            Clipboard.Clear();
            GraphicBase[] graphics = graphicsList.OfType<GraphicBase>().Where(g => g.IsSelected).ToArray();
            if (!graphics.Any())
                graphics = graphicsList.OfType<GraphicBase>().ToArray();
            var helper = new SerializationHelper(graphics.Select(g => g), GetArtworkBounds(true));

            XmlSerializer xml = new XmlSerializer(typeof(SerializationHelper));
            using (MemoryStream stream = new MemoryStream())
            {
                xml.Serialize(stream, helper);
                var format = DataFormats.GetDataFormat(clipboardFormat);
                DataObject dataObj = new DataObject();
                dataObj.SetData(format.Name, Convert.ToBase64String(stream.ToArray()), true);
                return dataObj;
            }
        }

        /// <summary>
        /// Paste any graphics objects in the clipboard to the drawing canvas
        /// </summary>
        /// <returns>True if there were graphics to paste, or False otherwise</returns>
        public bool Paste()
        {
            var dataObj = Clipboard.GetDataObject();
            if (dataObj?.GetDataPresent(clipboardFormat) == true)
            {
                var base64 = dataObj.GetData(clipboardFormat) as string;
                if (String.IsNullOrEmpty(base64))
                    return false;

                SerializationHelper helper;
                XmlSerializer xml = new XmlSerializer(typeof(SerializationHelper));

                using (var stream = new MemoryStream(Convert.FromBase64String(base64)))
                {
                    helper = (SerializationHelper)xml.Deserialize(stream);
                }

                var transformX = (-helper.Left - helper.Width / 2) + ((ActualWidth / 2 - ContentOffset.X) / ContentScale);
                var transformY = (-helper.Top - helper.Height / 2) + ((ActualHeight / 2 - ContentOffset.Y) / ContentScale);

                this.UnselectAll();
                foreach (var g in helper.Graphics)
                {
                    g.Move(transformX, transformY);
                    g.IsSelected = true;
                    graphicsList.Add(g);
                }
                AddCommandToHistory(new CommandAdd(helper.Graphics));
                UpdateState();
                InvalidateVisual();
                RefreshBounds();
                return true;
            }
            return false;
        }

        /// <summary>
        /// Select all
        /// </summary>
        public void SelectAll()
        {
            for (int i = 0; i < this.Count; i++)
            {
                this[i].IsSelected = true;
            }
            UpdateState();
        }

        /// <summary>
        /// Unselect all
        /// </summary>
        public void UnselectAll()
        {
            for (int i = 0; i < this.Count; i++)
            {
                this[i].IsSelected = false;
            }
            UpdateState();
        }

        /// <summary>
        /// Delete selection
        /// </summary>
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

        /// <summary>
        /// Delete all
        /// </summary>
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

        /// <summary>
        /// Move selection to the front of Z-order
        /// </summary>
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

        /// <summary>
        /// Move selection to the end of Z-order
        /// </summary>
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

        /// <summary>
        /// Reset angle of rotation to 0 for all selected elements
        /// </summary>
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

        /// <summary>
        /// Apply currently active properties to selected objects
        /// </summary>
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


        /// <summary>
        /// Undo
        /// </summary>
        public void Undo()
        {
            undoManager.Undo();
            UpdateState();
            RefreshBounds();
        }

        /// <summary>
        /// Redo
        /// </summary>
        public void Redo()
        {
            undoManager.Redo();
            UpdateState();
            RefreshBounds();
        }

        #endregion Public Functions

        #region Internal Properties

        /// <summary>
        /// Get graphic object by index
        /// </summary>
        internal GraphicBase this[int index]
        {
            get
            {
                if (index >= 0 && index < Count)
                {
                    return (GraphicBase)graphicsList[index];
                }

                return null;
            }
        }

        /// <summary>
        /// Get number of graphic objects
        /// </summary>
        internal int Count
        {
            get
            {
                return graphicsList.Count;
            }
        }

        /// <summary>
        /// Get number of selected graphic objects
        /// </summary>
		internal int SelectionCount
        {
            get
            {
                int n = 0;

                foreach (GraphicBase g in this.GraphicsList)
                {
                    if (g.IsSelected)
                    {
                        n++;
                    }
                }

                return n;
            }
        }

        /// <summary>
        /// Return list of graphics
        /// </summary>
        internal GraphicsVisualList GraphicsList => graphicsList;

        /// <summary>
        /// Returns INumerable which may be used for enumeration
        /// of selected objects.
        /// </summary>
        internal IEnumerable<GraphicBase> Selection
        {
            get
            {
                foreach (GraphicBase o in graphicsList)
                {
                    if (o.IsSelected)
                    {
                        yield return o;
                    }
                }
            }

        }

        #endregion Internal Properties

        #region Visual Children Overrides

        private Border _clickable;
        private Border _artworkRectangle;
        private const int _extraVisualsCount = 2; // only includes the permanent extra visuals

        protected override int VisualChildrenCount
        {
            get
            {
                int n = _extraVisualsCount + graphicsList.Count;

                if (toolText.TextBox != null)
                {
                    n++;
                }

                return n;
            }
        }

        protected override Visual GetVisualChild(int index)
        {
            if (index == 0)
            {
                if (_clickable != null && ActualWidth > 0 && _scaleTransform.ScaleX > 0)
                {
                    Canvas.SetLeft(_clickable, -_translateTransform.X / _scaleTransform.ScaleX);
                    Canvas.SetTop(_clickable, -_translateTransform.Y / _scaleTransform.ScaleY);
                    _clickable.Width = ActualWidth / _scaleTransform.ScaleX;
                    _clickable.Height = ActualHeight / _scaleTransform.ScaleY;
                }
                return _clickable;
            }
            else if (index == 1)
            {
                return _artworkRectangle;
            }
            else if (index - _extraVisualsCount < graphicsList.Count)
            {
                return graphicsList.GetItemVisual(index - _extraVisualsCount);
            }
            else if (index == _extraVisualsCount + graphicsList.Count && toolText.TextBox != null)
            {
                return toolText.TextBox;
            }

            throw new ArgumentOutOfRangeException("index");
        }

        #endregion Visual Children Overrides

        #region Mouse Event Handlers

        /// <summary>
        /// Mouse down.
        /// Left button down event is passed to active tool.
        /// Right button down event is handled in this class.
        /// </summary>
        void DrawingCanvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (IsPanning)
                return;

            this.Focus();

            if (e.ChangedButton == MouseButton.Left)
            {
                if (e.ClickCount == 2)
                {
                    HandleDoubleClick(e);        // special case for GraphicsText
                }
                else if (Tool == ToolType.None || tools[(int)Tool] == null)
                {
                    StartPanning(e);
                }
                else
                {
                    tools[(int)Tool].OnMouseDown(this, e);
                }

                UpdateState();
            }
            else if (e.ChangedButton == MouseButton.Right)
            {
                ShowContextMenu(e);
            }
        }

        /// <summary>
        /// Mouse move.
        /// Moving without button pressed or with left button pressed
        /// is passed to active tool.
        /// </summary>
        void DrawingCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (IsPanning)
            {
                ContinuePanning(e);
                return;
            }

            if (tools[(int)Tool] == null)
            {
                return;
            }

            if (e.MiddleButton == MouseButtonState.Released && e.RightButton == MouseButtonState.Released)
            {
                tools[(int)Tool].OnMouseMove(this, e);

                UpdateState();
            }
            else
            {
                this.Cursor = HelperFunctions.DefaultCursor;
            }

            if (e.LeftButton == MouseButtonState.Pressed)
                RefreshBounds();
        }

        /// <summary>
        /// Mouse up event.
        /// Left button up event is passed to active tool.
        /// </summary>
        void DrawingCanvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (IsPanning)
            {
                StopPanning(e);
                return;
            }

            if (tools[(int)Tool] == null)
            {
                return;
            }

            if (e.ChangedButton == MouseButton.Left)
            {
                tools[(int)Tool].OnMouseUp(this, e);

                UpdateState();
            }
        }

        /// <summary>
        /// Mouse wheel event.
        /// Change zoom, except if panning.
        /// </summary>
        void DrawingCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (IsPanning)
                return;

            double zoom = e.Delta > 0 ? .2 : -.2;
            if (!(e.Delta > 0) && (_scaleTransform.ScaleX < .3 || _scaleTransform.ScaleY < .3))
                return;
            if (e.Delta > 0 && (_scaleTransform.ScaleX > 2.9 || _scaleTransform.ScaleY > 2.9))
                return;

            Point relative = e.GetPosition(this);
            double absoluteX;
            double absoluteY;

            absoluteX = relative.X * _scaleTransform.ScaleX + _translateTransform.X;
            absoluteY = relative.Y * _scaleTransform.ScaleY + _translateTransform.Y;

            ContentScale += zoom;
            ContentOffset = new Point(absoluteX - relative.X * _scaleTransform.ScaleX, absoluteY - relative.Y * _scaleTransform.ScaleY);
        }

        #endregion Mouse Event Handlers

        #region Other Event Handlers

        /// <summary>
        /// Initialization after control is loaded
        /// </summary>
        void DrawingCanvas_Loaded(object sender, RoutedEventArgs e)
        {
            this.Focusable = true;      // to handle keyboard messages
        }

        /// <summary>
        /// Context menu item is clicked
        /// </summary>
        void contextMenuItem_Click(object sender, RoutedEventArgs e)
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


        /// <summary>
        /// Mouse capture is lost
        /// </summary>
        void DrawingCanvas_LostMouseCapture(object sender, MouseEventArgs e)
        {
            if (this.IsMouseCaptured)
            {
                CancelCurrentOperation();
                UpdateState();
            }
        }

        /// <summary>
        /// Handle keyboard input
        /// </summary>
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

        /// <summary>
        /// UndoManager state is changed.
        /// Refresh CanUndo, CanRedo and IsDirty properties.
        /// </summary>
        void undoManager_StateChanged(object sender, EventArgs e)
        {
            this.CanUndo = undoManager.CanUndo;
            this.CanRedo = undoManager.CanRedo;
        }


        #endregion Other Event Handlers

        #region Other Functions

        /// <summary>
        /// Create context menu
        /// </summary>
        void CreateContextMenu()
        {
            MenuItem menuItem;

            contextMenu = new ContextMenu();

            menuItem = new MenuItem();
            menuItem.Header = "Select all";
            menuItem.Tag = ContextMenuCommand.SelectAll;
            menuItem.Click += new RoutedEventHandler(contextMenuItem_Click);
            contextMenu.Items.Add(menuItem);

            menuItem = new MenuItem();
            menuItem.Header = "Unselect all";
            menuItem.Tag = ContextMenuCommand.UnselectAll;
            menuItem.Click += new RoutedEventHandler(contextMenuItem_Click);
            contextMenu.Items.Add(menuItem);

            menuItem = new MenuItem();
            menuItem.Header = "Delete";
            menuItem.Tag = ContextMenuCommand.Delete;
            menuItem.Click += new RoutedEventHandler(contextMenuItem_Click);
            contextMenu.Items.Add(menuItem);

            menuItem = new MenuItem();
            menuItem.Header = "Delete all";
            menuItem.Tag = ContextMenuCommand.DeleteAll;
            menuItem.Click += new RoutedEventHandler(contextMenuItem_Click);
            contextMenu.Items.Add(menuItem);

            contextMenu.Items.Add(new Separator());

            menuItem = new MenuItem();
            menuItem.Header = "Move to front";
            menuItem.Tag = ContextMenuCommand.MoveToFront;
            menuItem.Click += new RoutedEventHandler(contextMenuItem_Click);
            contextMenu.Items.Add(menuItem);

            menuItem = new MenuItem();
            menuItem.Header = "Move to back";
            menuItem.Tag = ContextMenuCommand.MoveToBack;
            menuItem.Click += new RoutedEventHandler(contextMenuItem_Click);
            contextMenu.Items.Add(menuItem);

            contextMenu.Items.Add(new Separator());

            menuItem = new MenuItem();
            menuItem.Header = "Undo";
            menuItem.Tag = ContextMenuCommand.Undo;
            menuItem.Click += new RoutedEventHandler(contextMenuItem_Click);
            contextMenu.Items.Add(menuItem);

            menuItem = new MenuItem();
            menuItem.Header = "Redo";
            menuItem.Tag = ContextMenuCommand.Redo;
            menuItem.Click += new RoutedEventHandler(contextMenuItem_Click);
            contextMenu.Items.Add(menuItem);

            menuItem = new MenuItem();
            menuItem.Header = "Set properties";
            menuItem.Tag = ContextMenuCommand.SetProperties;
            menuItem.Click += new RoutedEventHandler(contextMenuItem_Click);
            contextMenu.Items.Add(menuItem);

            menuItem = new MenuItem();
            menuItem.Header = "Reset rotation";
            menuItem.Tag = ContextMenuCommand.ResetRotation;
            menuItem.Click += new RoutedEventHandler(contextMenuItem_Click);
            contextMenu.Items.Add(menuItem);
        }

        /// <summary>
        /// Show context menu.
        /// </summary>
        void ShowContextMenu(MouseButtonEventArgs e)
        {
            // Change current selection if necessary

            Point point = e.GetPosition(this);

            GraphicBase o = null;

            for (int i = graphicsList.Count - 1; i >= 0; i--)
            {
                if (((GraphicBase)graphicsList[i]).MakeHitTest(point) == 0)
                {
                    o = (GraphicBase)graphicsList[i];
                    break;
                }
            }

            if (o != null)
            {
                if (!o.IsSelected)
                {
                    UnselectAll();
                }

                // Select clicked object
                o.IsSelected = true;
            }
            else
            {
                UnselectAll();
            }

            UpdateState();

            MenuItem item;

            /// Enable/disable menu items.
            foreach (object obj in contextMenu.Items)
            {
                item = obj as MenuItem;

                if (item != null)
                {
                    ContextMenuCommand command = (ContextMenuCommand)item.Tag;

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

            contextMenu.IsOpen = true;
        }

        /// <summary>
        /// Cancel currently executed operation:
        /// add new object or group selection.
        /// 
        /// Called when mouse capture is lost or Esc is pressed.
        /// </summary>
        void CancelCurrentOperation()
        {
            if (Tool == ToolType.Pointer)
            {
                if (graphicsList.Count > 0)
                {
                    if (graphicsList[graphicsList.Count - 1] is GraphicSelectionRectangle)
                    {
                        // Delete selection rectangle if it exists
                        graphicsList.RemoveAt(graphicsList.Count - 1);
                    }
                    else
                    {
                        // Pointer tool moved or resized graphics object.
                        // Add this action to the history
                        toolPointer.AddChangeToHistory(this);
                    }
                }
            }
            else if (Tool > ToolType.Pointer && Tool < ToolType.Max)
            {
                // Delete last graphics object which is currently drawn
                if (graphicsList.Count > 0)
                {
                    graphicsList.RemoveAt(graphicsList.Count - 1);
                }
            }

            Tool = ToolType.Pointer;

            this.ReleaseMouseCapture();
            this.Cursor = HelperFunctions.DefaultCursor;
        }

        /// <summary>
        /// Hide in-place editing textbox.
        /// Called from TextTool, when user pressed Enter or Esc,
        /// or from this class, when user clicks on the canvas.
        /// 
        /// graphicsText passed to this function can be new text added by
        /// ToolText, or existing text opened for editing.
        /// If ToolText.OldText is empty, this is new object.
        /// If not, this is existing object.
        /// </summary>
        internal void HideTextbox(GraphicBase graphic)
        {
            if (toolText.TextBox == null)
            {
                return;
            }
            var graphicsText = graphic as GraphicText;
            if (graphicsText == null)
                return;

            graphicsText.IsSelected = true;   // restore selection which was removed for better textbox appearance
            graphicsText.Editing = false;

            if (toolText.TextBox.Text.Trim().Length == 0)
            {
                // Textbox is empty: remove text object.

                if (!String.IsNullOrEmpty(toolText.OldText))  // existing text was edited
                {
                    // Since text object is removed now,
                    // Add Delete command to the history
                    undoManager.AddCommandToHistory(new CommandDelete(this));
                }

                // Remove empty text object
                graphicsList.Remove(graphic);
            }
            else
            {
                if (!String.IsNullOrEmpty(toolText.OldText))  // existing text was edited
                {
                    if (toolText.TextBox.Text.Trim() != toolText.OldText)     // text was really changed
                    {
                        // Create command
                        CommandChangeState command = new CommandChangeState(this);

                        // Make change in the text object
                        graphicsText.Body = toolText.TextBox.Text.Trim();

                        // Keep state after change and add command to the history
                        command.NewState(this);
                        undoManager.AddCommandToHistory(command);
                    }
                }
                else                                          // new text was added
                {
                    // Make change in the text object
                    graphicsText.Body = toolText.TextBox.Text.Trim();

                    // Add command to the history
                    undoManager.AddCommandToHistory(new CommandAdd(graphic));
                }
            }

            // Remove textbox and set it to null.
            this.Children.Remove(toolText.TextBox);
            toolText.TextBox = null;

            // This enables back all ApplicationCommands,
            // which are disabled while textbox is active.
            this.Focus();
        }

        /// <summary>
        /// Open in-place edit box if GraphicsText is clicked
        /// </summary>
        void HandleDoubleClick(MouseButtonEventArgs e)
        {
            Point point = e.GetPosition(this);

            // Enumerate all text objects
            for (int i = graphicsList.Count - 1; i >= 0; i--)
            {
                GraphicText t = graphicsList[i] as GraphicText;
                if (t != null && t.Contains(point))
                {
                    toolText.CreateTextBox(t, this);
                    return;
                }
            }
        }

        /// <summary>
        /// Add command to history.
        /// </summary>
        internal void AddCommandToHistory(CommandBase command)
        {
            undoManager.AddCommandToHistory(command);
        }

        /// <summary>
        /// Clear Undo history.
        /// </summary>
        void ClearHistory()
        {
            undoManager.ClearHistory();
        }

        /// <summary>
        /// Update state of Can* dependency properties
        /// used for Edit commands.
        /// This function calls after any change in drawing canvas state,
        /// caused by user commands.
        /// Helps to keep client controls state up-to-date, in the case
        /// if Can* properties are used for binding.
        /// </summary>
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

        private void RefreshBounds()
        {
            var bounds = GetArtworkBounds();
            Canvas.SetLeft(_artworkRectangle, bounds.Left);
            Canvas.SetTop(_artworkRectangle, bounds.Top);
            _artworkRectangle.Width = bounds.Width;
            _artworkRectangle.Height = bounds.Height;
        }

        #endregion Other Functions

        #region Zooming and panning

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
            var me = (DrawingCanvas)d;
            var scale = (double)e.NewValue;
            me._scaleTransform.ScaleX = scale;
            me._scaleTransform.ScaleY = scale;

            var worldCX = (me.ActualWidth / 2 - me.ContentOffset.X) / (double)e.OldValue;
            var worldCY = (me.ActualHeight / 2 - me.ContentOffset.Y) / (double)e.OldValue;
            me.ContentOffset = new Point(me.ActualWidth / 2 - worldCX * (double)e.NewValue, me.ActualHeight / 2 - worldCY * (double)e.NewValue);
        }
        private static void ContentOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var me = (DrawingCanvas)d;
            var pt = (Point)e.NewValue;
            if (me.ContentScale == 1)
            {
                me._translateTransform.X = Math.Round(pt.X);
                me._translateTransform.Y = Math.Round(pt.Y);
            }
            else
            {
                me._translateTransform.X = pt.X;
                me._translateTransform.Y = pt.Y;
            }
        }
        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            ContentOffset = new Point(
                ContentOffset.X + sizeInfo.NewSize.Width / 2 - sizeInfo.PreviousSize.Width / 2,
                ContentOffset.Y + sizeInfo.NewSize.Height / 2 - sizeInfo.PreviousSize.Height / 2);
        }

        private ScaleTransform _scaleTransform;
        private TranslateTransform _translateTransform;

        private void InitializeZoom()
        {
            TransformGroup group = new TransformGroup();
            _scaleTransform = new ScaleTransform();
            group.Children.Add(_scaleTransform);
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
            var rect = GetArtworkBounds();
            ContentScale = Math.Min(ActualWidth / rect.Width, ActualHeight / rect.Height);
            ZoomPanCenter();
        }

        public void ZoomPanActualSize()
        {
            var rect = GetArtworkBounds();
            ContentScale = 1;
            ZoomPanCenter();
        }

        public void ZoomPanCenter()
        {
            var rect = GetArtworkBounds();
            var x = ActualWidth / 2 - rect.Width * ContentScale / 2 - rect.Left * ContentScale;
            var y = ActualHeight / 2 - rect.Height * ContentScale / 2 - rect.Top * ContentScale;
            ContentOffset = new Point(x, y);
        }

        #endregion
    }
}

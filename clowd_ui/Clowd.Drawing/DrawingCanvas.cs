using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Clowd.Drawing.Graphics;
using Clowd.Drawing.Tools;

namespace Clowd.Drawing
{
    /// <summary>
    /// Avalonia control that hosts a <see cref="GraphicCollection"/> and renders
    /// every graphic in <see cref="Render(DrawingContext)"/>. In the WPF
    /// original this inherited <c>System.Windows.Controls.Canvas</c> and owned
    /// a <c>VisualCollection</c> of per-graphic <c>DrawingVisual</c>s; the
    /// Avalonia version draws everything in a single render override and
    /// invalidates whenever the collection raises <see cref="GraphicCollection.Changed"/>.
    ///
    /// We inherit from <see cref="Control"/> (not <see cref="Panel"/>) because
    /// <c>Panel.Render</c> is sealed in Avalonia and we need a custom draw
    /// pipeline. Overlay UI like the in-place text-edit <see cref="TextBox"/>
    /// is hosted by the outer shell via the <see cref="TextEditRequested"/>
    /// event.
    /// </summary>
    public class DrawingCanvas : Control
    {
        public static readonly StyledProperty<Color> ArtworkBackgroundProperty =
            AvaloniaProperty.Register<DrawingCanvas, Color>(nameof(ArtworkBackground), Colors.Transparent);

        public static readonly StyledProperty<GraphicCollection> GraphicsListProperty =
            AvaloniaProperty.Register<DrawingCanvas, GraphicCollection>(nameof(GraphicsList));

        public static readonly StyledProperty<ToolType> ToolProperty =
            AvaloniaProperty.Register<DrawingCanvas, ToolType>(nameof(Tool), ToolType.Pointer);

        public static readonly StyledProperty<Color> ObjectColorProperty =
            AvaloniaProperty.Register<DrawingCanvas, Color>(nameof(ObjectColor), Colors.Red);

        public static readonly StyledProperty<double> LineWidthProperty =
            AvaloniaProperty.Register<DrawingCanvas, double>(nameof(LineWidth), 2d);

        public static readonly StyledProperty<double> ObjectAngleProperty =
            AvaloniaProperty.Register<DrawingCanvas, double>(nameof(ObjectAngle), 0d);

        public static readonly StyledProperty<double> BlurRadiusProperty =
            AvaloniaProperty.Register<DrawingCanvas, double>(nameof(BlurRadius), 8d);

        public static readonly StyledProperty<string> TextFontFamilyNameProperty =
            AvaloniaProperty.Register<DrawingCanvas, string>(nameof(TextFontFamilyName), "Segoe UI");

        public static readonly StyledProperty<double> TextFontSizeProperty =
            AvaloniaProperty.Register<DrawingCanvas, double>(nameof(TextFontSize), 18d);

        public static readonly StyledProperty<FontStyle> TextFontStyleProperty =
            AvaloniaProperty.Register<DrawingCanvas, FontStyle>(nameof(TextFontStyle), FontStyle.Normal);

        public static readonly StyledProperty<FontWeight> TextFontWeightProperty =
            AvaloniaProperty.Register<DrawingCanvas, FontWeight>(nameof(TextFontWeight), FontWeight.Normal);

        public static readonly StyledProperty<FontStretch> TextFontStretchProperty =
            AvaloniaProperty.Register<DrawingCanvas, FontStretch>(nameof(TextFontStretch), FontStretch.Normal);

        public static readonly StyledProperty<Skill> SubjectSkillProperty =
            AvaloniaProperty.Register<DrawingCanvas, Skill>(nameof(SubjectSkill), Skill.None);

        public static readonly StyledProperty<string> SubjectNameProperty =
            AvaloniaProperty.Register<DrawingCanvas, string>(nameof(SubjectName), string.Empty);

        static DrawingCanvas()
        {
            AffectsRender<DrawingCanvas>(ArtworkBackgroundProperty);
            AffectsRender<DrawingCanvas>(GraphicsListProperty);
            GraphicsListProperty.Changed.AddClassHandler<DrawingCanvas>((c, e) => c.OnGraphicsListChanged(e));
            ToolProperty.Changed.AddClassHandler<DrawingCanvas>((c, e) => c.OnToolChanged());

            // When the user edits the toolbar property fields, push to the selection.
            ObjectColorProperty.Changed.AddClassHandler<DrawingCanvas>((c, _) => c.PushSubjectFromCanvas(syncColor: true));
            LineWidthProperty.Changed.AddClassHandler<DrawingCanvas>((c, _) => c.PushSubjectFromCanvas(syncStroke: true));
            ObjectAngleProperty.Changed.AddClassHandler<DrawingCanvas>((c, _) => c.PushSubjectFromCanvas(syncAngle: true));
        }

        public DrawingCanvas()
        {
            // A fresh collection by default so consumers don't have to assign one.
            GraphicsList = new GraphicCollection();
            Background = BuildCheckeredBackgroundBrush();
            // Keep the tiled checkerboard pattern crisp; without this Avalonia
            // bilinearly samples the 50×50 source bitmap on hi-DPI displays
            // and the dot edges turn into a uniform grey wash.
            RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
            _undoManager = new UndoManager(this);

            // Register the built-in tools with name + skill metadata.
            _toolPointer = new ToolPointer();
            var crossCursor = (Func<Cursor>)(() => new Cursor(StandardCursorType.Cross));

            _toolStore[ToolType.Pointer] = new ToolDesc("Pointer", _toolPointer, DefaultSkills: Skill.CanvasBackground);
            _toolStore[ToolType.None]    = new ToolDesc("Pan", new ToolPanning(), DefaultSkills: Skill.CanvasBackground);

            _toolStore[ToolType.Rectangle] = new ToolDesc("Rectangle",
                new ToolDraggable<GraphicRectangle>(
                    crossCursor,
                    point => new GraphicRectangle(ObjectColor, LineWidth, new Rect(point, new Size(1, 1))),
                    (point, g) => g.MoveHandleTo(point, 5),
                    snapMode: SnapMode.Diagonal),
                GraphicType: typeof(GraphicRectangle));

            _toolStore[ToolType.FilledRectangle] = new ToolDesc("Filled Rectangle",
                new ToolDraggable<GraphicFilledRectangle>(
                    crossCursor,
                    point => new GraphicFilledRectangle(ObjectColor, new Rect(point, new Size(1, 1))),
                    (point, g) => g.MoveHandleTo(point, 5),
                    snapMode: SnapMode.Diagonal),
                GraphicType: typeof(GraphicFilledRectangle));

            _toolStore[ToolType.Ellipse] = new ToolDesc("Ellipse",
                new ToolDraggable<GraphicEllipse>(
                    crossCursor,
                    point => new GraphicEllipse(ObjectColor, LineWidth, new Rect(point, new Size(1, 1))),
                    (point, g) => g.MoveHandleTo(point, 5),
                    snapMode: SnapMode.Diagonal),
                GraphicType: typeof(GraphicEllipse));

            _toolStore[ToolType.Line] = new ToolDesc("Line",
                new ToolDraggable<GraphicLine>(
                    crossCursor,
                    point => new GraphicLine(ObjectColor, LineWidth, point, point),
                    (point, g) => g.MoveHandleTo(point, 2),
                    snapMode: SnapMode.All),
                GraphicType: typeof(GraphicLine));

            _toolStore[ToolType.Arrow] = new ToolDesc("Arrow",
                new ToolDraggable<GraphicArrow>(
                    crossCursor,
                    point => new GraphicArrow(ObjectColor, LineWidth, point, point),
                    (point, g) => g.MoveHandleTo(point, 2),
                    snapMode: SnapMode.All),
                GraphicType: typeof(GraphicArrow));

            _toolStore[ToolType.PolyLine] = new ToolDesc("Pencil",
                new ToolPolyLine(),
                GraphicType: typeof(GraphicPolyLine));

            _toolStore[ToolType.Text] = new ToolDesc("Text",
                new ToolDraggable<GraphicText>(
                    () => new Cursor(StandardCursorType.Ibeam),
                    point => CreateTextGraphic(point),
                    (point, g) => g.MoveHandleTo(point, 5)),
                GraphicType: typeof(GraphicText));

            _toolStore[ToolType.Count] = new ToolDesc("Numeric Step",
                new ToolDraggable<GraphicCount>(
                    crossCursor,
                    point => CreateCountGraphic(point),
                    (point, g) => g.MoveHandleTo(point, 5),
                    snapMode: SnapMode.Diagonal),
                GraphicType: typeof(GraphicCount));

            _toolStore[ToolType.Pixelate] = new ToolDesc("Pixelate",
                new ToolPixelate(),
                DefaultSkills: Skill.BlurRadius);

            Focusable = true;

            // Drag-drop image import (Avalonia 12 API).
            DragDrop.SetAllowDrop(this, true);
            DragDrop.AddDragOverHandler(this, OnDragOver);
            DragDrop.AddDropHandler(this, OnDrop);

            BuildContextMenu();
            RecomputeSubject();
        }

        private void BuildContextMenu()
        {
            var menu = new ContextMenu();

            void Add(string header, Action exec) =>
                menu.Items.Add(new MenuItem
                {
                    Header = header,
                    Command = new RelayCommand(_ => exec()),
                });

            Add("Select all", SelectAll);
            Add("Delete", DeleteSelected);
            Add("Delete all", DeleteAll);
            menu.Items.Add(new Separator());
            Add("Move to front", MoveSelectionToFront);
            Add("Move forward", MoveSelectionForward);
            Add("Move backward", MoveSelectionBackward);
            Add("Move to back", MoveSelectionToBack);
            menu.Items.Add(new Separator());
            Add("Reset rotation", ResetSelectionRotation);
            menu.Items.Add(new Separator());
            Add("Reset zoom", ResetViewport);

            ContextMenu = menu;
        }

        /// <summary>
        /// Tiny ICommand wrapper local to <see cref="DrawingCanvas"/> so the
        /// drawing library doesn't need to depend on the host app's
        /// RelayCommand. Always reports CanExecute=true; the menu item is
        /// only shown when the user right-clicks anyway.
        /// </summary>
        private sealed class RelayCommand : System.Windows.Input.ICommand
        {
            private readonly Action<object?> _exec;
            public RelayCommand(Action<object?> exec) { _exec = exec; }
            public bool CanExecute(object? parameter) => true;
            public void Execute(object? parameter) => _exec(parameter);
            public event EventHandler? CanExecuteChanged { add { } remove { } }
        }

        private void OnDragOver(object? sender, DragEventArgs e)
        {
            // Accept drops only when the payload contains files.
            if (e.DataTransfer.Formats.Contains(DataFormat.File))
                e.DragEffects = DragDropEffects.Copy;
            else
                e.DragEffects = DragDropEffects.None;
        }

        private static readonly string[] _imageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tiff", ".tif" };

        private void OnDrop(object? sender, DragEventArgs e)
        {
            if (!e.DataTransfer.Formats.Contains(DataFormat.File)) return;

            var files = e.DataTransfer.TryGetFiles();
            if (files == null) return;

            var dropPoint = ToContentPoint(e.GetPosition(this));
            int added = 0;
            foreach (var item in files)
            {
                var path = item.Path?.LocalPath;
                if (string.IsNullOrEmpty(path)) continue;

                var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
                if (Array.IndexOf(_imageExtensions, ext) < 0) continue;

                // Stagger multiple drops slightly so they don't overlap exactly.
                var staggered = new Point(dropPoint.X + added * 24, dropPoint.Y + added * 24);
                var img = GraphicImage.CreateFromFile(path, staggered);
                GraphicsList.Add(img);
                added++;
            }

            if (added > 0)
            {
                AddCommandToHistory(false);
                e.Handled = true;
            }
        }

        private GraphicText CreateTextGraphic(Point point)
        {
            var color = ObjectColor.A == 0 ? GraphicText.NextDefaultColor() : ObjectColor;
            var text = new GraphicText(color, LineWidth, point, GraphicText.RandomTilt())
            {
                FontName = TextFontFamilyName,
                FontSize = TextFontSize,
                FontStyle = TextFontStyle,
                FontWeight = TextFontWeight,
                FontStretch = TextFontStretch,
            };
            return text;
        }

        private GraphicCount CreateCountGraphic(Point point)
        {
            // Pick the next number based on the current canvas contents.
            int next = 1;
            foreach (var g in GraphicsList)
            {
                if (g is GraphicCount c && int.TryParse(c.Body, out var n) && n >= next)
                    next = n + 1;
            }
            var count = new GraphicCount(ObjectColor, LineWidth, point, next.ToString())
            {
                FontName = TextFontFamilyName,
                FontSize = TextFontSize,
                FontStyle = TextFontStyle,
                FontWeight = TextFontWeight,
                FontStretch = TextFontStretch,
            };
            count.Normalize();
            return count;
        }

        public Color ArtworkBackground
        {
            get => GetValue(ArtworkBackgroundProperty);
            set => SetValue(ArtworkBackgroundProperty, value);
        }

        public GraphicCollection GraphicsList
        {
            get => GetValue(GraphicsListProperty);
            set => SetValue(GraphicsListProperty, value);
        }

        public ToolType Tool
        {
            get => GetValue(ToolProperty);
            set => SetValue(ToolProperty, value);
        }

        public Color ObjectColor
        {
            get => GetValue(ObjectColorProperty);
            set => SetValue(ObjectColorProperty, value);
        }

        public double LineWidth
        {
            get => GetValue(LineWidthProperty);
            set => SetValue(LineWidthProperty, value);
        }

        public double ObjectAngle
        {
            get => GetValue(ObjectAngleProperty);
            set => SetValue(ObjectAngleProperty, value);
        }

        public double BlurRadius
        {
            get => GetValue(BlurRadiusProperty);
            set => SetValue(BlurRadiusProperty, value);
        }

        public string TextFontFamilyName
        {
            get => GetValue(TextFontFamilyNameProperty);
            set => SetValue(TextFontFamilyNameProperty, value);
        }

        public double TextFontSize
        {
            get => GetValue(TextFontSizeProperty);
            set => SetValue(TextFontSizeProperty, value);
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

        /// <summary>
        /// The "skill" flags applicable to the current selection / active tool.
        /// The top toolbar uses this to show or hide colour, stroke, font, blur,
        /// etc. property editors via a SkillFlagConverter.
        /// </summary>
        public Skill SubjectSkill
        {
            get => GetValue(SubjectSkillProperty);
            private set => SetValue(SubjectSkillProperty, value);
        }

        public string SubjectName
        {
            get => GetValue(SubjectNameProperty);
            private set => SetValue(SubjectNameProperty, value);
        }

        /// <summary>
        /// Background brush set explicitly so the control's bounds are hit-testable
        /// even when ArtworkBackground is transparent. Distinct from
        /// <see cref="ArtworkBackground"/>, which is the colour we paint *inside*
        /// the artwork rect.
        /// </summary>
        public IBrush? Background
        {
            get => _background;
            set { _background = value; InvalidateVisual(); }
        }

        /// <summary>
        /// Effective UI scale used for sizing handles. Returns the inverse of
        /// the viewport scale so handles stay constant in screen pixels
        /// regardless of canvas zoom. (Handle size in screen px =
        /// UnscaledControlSize × dpi.X × viewportScale.)
        /// </summary>
        public DpiScale CanvasUiElementScale => new DpiScale(1.0 / _viewportScale);

        public int Count => GraphicsList?.Count ?? 0;

        public GraphicBase this[int index] => GraphicsList[index];

        public bool IsMouseCaptured => _capturedPointer != null;

        /// <summary>Current viewport zoom factor (content → screen).</summary>
        public double ContentScale => _viewportScale;

        /// <summary>Current viewport translation in screen pixels.</summary>
        public Point ContentOffset => new Point(_viewportTx, _viewportTy);

        private IBrush? _background;
        private readonly Dictionary<ToolType, ToolDesc> _toolStore = new();
        private readonly ToolPointer _toolPointer;
        private readonly UndoManager _undoManager;
        private IPointer? _capturedPointer;

        private record ToolDesc(string Name, ToolBase Instance, Type? GraphicType = null, Skill DefaultSkills = Skill.None);

        // Viewport transform: screen = content * scale + (tx, ty)
        // Stored as components (rather than a Matrix) so we can easily
        // mutate scale and translation independently from pan/zoom helpers.
        private double _viewportScale = 1.0;
        private double _viewportTx;
        private double _viewportTy;

        // Middle-mouse pan state, independent of the current Tool.
        private IPointer? _middlePanPointer;
        private Point _middlePanLast;

        private ToolBase? CurrentTool => _toolStore.TryGetValue(Tool, out var d) ? d.Instance : null;
        private ToolDesc? CurrentToolDesc => _toolStore.TryGetValue(Tool, out var d) ? d : null;

        private void OnGraphicsListChanged(AvaloniaPropertyChangedEventArgs e)
        {
            if (e.OldValue is GraphicCollection oldList)
                oldList.Changed -= OnCollectionChanged;
            if (e.NewValue is GraphicCollection newList)
            {
                newList.Changed += OnCollectionChanged;
                newList.Dpi = CanvasUiElementScale;
            }
            InvalidateVisual();
            RecomputeSubject();
        }

        private void OnCollectionChanged(object? sender, EventArgs e)
        {
            InvalidateVisual();
            RecomputeSubject();
        }

        private void OnToolChanged()
        {
            CurrentTool?.SetCursor(this);
            RecomputeSubject();
        }

        private bool _suppressPush;

        private void RecomputeSubject()
        {
            // Selection wins over tool when figuring out which property editors
            // to expose.
            var selected = GraphicsList?.SelectedItems ?? Array.Empty<GraphicBase>();
            if (selected.Length > 0)
            {
                var first = selected[0];
                var (name, skills) = ReadGraphicMetadata(first.GetType());
                // Rotation is universal for any selected shape.
                SubjectName = name;
                SubjectSkill = skills | Skill.Angle;
                PullCanvasFromSubject(first);
                return;
            }

            var desc = CurrentToolDesc;
            if (desc == null)
            {
                SubjectName = string.Empty;
                SubjectSkill = Skill.CanvasBackground;
                return;
            }

            var skill = desc.DefaultSkills;
            if (desc.GraphicType != null)
            {
                var (_, gfxSkills) = ReadGraphicMetadata(desc.GraphicType);
                skill |= gfxSkills;
            }
            SubjectName = desc.Name;
            SubjectSkill = skill;
        }

        /// <summary>
        /// Mirror the first-selected graphic's properties up to the canvas
        /// styled properties so the toolbar inputs reflect the selection.
        /// </summary>
        private void PullCanvasFromSubject(GraphicBase g)
        {
            _suppressPush = true;
            try
            {
                ObjectColor = g.ObjectColor;
                LineWidth = g.LineWidth;
                if (g is GraphicRectangle r)
                    ObjectAngle = r.Angle;
            }
            finally { _suppressPush = false; }
        }

        /// <summary>
        /// Push the canvas styled properties down onto every selected graphic.
        /// Triggered when the user edits a toolbar input.
        /// </summary>
        private void PushSubjectFromCanvas(bool syncColor = false, bool syncStroke = false, bool syncAngle = false)
        {
            if (_suppressPush) return;
            var selected = GraphicsList?.SelectedItems ?? Array.Empty<GraphicBase>();
            if (selected.Length == 0) return;

            foreach (var g in selected)
            {
                if (syncColor) g.ObjectColor = ObjectColor;
                if (syncStroke) g.LineWidth = LineWidth;
                if (syncAngle && g is GraphicRectangle r) r.Angle = ObjectAngle;
            }
        }

        private static (string Name, Skill Skills) ReadGraphicMetadata(Type type)
        {
            var attr = type.GetCustomAttribute<GraphicDescAttribute>();
            return attr != null ? (attr.Name, attr.Skills) : (type.Name, Skill.None);
        }

        // ---- Pointer capture helpers (mirror WPF DrawingCanvas methods that the tools call) ----

        public void CaptureMouse(IPointer pointer)
        {
            pointer.Capture(this);
            _capturedPointer = pointer;
        }

        public void ReleaseMouseCapture()
        {
            // Null the field *before* clearing the pointer's capture, because
            // Pointer.Capture(null) fires OnPointerCaptureLost synchronously.
            // If _capturedPointer is still set at that point the handler treats
            // it as an involuntary capture loss and calls AbortOperation, which
            // destroys the shape the tool was about to finalize. Clearing first
            // makes explicit release a no-op in OnPointerCaptureLost.
            var p = _capturedPointer;
            _capturedPointer = null;
            p?.Capture(null);
        }

        public void UnselectAll()
        {
            foreach (var g in GraphicsList)
                g.IsSelected = false;
        }

        public void UnselectAllExcept(GraphicBase? except)
        {
            foreach (var g in GraphicsList)
                if (g != except)
                    g.IsSelected = false;
        }

        public void SelectAll()
        {
            if (GraphicsList == null) return;
            foreach (var g in GraphicsList)
                if (!g.IsScaffolding)
                    g.IsSelected = true;
        }

        public void DeleteSelected()
        {
            var list = GraphicsList;
            if (list == null) return;
            var selected = list.SelectedItems;
            if (selected.Length == 0) return;
            foreach (var g in selected)
                list.Remove(g);
            AddCommandToHistory(false);
        }

        public void DeleteAll()
        {
            var list = GraphicsList;
            if (list == null || list.Count == 0) return;
            list.Clear();
            AddCommandToHistory(false);
        }

        public void MoveSelectionToFront()
        {
            var list = GraphicsList;
            if (list == null) return;
            foreach (var g in list.SelectedItems)
                list.MoveToFront(g);
            AddCommandToHistory(false);
        }

        public void MoveSelectionToBack()
        {
            var list = GraphicsList;
            if (list == null) return;
            foreach (var g in list.SelectedItems)
                list.MoveToBack(g);
            AddCommandToHistory(false);
        }

        public void MoveSelectionForward()
        {
            var list = GraphicsList;
            if (list == null) return;
            foreach (var g in list.SelectedItems)
                list.MoveForward(g);
            AddCommandToHistory(false);
        }

        public void MoveSelectionBackward()
        {
            var list = GraphicsList;
            if (list == null) return;
            foreach (var g in list.SelectedItems)
                list.MoveBackward(g);
            AddCommandToHistory(false);
        }

        public void ResetSelectionRotation()
        {
            var selected = GraphicsList?.SelectedItems ?? Array.Empty<GraphicBase>();
            foreach (var g in selected)
                if (g is GraphicRectangle r)
                    r.Angle = 0;
            if (selected.Length > 0)
                AddCommandToHistory(false);
        }

        /// <summary>
        /// Snapshot the current canvas state into the undo history. Tools call
        /// this once at the end of an edit (drag, create, paste, ...). The
        /// <paramref name="merge"/> parameter is currently unused — see the
        /// <see cref="UndoManager"/> XML doc comment for the rationale.
        /// </summary>
        public void AddCommandToHistory(bool merge)
        {
            _undoManager.AddCommandStep(merge);
        }

        public bool CanUndo => _undoManager.CanUndo;
        public bool CanRedo => _undoManager.CanRedo;

        public void Undo()
        {
            _undoManager.Undo();
        }

        public void Redo()
        {
            _undoManager.Redo();
        }

        // ---- Viewport / coordinate transforms ----

        /// <summary>
        /// Convert a screen-space point (e.g. from <c>e.GetPosition(canvas)</c>)
        /// into the canvas's content-space coordinate system. Tools should call
        /// this on every pointer position so they always operate in content
        /// coordinates regardless of pan / zoom.
        /// </summary>
        public Point ToContentPoint(Point screenPoint)
        {
            return new Point(
                (screenPoint.X - _viewportTx) / _viewportScale,
                (screenPoint.Y - _viewportTy) / _viewportScale);
        }

        public Point ToScreenPoint(Point contentPoint)
        {
            return new Point(
                contentPoint.X * _viewportScale + _viewportTx,
                contentPoint.Y * _viewportScale + _viewportTy);
        }

        /// <summary>Pan the viewport by the given delta in screen pixels.</summary>
        public void PanByScreenDelta(double dx, double dy)
        {
            _viewportTx += dx;
            _viewportTy += dy;
            InvalidateVisual();
            ViewportChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Fires after any viewport mutation (pan, zoom, reset). The shell
        /// uses it to reposition screen-space overlays like the in-place
        /// text editor.
        /// </summary>
        public event EventHandler? ViewportChanged;

        /// <summary>
        /// Multiply the viewport scale by <paramref name="factor"/>, anchored at
        /// <paramref name="screenAnchor"/> so the content under that point stays
        /// fixed.
        /// </summary>
        public void ZoomBy(double factor, Point screenAnchor)
        {
            if (factor <= 0) return;
            // Clamp to a sensible range so a stray wheel-spam doesn't blow up.
            var newScale = Math.Clamp(_viewportScale * factor, 0.05, 50.0);
            factor = newScale / _viewportScale;

            // Keep screenAnchor stable: tx_new = anchor.X * (1 - factor) + tx_old * factor
            _viewportTx = screenAnchor.X * (1 - factor) + _viewportTx * factor;
            _viewportTy = screenAnchor.Y * (1 - factor) + _viewportTy * factor;
            _viewportScale = newScale;

            if (GraphicsList != null)
                GraphicsList.Dpi = CanvasUiElementScale;

            InvalidateVisual();
            ViewportChanged?.Invoke(this, EventArgs.Empty);
        }

        public void ResetViewport()
        {
            _viewportScale = 1.0;
            _viewportTx = 0;
            _viewportTy = 0;
            if (GraphicsList != null)
                GraphicsList.Dpi = CanvasUiElementScale;
            InvalidateVisual();
            ViewportChanged?.Invoke(this, EventArgs.Empty);
        }

        // ---- In-place text editing ----

        /// <summary>
        /// Raised when the user double-clicks a <see cref="GraphicText"/> and
        /// the shell (e.g. <c>EditorWindow</c>) should display an in-place
        /// TextBox overlay. The shell is responsible for positioning,
        /// focusing, and committing — the canvas only knows that editing is
        /// requested. See <c>EditorWindow.OnTextEditRequested</c> for the
        /// Avalonia host implementation.
        /// </summary>
        public event EventHandler<TextEditRequestedEventArgs>? TextEditRequested;

        /// <summary>
        /// Called by <see cref="GraphicText.Activate"/>. Raises
        /// <see cref="TextEditRequested"/> so the outer shell can host a
        /// positioned editor. No-op if nothing is subscribed.
        /// </summary>
        public void RequestTextEdit(GraphicText target)
        {
            TextEditRequested?.Invoke(this, new TextEditRequestedEventArgs(target));
        }

        // ---- Artwork export ----

        /// <summary>
        /// Computes the union of every non-scaffolding graphic's bounding box.
        /// Returns <see cref="Rect.Empty"/> if the canvas holds nothing paintable.
        /// </summary>
        public Rect GetArtworkBounds()
        {
            var list = GraphicsList;
            if (list == null || list.Count == 0) return default;

            bool any = false;
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            foreach (var g in list)
            {
                if (g.IsScaffolding) continue;
                var b = g.Bounds;
                if (b.Width <= 0 || b.Height <= 0) continue;
                if (b.Left   < minX) minX = b.Left;
                if (b.Top    < minY) minY = b.Top;
                if (b.Right  > maxX) maxX = b.Right;
                if (b.Bottom > maxY) maxY = b.Bottom;
                any = true;
            }
            if (!any) return default;
            return new Rect(minX, minY, maxX - minX, maxY - minY);
        }

        /// <summary>
        /// Flattens the current artwork (all non-scaffolding graphics, plus the
        /// artwork background colour if opaque) into a <see cref="RenderTargetBitmap"/>.
        /// No viewport transform is applied — the bitmap is always at 100% zoom,
        /// its pixel size equal to the artwork bounds plus an optional
        /// <paramref name="padding"/>. Returns <c>null</c> if there is nothing
        /// to render.
        /// </summary>
        public RenderTargetBitmap? RenderArtworkToBitmap(int padding = 8)
        {
            var list = GraphicsList;
            if (list == null) return null;

            var bounds = GetArtworkBounds();
            if (bounds.Width <= 0 || bounds.Height <= 0) return null;

            var w = (int)Math.Ceiling(bounds.Width) + padding * 2;
            var h = (int)Math.Ceiling(bounds.Height) + padding * 2;
            if (w <= 0 || h <= 0) return null;

            var rtb = new RenderTargetBitmap(new Avalonia.PixelSize(w, h), new Avalonia.Vector(96, 96));
            using (var ctx = rtb.CreateDrawingContext())
            {
                var bg = ArtworkBackground;
                if (bg.A != 0)
                    ctx.DrawRectangle(new SolidColorBrush(bg), null, new Rect(0, 0, w, h));

                // Translate so the artwork's top-left lands at (padding, padding).
                var translate = Matrix.CreateTranslation(-bounds.X + padding, -bounds.Y + padding);
                using (ctx.PushTransform(translate))
                {
                    var dpi = new DpiScale(1.0);
                    list.Dpi = dpi;
                    foreach (var g in list)
                    {
                        if (g.IsScaffolding) continue;
                        // Use DrawObject directly so selection handles / dashed
                        // borders aren't baked into the exported bitmap.
                        g.DrawObject(ctx);
                    }
                }
            }
            return rtb;
        }

        // ---- Pointer event dispatch ----

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            Focus();

            var props = e.GetCurrentPoint(this).Properties;
            if (props.IsMiddleButtonPressed)
            {
                // Always-on middle-mouse pan, independent of the current tool.
                _middlePanPointer = e.Pointer;
                _middlePanLast = e.GetPosition(this);
                e.Pointer.Capture(this);
                _capturedPointer = e.Pointer;
                Cursor = new Cursor(StandardCursorType.SizeAll);
                return;
            }

            // Double-click routes to Graphic.Activate — used by GraphicImage to
            // toggle crop mode and GraphicText to enter inline edit mode. Only
            // left-button double-clicks trigger this; middle/right pass through.
            if (e.ClickCount == 2 && props.IsLeftButtonPressed)
            {
                var pt = ToContentPoint(e.GetPosition(this));
                var dpi = CanvasUiElementScale;
                GraphicBase? hit = null;
                for (int i = GraphicsList.Count - 1; i >= 0; i--)
                {
                    var g = GraphicsList[i];
                    if (g.IsScaffolding) continue;
                    if (g.MakeHitTest(pt, dpi) >= 0) { hit = g; break; }
                }
                if (hit != null)
                {
                    hit.Activate(this);
                    e.Handled = true;
                    return;
                }
            }

            CurrentTool?.OnPointerPressed(this, e);
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);

            if (_middlePanPointer == e.Pointer)
            {
                var pt = e.GetPosition(this);
                PanByScreenDelta(pt.X - _middlePanLast.X, pt.Y - _middlePanLast.Y);
                _middlePanLast = pt;
                return;
            }

            CurrentTool?.OnPointerMoved(this, e);
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);

            if (_middlePanPointer == e.Pointer)
            {
                _middlePanPointer = null;
                ReleaseMouseCapture();
                Cursor = HelperFunctions.DefaultCursor;
                return;
            }

            CurrentTool?.OnPointerReleased(this, e);
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            if (_middlePanPointer == e.Pointer)
            {
                _middlePanPointer = null;
            }
            if (_capturedPointer == e.Pointer)
            {
                _capturedPointer = null;
                CurrentTool?.AbortOperation(this);
            }
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            // Wheel scroll → zoom around the cursor. ~10% per click.
            var factor = Math.Pow(1.10, e.Delta.Y);
            ZoomBy(factor, e.GetPosition(this));
            e.Handled = true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            var ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
            var shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
            var alt = (e.KeyModifiers & KeyModifiers.Alt) != 0;

            if (ctrl && e.Key == Key.Z)
            {
                Undo();
                e.Handled = true;
            }
            else if (ctrl && (e.Key == Key.Y || (e.Key == Key.Z && shift)))
            {
                Redo();
                e.Handled = true;
            }
            else if (ctrl && e.Key == Key.A)
            {
                SelectAll();
                e.Handled = true;
            }
            else if (e.Key == Key.Delete)
            {
                DeleteSelected();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                UnselectAll();
                e.Handled = true;
            }
            else if (e.Key == Key.Home && ctrl)
            {
                MoveSelectionForward();
                e.Handled = true;
            }
            else if (e.Key == Key.End && ctrl)
            {
                MoveSelectionBackward();
                e.Handled = true;
            }
            else if (e.Key == Key.Home)
            {
                MoveSelectionToFront();
                e.Handled = true;
            }
            else if (e.Key == Key.End)
            {
                MoveSelectionToBack();
                e.Handled = true;
            }
            // --- Single-letter tool shortcuts (no modifiers only so they don't
            //     collide with Ctrl+A etc). Port of the WPF BareKeyBinding set.
            else if (!ctrl && !alt && !shift)
            {
                ToolType? pick = e.Key switch
                {
                    Key.S => ToolType.Pointer,
                    Key.D => ToolType.None,          // Pan
                    Key.R => ToolType.Rectangle,
                    Key.F => ToolType.FilledRectangle,
                    Key.E => ToolType.Ellipse,
                    Key.L => ToolType.Line,
                    Key.A => ToolType.Arrow,
                    Key.P => ToolType.PolyLine,      // Pencil
                    Key.T => ToolType.Text,
                    Key.N => ToolType.Count,         // Numbered step
                    Key.O => ToolType.Pixelate,      // Obscure
                    _ => null,
                };
                if (pick.HasValue)
                {
                    Tool = pick.Value;
                    e.Handled = true;
                }
            }
        }

        // ---- Render ----

        /// <summary>
        /// Builds the screen-space tiled checkerboard brush ported from the WPF
        /// editor's <c>CheckeredLargeLightWhiteBackgroundBrush</c>. The original
        /// used a vector geometry "M0,0 H1 V1 H2 V2 H1 V1 H0Z" (an L-shape made
        /// of two unit squares) inside a 0,0,2,2 viewbox stretched to a 50×50
        /// viewport — a 25× scale, so the visible squares are 25×25 px each.
        /// We bake that to a 50×50 bitmap and tile it. The pattern is fixed in
        /// screen space so panning the artwork visibly slides over it.
        /// </summary>
        private static IBrush BuildCheckeredBackgroundBrush()
        {
            var bmp = new RenderTargetBitmap(new PixelSize(50, 50), new Vector(96, 96));
            using (var ctx = bmp.CreateDrawingContext())
            {
                var fill = new SolidColorBrush(Color.FromArgb(0x11, 0xff, 0xff, 0xff));
                ctx.FillRectangle(fill, new Rect(0, 0, 25, 25));
                ctx.FillRectangle(fill, new Rect(25, 25, 25, 25));
            }
            return new ImageBrush(bmp)
            {
                TileMode = TileMode.Tile,
                Stretch = Stretch.None,
                DestinationRect = new RelativeRect(0, 0, 50, 50, RelativeUnit.Absolute),
            };
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);

            var list = GraphicsList;
            if (list == null) return;

            // Push the viewport transform: content → screen = content*scale + (tx,ty).
            var viewport =
                Matrix.CreateScale(_viewportScale, _viewportScale) *
                Matrix.CreateTranslation(_viewportTx, _viewportTy);

            using (context.PushTransform(viewport))
            {
                // Checkered pattern, anchored to *content* space rather than screen
                // space, so panning and zooming visibly slide the pattern under the
                // cursor — that's the affordance the user wanted ("it needs to move
                // when you are panning"). Cover the visible content region by
                // inverse-transforming the on-screen Bounds back into content units.
                if (_background != null && viewport.TryInvert(out var inverse))
                {
                    var visibleContent = new Rect(Bounds.Size).TransformToAABB(inverse);
                    context.DrawRectangle(_background, null, visibleContent);
                }

                // Artwork background — only paints if the user picked a non-transparent
                // colour. Bounded to the artwork content rect, so it sits *on top* of
                // the checkered pattern but only inside the artwork region.
                // Mirrors the WPF editor's InvalidateBackground which drew at
                // GraphicsList.ContentBounds.
                var bgColor = ArtworkBackground;
                if (bgColor.A != 0)
                {
                    var bounds = list.ContentBounds;
                    if (bounds.Width > 0 && bounds.Height > 0)
                    {
                        context.DrawRectangle(new SolidColorBrush(bgColor), null, bounds);
                    }
                }

                var dpi = CanvasUiElementScale;
                list.Dpi = dpi;
                foreach (var g in list)
                {
                    g.Draw(context, dpi);
                }
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Clowd.Drawing;
using Clowd.Drawing.Graphics;
using Clowd.UI.Helpers;

namespace Clowd.UI.Controls
{
    /// <summary>
    /// A live view over <see cref="DrawingCanvas.GraphicsList"/> (top of the z-stack first).
    /// Rows are rebuilt wholesale — object counts are small and the collection deliberately
    /// exposes only coarse signals (StructureChanged + SelectedItems/Count PropertyChanged);
    /// per-graphic PropertyChanged subscriptions are unsafe because reorders drop them
    /// (RemoveAt calls DisconnectFromParent). Mutations go through the canvas's public
    /// panel seam (ToggleHidden/ToggleLocked/SetPanelSelection/MoveGraphicToIndex) so every
    /// change is committed to undo history by the canvas itself.
    /// </summary>
    public partial class LayersPanel : UserControl
    {
        private static readonly Dictionary<Type, string> _typeNameCache = new Dictionary<Type, string>();
        private static readonly SolidColorBrush _badgeBrush = new SolidColorBrush(Color.FromUInt32(0xFF666666));

        private DrawingCanvas _canvas;
        private GraphicCollection _collection;
        private bool _hooked;
        private bool _rebuildQueued;
        private ControlTheme _rowButtonTheme;

        public LayersPanel()
        {
            DataContext = this;
            InitializeComponent();
            Rebuild();
        }

        /// <summary>Binds the panel to a canvas. Re-attaching the same canvas is an idempotent
        /// refresh; attaching a different canvas detaches the previous one first.</summary>
        public void Attach(DrawingCanvas canvas)
        {
            if (ReferenceEquals(_canvas, canvas))
            {
                Rebuild();
                return;
            }

            Detach();
            _canvas = canvas;
            Hook();
            Rebuild();
        }

        /// <summary>Unhooks all canvas/collection events and empties the panel.</summary>
        public void Detach()
        {
            Unhook();
            _canvas = null;
            Rebuild();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            if (_canvas != null)
            {
                Hook();
                ScheduleRebuild();
            }
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            // leak guard: drop the event subscriptions but keep _canvas so a re-attach to the
            // visual tree (OnAttachedToVisualTree above) resumes without another Attach() call
            Unhook();
        }

        // ====================================================================
        // Event hookup
        // ====================================================================

        private void Hook()
        {
            if (_canvas == null || _hooked)
                return;

            // GraphicsList is a StyledProperty: RestoreState can swap the collection instance,
            // so watch the canvas property and re-hook the new collection when it does
            _canvas.PropertyChanged += OnCanvasPropertyChanged;
            // fires on every discrete history commit including undo/redo — this is the only
            // signal for field-only changes (Hidden/Locked round-trips via undo raise neither
            // StructureChanged nor a SelectedItems/Count PropertyChanged)
            _canvas.StateUpdated += OnCanvasStateUpdated;
            HookCollection(_canvas.GraphicsList);
            _hooked = true;
        }

        private void Unhook()
        {
            if (!_hooked)
                return;

            _canvas.PropertyChanged -= OnCanvasPropertyChanged;
            _canvas.StateUpdated -= OnCanvasStateUpdated;
            UnhookCollection();
            _hooked = false;
        }

        private void HookCollection(GraphicCollection collection)
        {
            _collection = collection;
            if (_collection == null)
                return;

            _collection.StructureChanged += OnCollectionStructureChanged;
            _collection.PropertyChanged += OnCollectionPropertyChanged;
        }

        private void UnhookCollection()
        {
            if (_collection == null)
                return;

            _collection.StructureChanged -= OnCollectionStructureChanged;
            _collection.PropertyChanged -= OnCollectionPropertyChanged;
            _collection = null;
        }

        private void OnCanvasPropertyChanged(object sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == DrawingCanvas.GraphicsListProperty)
            {
                UnhookCollection();
                HookCollection(e.NewValue as GraphicCollection);
                ScheduleRebuild();
            }
        }

        private void OnCanvasStateUpdated(object sender, StateChangedEventArgs e) => ScheduleRebuild();

        private void OnCollectionStructureChanged(object sender, EventArgs e) => ScheduleRebuild();

        private void OnCollectionPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(GraphicCollection.SelectedItems) || e.PropertyName == nameof(GraphicCollection.Count))
                ScheduleRebuild();
        }

        // ====================================================================
        // Rebuild
        // ====================================================================

        /// <summary>Coalesces the event storm of a single user action (a reorder raises
        /// StructureChanged twice plus SelectedItems/StateUpdated) into one rebuild per
        /// dispatcher batch. Also serves as the re-entrancy guard: panel-initiated mutations
        /// re-raise the same events, which just re-arm this one post.</summary>
        private void ScheduleRebuild()
        {
            if (_rebuildQueued)
                return;

            _rebuildQueued = true;
            Dispatcher.UIThread.Post(() =>
            {
                if (_rebuildQueued)
                    Rebuild();
            });
        }

        private void Rebuild()
        {
            _rebuildQueued = false;
            rowsHost.Children.Clear();

            var graphics = _canvas?.GraphicsList?.GetGraphicList(false);
            bool empty = graphics == null || graphics.Length == 0;
            emptyText.IsVisible = empty;
            if (empty)
                return;

            // reversed: index 0 is the back of the z-stack, the panel shows the top first
            for (int i = graphics.Length - 1; i >= 0; i--)
                rowsHost.Children.Add(BuildRow(graphics[i], isTopmost: i == graphics.Length - 1, isBottommost: i == 0));
        }

        private Control BuildRow(GraphicBase g, bool isTopmost, bool isBottommost)
        {
            var canvas = _canvas;

            var icon = new Path
            {
                Data = FindIcon(GetIconKey(g)),
                Width = 14,
                Height = 14,
                Stretch = Stretch.Uniform,
                Fill = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var name = new TextBlock
            {
                Text = GetDisplayName(g),
                FontSize = 12,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            var left = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = g.Hidden ? 0.55 : 1.0, // hidden rows are dimmed
            };
            left.Children.Add(icon);
            left.Children.Add(name);
            if (g is GraphicImage)
                left.Children.Add(BuildRasterBadge());

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto,Auto") };
            grid.Children.Add(left);

            // eye + lock toggles route through the canvas seam (each is one undo step). The seam
            // raises no coarse collection signal for an unselected graphic, so refresh explicitly.
            var eye = BuildRowButton(g.Hidden ? "IconEyeOff" : "IconEye", g.Hidden ? "Show" : "Hide", () =>
            {
                canvas.ToggleHidden(g);
                ScheduleRebuild();
            });
            Grid.SetColumn(eye, 1);
            grid.Children.Add(eye);

            var lockBtn = BuildRowButton(g.Locked ? "IconLock" : "IconLockOpen", g.Locked ? "Unlock" : "Lock", () =>
            {
                canvas.ToggleLocked(g);
                ScheduleRebuild();
            }, iconOpacity: g.Locked ? 1.0 : 0.6);
            Grid.SetColumn(lockBtn, 2);
            grid.Children.Add(lockBtn);

            if (g.IsSelected)
            {
                // up in the panel = toward the top of the z-stack = HIGHER collection index
                var up = BuildRowButton("IconChevronUp", "Move up", () => canvas.MoveGraphicToIndex(g, canvas.GraphicsList.IndexOf(g) + 1));
                up.IsEnabled = !isTopmost;
                Grid.SetColumn(up, 3);
                grid.Children.Add(up);

                var down = BuildRowButton("IconChevronDown", "Move down", () => canvas.MoveGraphicToIndex(g, canvas.GraphicsList.IndexOf(g) - 1));
                down.IsEnabled = !isBottommost;
                Grid.SetColumn(down, 4);
                grid.Children.Add(down);

                // sole-select first so CommandDelete's "delete selected" semantics remove only
                // this row even when the canvas has a wider multi-selection
                var delete = BuildRowButton("IconDelete", "Delete", () =>
                {
                    canvas.SetPanelSelection(g, false);
                    ((ICommand)canvas.CommandDelete).Execute(null);
                });
                Grid.SetColumn(delete, 5);
                grid.Children.Add(delete);
            }

            var row = new Border { Child = grid };
            row.Classes.Add("layerRow");
            if (g.IsSelected)
                row.Classes.Add("selected");
            row.PointerPressed += (s, e) =>
            {
                // row buttons mark their presses handled, so this only fires on the row itself
                if (e.GetCurrentPoint(row).Properties.IsLeftButtonPressed)
                    canvas.SetPanelSelection(g, additive: e.KeyModifiers.HasFlag(KeyModifiers.Control));
            };
            row.ContextMenu = BuildContextMenu(g);
            return row;
        }

        private Button BuildRowButton(string iconKey, string tip, Action action, double iconOpacity = 1.0)
        {
            _rowButtonTheme ??= this.FindResource("LayerRowButtonTheme") as ControlTheme;
            var button = new Button
            {
                Theme = _rowButtonTheme,
                Margin = new Thickness(1, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Content = new Path
                {
                    Data = FindIcon(iconKey),
                    Width = 12,
                    Height = 12,
                    Stretch = Stretch.Uniform,
                    Fill = Brushes.White,
                    Opacity = iconOpacity,
                },
            };
            ToolTip.SetTip(button, tip);
            button.Click += (s, e) => action();
            return button;
        }

        private static Border BuildRasterBadge()
        {
            return new Border
            {
                Background = _badgeBrush,
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(3, 1),
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = "RASTER",
                    FontSize = 9,
                    Foreground = Brushes.White,
                },
            };
        }

        private ContextMenu BuildContextMenu(GraphicBase g)
        {
            var canvas = _canvas;
            var menu = new ContextMenu();
            menu.Items.Add(CreateRowMenuItem(canvas.CommandMoveToFront, g));
            menu.Items.Add(CreateRowMenuItem(canvas.CommandMoveForward, g));
            menu.Items.Add(CreateRowMenuItem(canvas.CommandMoveBackward, g));
            menu.Items.Add(CreateRowMenuItem(canvas.CommandMoveToBack, g));
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateRowMenuItem(canvas.CommandDelete, g));
            return menu;
        }

        /// <summary>The canvas RelayCommands operate on "the selection", so the row's graphic is
        /// made the sole selection before the command runs (matching the panel-click semantics).</summary>
        private MenuItem CreateRowMenuItem(RelayCommand command, GraphicBase g)
        {
            var item = new MenuItem { Header = command.Text?.Replace("_", "") };
            item.Click += (s, e) =>
            {
                var canvas = _canvas;
                if (canvas == null)
                    return;

                canvas.SetPanelSelection(g, false);
                ((ICommand)command).Execute(null);
            };
            return item;
        }

        // ====================================================================
        // Row content helpers
        // ====================================================================

        private Geometry FindIcon(string key)
        {
            if (this.TryFindResource(key, out var value) && value is Geometry geometry)
                return geometry;

            this.TryFindResource("IconToolPointer", out var fallback);
            return fallback as Geometry;
        }

        // exact/most-derived types first: GraphicCount derives from GraphicText,
        // GraphicImage/GraphicPolyLine/GraphicEllipse/GraphicFilledRectangle from
        // GraphicRectangle, GraphicArrow and GraphicMeasure from GraphicLine — pattern order is load-bearing
        private static string GetIconKey(GraphicBase g) => g switch
        {
            GraphicCount => "IconToolNumericCount",
            GraphicText => "IconToolText",
            GraphicImage => "IconPhoto",
            GraphicPolyLine => "IconToolPolyLine",
            GraphicEllipse => "IconToolEllipse",
            GraphicFilledRectangle => "IconToolFilledRectangle",
            GraphicRectangle => "IconToolRectangle",
            GraphicArrow => "IconToolArrow",
            GraphicMeasure => "IconToolMeasure",
            GraphicLine => "IconToolLine",
            _ => "IconToolPointer",
        };

        private static string GetDisplayName(GraphicBase g)
        {
            var name = GetTypeDisplayName(g.GetType());

            // text-ish rows get a short quoted body preview (GraphicCount derives from GraphicText)
            if (g is GraphicText text && !string.IsNullOrWhiteSpace(text.Body))
            {
                var body = text.Body.Replace('\r', ' ').Replace('\n', ' ').Trim();
                if (body.Length > 12)
                    body = body.Substring(0, 12) + "…";
                name = name + " “" + body + "”";
            }

            return name;
        }

        private static string GetTypeDisplayName(Type type)
        {
            if (!_typeNameCache.TryGetValue(type, out var name))
            {
                var desc = type.GetCustomAttribute<GraphicDescAttribute>();
                name = desc != null ? desc.Name : type.Name;
                _typeNameCache[type] = name;
            }

            return name;
        }
    }
}

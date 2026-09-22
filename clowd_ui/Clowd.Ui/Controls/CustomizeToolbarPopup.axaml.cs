using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Clowd.Config;
using Clowd.UI.Config;

namespace Clowd.UI.Controls
{
    /// <summary>One row of the customize flyout: a tool the host's strip can show, hide and
    /// reorder. <see cref="Key"/> is whatever the host persists the order by (an enum name in
    /// both editors) and is handed straight back in the events.</summary>
    public sealed class CustomizeToolbarItem
    {
        public CustomizeToolbarItem(string key, string displayName, bool isVisible, bool canHide = true)
        {
            Key = key;
            DisplayName = displayName;
            IsVisible = isVisible;
            CanHide = canHide;
        }

        public string Key { get; }

        public string DisplayName { get; }

        /// <summary>Whether the tool is currently on the strip — the checkbox's state.</summary>
        public bool IsVisible { get; }

        /// <summary>False for a tool the strip cannot do without (the image editor's pointer):
        /// the row is still there and still draggable, but its checkbox is disabled.</summary>
        public bool CanHide { get; }
    }

    public sealed class CustomizeToolbarVisibilityEventArgs : EventArgs
    {
        public CustomizeToolbarVisibilityEventArgs(string key, bool isVisible)
        {
            Key = key;
            IsVisible = isVisible;
        }

        public string Key { get; }

        public bool IsVisible { get; }
    }

    public sealed class CustomizeToolbarMoveEventArgs : EventArgs
    {
        public CustomizeToolbarMoveEventArgs(IReadOnlyList<string> keys, int fromRow, int toRow)
        {
            Keys = keys;
            FromRow = fromRow;
            ToRow = toRow;
        }

        /// <summary>The keys of the rows as they were before the move, in display order — the
        /// host's persisted order may name tools this flyout never showed, so a row index is not
        /// an order index and the host needs the mapping.</summary>
        public IReadOnlyList<string> Keys { get; }

        public int FromRow { get; }

        public int ToRow { get; }
    }

    /// <summary>
    /// The "Customize toolbar" flyout both editors hang off their tool strip: a checkbox and a
    /// drag grip per tool, the editor-chrome picker, and the reset buttons. The control owns the
    /// interaction (reorder drag, keyboard focus, Escape, light dismiss) and nothing else — the
    /// host supplies the rows through <see cref="ItemsProvider"/> and acts on the events, which
    /// is where every editor-specific rule (what may be hidden, how the order is persisted, what
    /// a reset means) stays.
    ///
    /// Put one in the window's tree and call <see cref="Open"/> from the customize button's Click.
    /// </summary>
    public partial class CustomizeToolbarPopup : UserControl
    {
        /// <summary>Grip dots at rest / on hover — the same two text steps the layers panel's
        /// grips take, so every reorderable list in the app reads as one control. Fallbacks for a
        /// theme that somehow has neither token; the rows are rebuilt on every open, so resolving
        /// once per build is enough to follow a theme change.</summary>
        private static readonly SolidColorBrush _gripFallback = new SolidColorBrush(Color.FromRgb(215, 215, 218));
        private static readonly SolidColorBrush _gripHoverFallback = new SolidColorBrush(Colors.White);

        private readonly RowReorderDrag _drag;

        /// <summary>The key behind each row, in display order.</summary>
        private readonly List<string> _rowKeys = new List<string>();

        /// <summary>The checkbox of each row, in display order. Tab reaches these, the layout
        /// picker and the reset buttons; the grips stay out of it, being pointer-only by nature
        /// (the rows reorder from the keyboard through the checkboxes instead).</summary>
        private readonly List<CheckBox> _checks = new List<CheckBox>();

        private bool _building;
        private Control _target;

        public CustomizeToolbarPopup()
        {
            InitializeComponent();

            dropIndicator.Background = new SolidColorBrush(AppStyles.AccentColor);
            _drag = new RowReorderDrag(rowsHost, rowsHost, dropIndicator, new RowsHost(this));

            InitializeLayoutCombo();

            btnResetOrder.Click += (_, _) => ResetOrderRequested?.Invoke(this, EventArgs.Empty);
            btnResetSettings.Click += (_, _) => ResetSettingsRequested?.Invoke(this, EventArgs.Empty);

            popup.Opened += (_, _) => FocusCheck(_checks.FirstOrDefault(c => c.IsEnabled));

            // covers every close path, light dismiss included
            popup.Closed += (_, _) =>
            {
                if (_target != null && _target.IsEffectivelyVisible)
                    _target.Focus(NavigationMethod.Tab);
                else
                    FallbackFocus?.Focus();
            };

            // The popup lives in its own PopupRoot with its own focus scope, so the window's key
            // handlers never see keys pressed in it. Hook the root itself (it only exists while
            // the popup is open, hence on attach) so Escape works whichever child has focus.
            TopLevel keyRoot = null;
            popupRoot.AttachedToVisualTree += (_, _) =>
            {
                keyRoot = TopLevel.GetTopLevel(popupRoot);
                keyRoot?.AddHandler(KeyDownEvent, Root_KeyDown, RoutingStrategies.Tunnel);
            };
            popupRoot.DetachedFromVisualTree += (_, _) =>
            {
                keyRoot?.RemoveHandler(KeyDownEvent, Root_KeyDown);
                keyRoot = null;
            };
        }

        /// <summary>Supplies the rows, in the order they should be shown, each time the flyout is
        /// opened or rebuilt. Required.</summary>
        public Func<IReadOnlyList<CustomizeToolbarItem>> ItemsProvider { get; set; }

        /// <summary>Where the keyboard goes when the flyout closes and the button that opened it
        /// has gone with it (the canvas, in both editors).</summary>
        public Control FallbackFocus { get; set; }

        /// <summary>Whether the "Reset Tool Settings" button is offered. Off for a strip whose
        /// tools have no saved settings of their own.</summary>
        public static readonly StyledProperty<bool> ShowResetSettingsProperty =
            AvaloniaProperty.Register<CustomizeToolbarPopup, bool>(nameof(ShowResetSettings), defaultValue: true);

        public bool ShowResetSettings
        {
            get => GetValue(ShowResetSettingsProperty);
            set => SetValue(ShowResetSettingsProperty, value);
        }

        public static readonly StyledProperty<string> TitleProperty =
            AvaloniaProperty.Register<CustomizeToolbarPopup, string>(nameof(Title), defaultValue: "Customize toolbar");

        public string Title
        {
            get => GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }

        /// <summary>A checkbox was toggled. The host shows or hides the tool and persists it; the
        /// flyout does not rebuild itself, so the keyboard stays on the box that was just used.</summary>
        public event EventHandler<CustomizeToolbarVisibilityEventArgs> VisibilityChanged;

        /// <summary>A row was dragged to a new position. The host reorders and persists, then
        /// calls <see cref="Rebuild"/> with the moved key to put the rows back in step.</summary>
        public event EventHandler<CustomizeToolbarMoveEventArgs> Moved;

        public event EventHandler ResetOrderRequested;

        public event EventHandler ResetSettingsRequested;

        public bool IsOpen => popup.IsOpen;

        /// <summary>Fills the flyout from <see cref="ItemsProvider"/> and shows it beside
        /// <paramref name="target"/> (the customize button).</summary>
        public void Open(Control target)
        {
            _target = target;
            Rebuild();
            popup.PlacementTarget = target;
            popup.IsOpen = true;
        }

        public void Close() => popup.IsOpen = false;

        /// <summary>Regenerates the rows in place. <paramref name="focusKey"/> names the row whose
        /// checkbox should hold the keyboard afterwards — a rebuild destroys the row that had
        /// it.</summary>
        public void Rebuild(string focusKey = null)
        {
            _drag.Cancel(); // before the rows it is holding on to go away

            _building = true;
            try
            {
                rowsHost.Children.Clear();
                _rowKeys.Clear();
                _checks.Clear();

                var items = ItemsProvider?.Invoke() ?? Array.Empty<CustomizeToolbarItem>();

                for (int i = 0; i < items.Count; i++)
                    rowsHost.Children.Add(BuildRow(items[i], i, items.Count));
            }
            finally
            {
                _building = false;
            }

            if (focusKey != null)
            {
                var index = _rowKeys.IndexOf(focusKey);
                if (index >= 0)
                    FocusCheck(_checks[index]);
            }
        }

        private Control BuildRow(CustomizeToolbarItem item, int index, int rowCount)
        {
            var row = new Grid
            {
                Height = 28,
                ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"),
            };

            // drag grip, leftmost; the cell is reserved on every row so the checkboxes stay on one
            // left edge, and the dots are only there when there is somewhere to go
            var grip = _drag.BuildGrip(index, rowCount > 1,
                ThemeBrush("SemiColorText3", _gripFallback),
                ThemeBrush("SemiColorText1", _gripHoverFallback),
                new Thickness(2, 2, 8, 2));
            Grid.SetColumn(grip, 0);
            row.Children.Add(grip);

            var check = new CheckBox
            {
                VerticalAlignment = VerticalAlignment.Center,
                IsChecked = item.IsVisible,
                IsEnabled = item.CanHide,
            };
            Grid.SetColumn(check, 1);
            var key = item.Key;
            check.IsCheckedChanged += (_, _) =>
            {
                if (_building)
                    return;

                VisibilityChanged?.Invoke(this, new CustomizeToolbarVisibilityEventArgs(key, check.IsChecked == true));
            };
            row.Children.Add(check);

            var label = new TextBlock
            {
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = ThemeBrush("SemiColorText0", Brushes.White),
                Text = item.DisplayName,
            };
            Grid.SetColumn(label, 2);
            row.Children.Add(label);

            _rowKeys.Add(key);
            _checks.Add(check);
            return row;
        }

        private IBrush ThemeBrush(string key, IBrush fallback)
            => this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : fallback;

        /// <summary>The chrome picker, bound straight to the setting, so it and the Appearance
        /// page are two views of one value and neither has to know about the other.</summary>
        private void InitializeLayoutCombo()
        {
            layoutCombo.ItemTemplate = new FuncDataTemplate<object>((o, _) =>
                new TextBlock { Text = SettingsControlFactory.GetEnumDisplayString(o) });
            layoutCombo.ItemsSource = Enum.GetValues(typeof(EditorLayout));
            layoutCombo.Bind(SelectingItemsControl.SelectedItemProperty,
                new Binding(nameof(SettingsGeneral.EditorLayout))
                {
                    Source = SettingsRoot.Current.General,
                    Mode = BindingMode.TwoWay,
                });
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == ShowResetSettingsProperty)
                btnResetSettings.IsVisible = ShowResetSettings;
            else if (change.Property == TitleProperty)
                titleText.Text = Title;
        }

        private void Root_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape)
                return;

            e.Handled = true;
            popup.IsOpen = false;
        }

        /// <summary>Posted: on open the popup root is still laying out, and after a rebuild the
        /// new rows are, so an immediate Focus() is dropped on the way in (the mini color picker
        /// does the same). Skipped if the popup closed in the meantime.</summary>
        private void FocusCheck(CheckBox check)
        {
            if (check == null)
                return;

            Dispatcher.UIThread.Post(() =>
            {
                if (popup.IsOpen && check.IsLoaded)
                    check.Focus(NavigationMethod.Tab);
            }, DispatcherPriority.Background);
        }

        /// <summary>What the rows tell <see cref="RowReorderDrag"/> about themselves: one flat
        /// list, every row may land anywhere in it.</summary>
        private sealed class RowsHost : IRowReorderDragHost
        {
            private readonly CustomizeToolbarPopup _owner;

            public RowsHost(CustomizeToolbarPopup owner) => _owner = owner;

            public int RowCount => _owner.rowsHost.Children.Count;

            public (double Top, double Height) RowExtent(int row)
            {
                var bounds = _owner.rowsHost.Children[row].Bounds;
                return (bounds.Top, bounds.Height);
            }

            public (int Start, int End) SlotGroup(int row) => (0, RowCount - 1);

            public bool CanBeginDrag => true;

            public void SetRowLifted(int row, bool lifted) =>
                _owner.rowsHost.Children[row].Opacity = lifted ? 0.45 : 1;

            public void Drop(int fromRow, int dropSlot)
            {
                var target = RowReorderMath.TargetRow(fromRow, dropSlot);
                if (target == fromRow)
                    return;

                _owner.Moved?.Invoke(_owner,
                    new CustomizeToolbarMoveEventArgs(_owner._rowKeys.ToList(), fromRow, target));
            }
        }
    }
}

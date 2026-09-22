using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;

namespace Clowd.UI.Controls
{
    /// <summary>
    /// The enum picker of the editor properties bar, in whichever chrome the host is wearing: a
    /// <see cref="CompactDropDown"/> (the dense, flat, 22px original) or a plain
    /// <see cref="ComboBox"/> (the Semi-themed control the rest of the app uses). Both live in the
    /// template; <see cref="IsModern"/> picks one, and the host sets it from a style — the editor's
    /// chrome class does it, so a field does not have to know the setting exists. Same shape as
    /// <see cref="ThemedSpinner"/>, for the same reason.
    /// </summary>
    public class ThemedDropDown : TemplatedControl
    {
        public static readonly StyledProperty<IEnumerable> ItemsSourceProperty =
            AvaloniaProperty.Register<ThemedDropDown, IEnumerable>(nameof(ItemsSource));

        public IEnumerable ItemsSource
        {
            get => GetValue(ItemsSourceProperty);
            set => SetValue(ItemsSourceProperty, value);
        }

        public static readonly StyledProperty<object> SelectedItemProperty =
            AvaloniaProperty.Register<ThemedDropDown, object>(nameof(SelectedItem), defaultBindingMode: BindingMode.TwoWay);

        public object SelectedItem
        {
            get => GetValue(SelectedItemProperty);
            set => SetValue(SelectedItemProperty, value);
        }

        /// <summary>Which of the two pickers to show. Set from the host's chrome, not read from
        /// settings here — see <see cref="ThemedSpinner.IsModern"/>.</summary>
        public static readonly StyledProperty<bool> IsModernProperty =
            AvaloniaProperty.Register<ThemedDropDown, bool>(nameof(IsModern));

        public bool IsModern
        {
            get => GetValue(IsModernProperty);
            set => SetValue(IsModernProperty, value);
        }

        private CompactDropDown _compact;
        private ComboBox _modern;

        // The hidden picker is still bound and still has a selection of its own. Without this, its
        // settling — a ComboBox that has not been given its items yet reports null — writes back
        // over the real value.
        private bool _syncing;

        static ThemedDropDown()
        {
            ControlThemes.EnsureRegistered();
        }

        protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
        {
            base.OnApplyTemplate(e);

            if (_compact != null)
                _compact.PropertyChanged -= OnCompactPropertyChanged;
            if (_modern != null)
                _modern.SelectionChanged -= OnModernSelectionChanged;

            _compact = e.NameScope.Find<CompactDropDown>("PART_Compact");
            _modern = e.NameScope.Find<ComboBox>("PART_Modern");

            // Items can be bound straight through — only the selection needs arbitrating.
            if (_compact != null)
            {
                _compact.Bind(CompactDropDown.ItemsSourceProperty, new Binding(nameof(ItemsSource)) { Source = this });
                _compact.PropertyChanged += OnCompactPropertyChanged;
            }

            if (_modern != null)
            {
                _modern.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(ItemsSource)) { Source = this });
                _modern.SelectionChanged += OnModernSelectionChanged;
            }

            SyncSelection();
            SyncVisibility();
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == IsModernProperty)
            {
                SyncVisibility();
                SyncSelection();
            }
            else if (change.Property == SelectedItemProperty || change.Property == ItemsSourceProperty)
            {
                SyncSelection();
            }
        }

        private void SyncVisibility()
        {
            if (_compact != null)
                _compact.IsVisible = !IsModern;
            if (_modern != null)
                _modern.IsVisible = IsModern;
        }

        private void SyncSelection()
        {
            var wasSyncing = _syncing;
            _syncing = true;

            try
            {
                if (_compact != null)
                    _compact.SelectedItem = SelectedItem;
                if (_modern != null)
                    _modern.SelectedItem = SelectedItem;
            }
            finally
            {
                _syncing = wasSyncing;
            }
        }

        // Only the picker on show may move the value: the hidden one is mirroring, and its own
        // settling is not a user choice.
        private void OnCompactPropertyChanged(object sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (_syncing || IsModern || e.Property != CompactDropDown.SelectedItemProperty)
                return;

            SetCurrentValue(SelectedItemProperty, e.GetNewValue<object>());
        }

        private void OnModernSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncing || !IsModern)
                return;

            // A ComboBox drops its selection when its items are replaced, which the inspector does
            // whenever the list depends on the project (the crop-window and microphone pickers).
            // The user can never choose "nothing", so a null from the box is that settling, not a
            // pick: keep the value and put it back on the box once the new items have landed.
            if (_modern.SelectedItem == null && SelectedItem != null)
            {
                SyncSelection();
                return;
            }

            SetCurrentValue(SelectedItemProperty, _modern.SelectedItem);
        }
    }
}

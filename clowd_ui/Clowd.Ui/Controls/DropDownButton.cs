using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;

namespace Clowd.UI.Controls
{
    /// <summary>
    /// Editor properties-bar dropdown (22px, same chrome idiom as SpinnerTextBox): a flat label
    /// over the chrome fill with a raised full-height ▼ column on the right. The whole control is
    /// one click target; clicking opens a light-dismiss popup listing <see cref="ItemsSource"/>,
    /// and releasing over an item commits it to <see cref="SelectedItem"/>. The label always shows
    /// the selected item (Content tracks SelectedItem).
    /// </summary>
    public class DropDownButton : Button
    {
        public static readonly StyledProperty<IEnumerable> ItemsSourceProperty =
            AvaloniaProperty.Register<DropDownButton, IEnumerable>(nameof(ItemsSource));

        public IEnumerable ItemsSource
        {
            get => GetValue(ItemsSourceProperty);
            set => SetValue(ItemsSourceProperty, value);
        }

        public static readonly StyledProperty<object> SelectedItemProperty =
            AvaloniaProperty.Register<DropDownButton, object>(nameof(SelectedItem), defaultBindingMode: BindingMode.TwoWay);

        public object SelectedItem
        {
            get => GetValue(SelectedItemProperty);
            set => SetValue(SelectedItemProperty, value);
        }

        private Popup _popup;
        private ListBox _list;
        private bool _syncingList;

        static DropDownButton()
        {
            ControlThemes.EnsureRegistered();
        }

        protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
        {
            base.OnApplyTemplate(e);

            if (_list != null)
                _list.PointerReleased -= ListPointerReleased;

            _popup = e.NameScope.Find<Popup>("PART_Popup");
            _list = e.NameScope.Find<ListBox>("PART_ListBox");

            if (_popup != null)
                _popup.PlacementTarget = this;
            if (_list != null)
                _list.PointerReleased += ListPointerReleased;
        }

        protected override void OnClick()
        {
            base.OnClick();

            if (_popup == null)
                return;

            // the popup should never be narrower than the button; sync the list highlight to the
            // current value before showing (guarded — the sync must not commit back through
            // SelectedItem or close the popup we are about to open)
            _popup.MinWidth = Bounds.Width;
            if (_list != null)
            {
                _syncingList = true;
                _list.SelectedItem = SelectedItem;
                _syncingList = false;
            }

            // no explicit close path is needed here: light dismiss swallows the outside press
            // (OverlayDismissEventPassThrough=false), so a click on the button while open closes
            // without immediately reopening
            _popup.IsOpen = true;
        }

        // commit on RELEASE rather than SelectionChanged: the ListBox already selected the item on
        // press, and SelectionChanged does not fire at all when the already-selected item is
        // clicked — release closes the popup in both cases
        private void ListPointerReleased(object sender, PointerReleasedEventArgs e)
        {
            if (_syncingList || _popup == null)
                return;

            if (_list?.SelectedItem != null)
                SetCurrentValue(SelectedItemProperty, _list.SelectedItem);
            _popup.IsOpen = false;
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            // the label is the selected item; SetCurrentValue so a locally-set placeholder
            // Content still works until the first selection arrives
            if (change.Property == SelectedItemProperty)
                SetCurrentValue(ContentProperty, change.GetNewValue<object>());
        }
    }
}

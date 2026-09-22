using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Styling;

namespace Clowd.UI.Controls
{
    /// <summary>
    /// The editor properties-bar checkbox: a small check over a caption. A labeled CheckBox is the
    /// wrong shape there — the bar is 30px tall and its labels read as captions, not as content
    /// beside a box — so the check is scaled into a Viewbox and the word goes underneath it.
    ///
    /// The templated box takes no pointer input (see the theme): this control handles the press
    /// itself so a click anywhere on it — caption included — toggles exactly once.
    /// </summary>
    public class CaptionedCheckBox : TemplatedControl
    {
        public static readonly StyledProperty<bool?> IsCheckedProperty =
            AvaloniaProperty.Register<CaptionedCheckBox, bool?>(nameof(IsChecked), false,
                defaultBindingMode: BindingMode.TwoWay);

        public bool? IsChecked
        {
            get => GetValue(IsCheckedProperty);
            set => SetValue(IsCheckedProperty, value);
        }

        public static readonly StyledProperty<string> CaptionProperty =
            AvaloniaProperty.Register<CaptionedCheckBox, string>(nameof(Caption));

        /// <summary>The word under the check — kept short, the column is 10px wide before it.</summary>
        public string Caption
        {
            get => GetValue(CaptionProperty);
            set => SetValue(CaptionProperty, value);
        }

        public static readonly StyledProperty<ThemeVariant> BoxVariantProperty =
            AvaloniaProperty.Register<CaptionedCheckBox, ThemeVariant>(nameof(BoxVariant), ThemeVariant.Dark);

        /// <summary>Which variant the templated box inside draws itself in. It is a stock Semi
        /// CheckBox, so in the light theme it paints a dark outline — right on a light bar, and
        /// invisible on the Compact chrome's dark slab, which is hand-painted dark whatever the
        /// app theme is. Dark by default, therefore: that is the Compact case. A host whose bar
        /// follows the theme (the Modern chrome) sets this to null and the box follows too.</summary>
        public ThemeVariant BoxVariant
        {
            get => GetValue(BoxVariantProperty);
            set => SetValue(BoxVariantProperty, value);
        }

        static CaptionedCheckBox()
        {
            ControlThemes.EnsureRegistered();
        }

        /// <summary>Toggles on release rather than on press, so a press that drifts off the control
        /// changes nothing — the same contract as a CheckBox. Null (never set, or three-state)
        /// counts as unchecked and goes to checked.</summary>
        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);

            if (e.Handled || e.InitialPressMouseButton != MouseButton.Left)
                return;

            var p = e.GetPosition(this);
            if (p.X < 0 || p.Y < 0 || p.X > Bounds.Width || p.Y > Bounds.Height)
                return;

            SetCurrentValue(IsCheckedProperty, IsChecked != true);
            e.Handled = true;
        }
    }
}

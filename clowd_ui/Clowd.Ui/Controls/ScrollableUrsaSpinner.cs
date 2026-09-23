using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Ursa.Controls;
using UrsaNumericUpDown = Ursa.Controls.NumericUpDown;

namespace Clowd.UI.Controls
{
    /// <summary>
    /// Ursa's <see cref="NumericDoubleUpDown"/> with a mouse wheel.
    /// <para>
    /// Ursa's numeric controls do not handle the wheel themselves; what wheel support they appear
    /// to have comes from the <c>PART_Spinner</c> ButtonSpinner underneath, which spins only while
    /// keyboard focus is already inside the control. That is no use in a properties bar, where the
    /// point of the wheel is to adjust a field you are merely pointing at. This subclass handles the
    /// wheel on the tunnel, before the spinner sees it, and spins regardless of focus.
    /// </para>
    /// The event is swallowed whether or not it paid out a notch, exactly as
    /// <see cref="CompactSpinner"/> does: the bar it sits in scrolls, and a wheel that both spun the
    /// field and scrolled it out from under the pointer would be worse than one that occasionally
    /// does nothing.
    /// </summary>
    public class ScrollableUrsaSpinner : NumericDoubleUpDown
    {
        /// <summary>Round to a whole number before stepping, so a value that arrived fractional
        /// (from a binding, or a typed entry) lands back on the ladder rather than carrying its
        /// fraction up it forever.</summary>
        public static readonly StyledProperty<bool> SnapToWholeNumberProperty =
            AvaloniaProperty.Register<ScrollableUrsaSpinner, bool>(nameof(SnapToWholeNumber));

        public bool SnapToWholeNumber
        {
            get => GetValue(SnapToWholeNumberProperty);
            set => SetValue(SnapToWholeNumberProperty, value);
        }

        /// <summary>Pools fractional wheel deltas into whole notches — a Mac trackpad reports a
        /// stream of ~0.05 fractions where a mouse reports one ±1 per detent.</summary>
        private WheelNotchAccumulator _wheelNotches;

        // Ursa ships ONE theme for the whole numeric family, keyed on the abstract NumericUpDown
        // (every concrete NumericXxxUpDown points its own style key at it). Without this a subclass
        // looks itself up, finds nothing, and lays out as a 0px-high blank.
        protected override Type StyleKeyOverride => typeof(UrsaNumericUpDown);

        private ButtonSpinner _buttonSpinner;

        public ScrollableUrsaSpinner()
        {
            AddHandler(PointerWheelChangedEvent, OnTunnelPointerWheelChanged, RoutingStrategies.Tunnel);
        }

        /// <summary>
        /// Leaves exactly one tab stop per field: the text box. Out of the box the control, the
        /// ButtonSpinner wrapping it and that spinner's two arrows are all stops of their own, so
        /// tabbing off one field took four presses to reach the next. The arrows are made
        /// unfocusable rather than merely skipped — Up/Down/PageUp/PageDown already spin from the
        /// text box, so focus has no business being on them at all — while the two containers keep
        /// their focusability (the control forwards focus to its text box) and only leave the tab
        /// order.
        /// </summary>
        protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
        {
            base.OnApplyTemplate(e);

            IsTabStop = false;

            if (_buttonSpinner != null)
                _buttonSpinner.TemplateApplied -= OnSpinnerTemplateApplied;

            _buttonSpinner = e.NameScope.Find<ButtonSpinner>(UrsaNumericUpDown.PART_Spinner);

            if (_buttonSpinner != null)
            {
                _buttonSpinner.IsTabStop = false;

                // The arrows live in the ButtonSpinner's own template, which has not been applied
                // yet — a second name scope, and the only way into it.
                _buttonSpinner.TemplateApplied += OnSpinnerTemplateApplied;
            }
        }

        private static void OnSpinnerTemplateApplied(object sender, TemplateAppliedEventArgs e)
        {
            if (e.NameScope.Find<Control>("PART_IncreaseButton") is InputElement increase)
                increase.Focusable = false;

            if (e.NameScope.Find<Control>("PART_DecreaseButton") is InputElement decrease)
                decrease.Focusable = false;
        }

        private void OnTunnelPointerWheelChanged(object sender, PointerWheelEventArgs e)
        {
            e.Handled = true;

            // Vertical only: a two-finger scroll sideways across the properties bar must not
            // rewrite whichever field it happens to pass over.
            var notches = _wheelNotches.Accumulate(e.Delta.Y);
            if (notches == 0)
                return;

            if (SnapToWholeNumber && Value is { } value)
                SetCurrentValue(ValueProperty, Math.Round(value));

            for (var i = Math.Abs(notches); i > 0; i--)
            {
                if (notches > 0)
                    Increase();
                else
                    Decrease();
            }
        }
    }
}

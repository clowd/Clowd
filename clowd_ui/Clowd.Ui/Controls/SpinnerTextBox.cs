using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Clowd.UI.Controls
{
    /// <summary>
    /// Numeric spinner (70x22) used in the editor properties bar. This is the Value/Suffix/
    /// DisplayScale redesign of the WPF SpinnerTextBox (which spun the binding source via
    /// reflection): the displayed text is $"{Math.Round(Value * DisplayScale, 2)} {Suffix}",
    /// edits commit on Enter/LostFocus (suffix stripped, divided by DisplayScale, reverted on
    /// parse failure), and spinning (buttons, wheel, Up/PageUp/Down/PageDown) snaps, steps by
    /// SpinAmount and clamps to Min/Max — spinning past an end stops there rather than wrapping
    /// to the other end.
    /// </summary>
    public class SpinnerTextBox : TemplatedControl
    {
        public static readonly StyledProperty<double> ValueProperty =
            AvaloniaProperty.Register<SpinnerTextBox, double>(nameof(Value), 0d, defaultBindingMode: BindingMode.TwoWay);

        public double Value
        {
            get => GetValue(ValueProperty);
            set => SetValue(ValueProperty, value);
        }

        public static readonly StyledProperty<double> SpinAmountProperty =
            AvaloniaProperty.Register<SpinnerTextBox, double>(nameof(SpinAmount), 1d);

        public double SpinAmount
        {
            get => GetValue(SpinAmountProperty);
            set => SetValue(SpinAmountProperty, value);
        }

        public static readonly StyledProperty<double?> MinProperty =
            AvaloniaProperty.Register<SpinnerTextBox, double?>(nameof(Min));

        public double? Min
        {
            get => GetValue(MinProperty);
            set => SetValue(MinProperty, value);
        }

        public static readonly StyledProperty<double?> MaxProperty =
            AvaloniaProperty.Register<SpinnerTextBox, double?>(nameof(Max));

        public double? Max
        {
            get => GetValue(MaxProperty);
            set => SetValue(MaxProperty, value);
        }

        public static readonly StyledProperty<bool> SnapToWholeNumberProperty =
            AvaloniaProperty.Register<SpinnerTextBox, bool>(nameof(SnapToWholeNumber));

        public bool SnapToWholeNumber
        {
            get => GetValue(SnapToWholeNumberProperty);
            set => SetValue(SnapToWholeNumberProperty, value);
        }

        public static readonly StyledProperty<string> SuffixProperty =
            AvaloniaProperty.Register<SpinnerTextBox, string>(nameof(Suffix));

        public string Suffix
        {
            get => GetValue(SuffixProperty);
            set => SetValue(SuffixProperty, value);
        }

        public static readonly StyledProperty<double> DisplayScaleProperty =
            AvaloniaProperty.Register<SpinnerTextBox, double>(nameof(DisplayScale), 1d);

        public double DisplayScale
        {
            get => GetValue(DisplayScaleProperty);
            set => SetValue(DisplayScaleProperty, value);
        }

        private TextBox _textBox;
        private Button _spinUp;
        private Button _spinDown;

        /// <summary>Pools fractional wheel deltas into whole notches — see
        /// <see cref="OnTunnelPointerWheelChanged"/>.</summary>
        private WheelNotchAccumulator _wheelNotches;

        static SpinnerTextBox()
        {
            ControlThemes.EnsureRegistered();
        }

        public SpinnerTextBox()
        {
            AddHandler(KeyDownEvent, OnTunnelKeyDown, RoutingStrategies.Tunnel);
            AddHandler(PointerWheelChangedEvent, OnTunnelPointerWheelChanged, RoutingStrategies.Tunnel);
        }

        protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
        {
            base.OnApplyTemplate(e);

            if (_spinUp != null) _spinUp.Click -= OnSpinUpClick;
            if (_spinDown != null) _spinDown.Click -= OnSpinDownClick;
            if (_textBox != null) _textBox.LostFocus -= OnTextBoxLostFocus;

            _textBox = e.NameScope.Find<TextBox>("PART_TextBox");
            _spinUp = e.NameScope.Find<Button>("PART_SpinUp");
            _spinDown = e.NameScope.Find<Button>("PART_SpinDown");

            if (_spinUp != null) _spinUp.Click += OnSpinUpClick;
            if (_spinDown != null) _spinDown.Click += OnSpinDownClick;
            if (_textBox != null) _textBox.LostFocus += OnTextBoxLostFocus;

            UpdateDisplay();
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == ValueProperty || change.Property == SuffixProperty || change.Property == DisplayScaleProperty)
                UpdateDisplay();
        }

        private void OnSpinUpClick(object sender, RoutedEventArgs e) => Spin(1);

        private void OnSpinDownClick(object sender, RoutedEventArgs e) => Spin(-1);

        private void OnTextBoxLostFocus(object sender, RoutedEventArgs e) => CommitText();

        private void OnTunnelKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Up || e.Key == Key.PageUp)
            {
                e.Handled = true;
                Spin(1);
            }
            else if (e.Key == Key.Down || e.Key == Key.PageDown)
            {
                e.Handled = true;
                Spin(-1);
            }
            else if (e.Key == Key.Return)
            {
                e.Handled = true;
                CommitText();
            }
        }

        private void OnTunnelPointerWheelChanged(object sender, PointerWheelEventArgs e)
        {
            // Swallowed either way, notch or not: the properties bar scrolls under the pointer, and
            // a wheel that spun the field *and* scrolled it out from under the pointer would be
            // worse than one that occasionally does nothing.
            e.Handled = true;

            // One event is not one step. A Windows detent arrives as a whole ±1 and still spins
            // immediately (multiple notches in a single event spin once each, like the image
            // editor's zoom stops), but a Mac trackpad sends a stream of ~0.05 fractions and the
            // old "anything non-zero is a notch" reading turned a light two-finger scroll over a
            // field into dozens of steps.
            //
            // The horizontal axis is ignored rather than folded in: a two-finger scroll sideways
            // across the properties bar must not rewrite the value it happens to pass over, and the
            // old code's `else` branch decremented on exactly those events.
            var notches = _wheelNotches.Accumulate(e.Delta.Y);
            for (var i = Math.Abs(notches); i > 0; i--)
                Spin(Math.Sign(notches));
        }

        private void Spin(int direction)
        {
            var currentValue = Value;

            if (SnapToWholeNumber)
                currentValue = Math.Round(currentValue);

            currentValue += SpinAmount * direction;

            if (Max.HasValue)
                currentValue = Math.Min(Max.Value, currentValue);
            if (Min.HasValue)
                currentValue = Math.Max(Min.Value, currentValue);

            SetCurrentValue(ValueProperty, currentValue);
        }

        private void CommitText()
        {
            if (_textBox == null)
                return;

            var text = (_textBox.Text ?? "").Trim();
            var suffix = Suffix;
            if (!string.IsNullOrEmpty(suffix) && text.EndsWith(suffix, StringComparison.Ordinal))
                text = text.Substring(0, text.Length - suffix.Length).Trim();

            // Commit only a real edit: the box shows a 2-decimal rounding of Value, so re-writing
            // what it already displays (a focus-and-leave with no typing) would push a lossy value
            // — and, through the binding, a phantom undo entry — for an edit the user never made.
            // The tolerance compare (not ==) absorbs any last-bit disagreement between Math.Round
            // and double.Parse of the formatted text; a NaN parse compares false and never commits.
            var scale = GetDisplayScaleSafe();
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var parsed) &&
                Math.Abs(parsed - Math.Round(Value * scale, 2)) > 1e-9)
            {
                // clamp exactly as Spin does — without this a typed out-of-range number leaves the
                // control's Value outside Min/Max while a clamping binding source silently absorbs
                // it, and the box then displays a number the model does not hold.
                var value = parsed / scale;
                if (Max.HasValue)
                    value = Math.Min(Max.Value, value);
                if (Min.HasValue)
                    value = Math.Max(Min.Value, value);
                SetCurrentValue(ValueProperty, value);
            }

            // refresh the text; this also reverts it when parsing failed or the value was unchanged
            UpdateDisplay();
        }

        private void UpdateDisplay()
        {
            if (_textBox == null)
                return;

            _textBox.Text = $"{Math.Round(Value * GetDisplayScaleSafe(), 2)} {Suffix}".TrimEnd();
        }

        private double GetDisplayScaleSafe()
        {
            var scale = DisplayScale;
            return scale == 0 || double.IsNaN(scale) ? 1d : scale;
        }
    }
}

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
    /// SpinAmount, then wraps when BOTH Min and Max are set, otherwise clamps.
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
            e.Handled = true;
            if (e.Delta.Y > 0)
                Spin(1);
            else
                Spin(-1);
        }

        private void Spin(int direction)
        {
            var currentValue = Value;

            if (SnapToWholeNumber)
                currentValue = Math.Round(currentValue);

            currentValue += SpinAmount * direction;

            if (Max.HasValue && Min.HasValue && direction > 0 && currentValue > Max.Value)
            {
                // if both min and max are set, lets let the spinner loop around
                currentValue = Min.Value + (currentValue - Max.Value);
            }
            else if (Max.HasValue && Min.HasValue && direction < 0 && currentValue < Min.Value)
            {
                currentValue = Max.Value + (currentValue - Min.Value);
            }
            else
            {
                if (Max.HasValue)
                    currentValue = Math.Min(Max.Value, currentValue);
                if (Min.HasValue)
                    currentValue = Math.Max(Min.Value, currentValue);
            }

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

            if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var parsed))
                SetCurrentValue(ValueProperty, parsed / GetDisplayScaleSafe());

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

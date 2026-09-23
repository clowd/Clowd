using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;

namespace Clowd.UI.Controls
{
    public class ResetDefaultButton : Border
    {
        public static readonly StyledProperty<object> CurrentValueProperty =
            AvaloniaProperty.Register<ResetDefaultButton, object>(nameof(CurrentValue), defaultBindingMode: BindingMode.TwoWay);

        public object CurrentValue
        {
            get => GetValue(CurrentValueProperty);
            set => SetValue(CurrentValueProperty, value);
        }

        /// <summary>The value the dot resets to, and the value it compares against to decide
        /// whether to show at all. A styled property rather than a plain one so a row whose default
        /// depends on the selection (the effect dials, whose meaning changes with the effect style)
        /// can bind it — and so the dot re-evaluates when it moves, which a CLR property could not
        /// tell it.</summary>
        public static readonly StyledProperty<object> DefaultValueProperty =
            AvaloniaProperty.Register<ResetDefaultButton, object>(nameof(DefaultValue));

        public object DefaultValue
        {
            get => GetValue(DefaultValueProperty);
            set => SetValue(DefaultValueProperty, value);
        }

        public ResetDefaultButton()
        {
            this.Height = 10;
            this.Width = 10;
            this.Background = new SolidColorBrush(Color.FromRgb(106, 177, 235));
            ToolTip.SetTip(this, "Reset to default");
            this.Cursor = new Cursor(StandardCursorType.Hand);
            this.CornerRadius = new CornerRadius(5);
            this.PointerPressed += ResetDefaultButton_PointerPressed;
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == CurrentValueProperty || change.Property == DefaultValueProperty)
                EvaluateIsDefault();
        }

        // the comparison and the typed reset live in ResetDefaultValue (culture rules, tests)
        private void EvaluateIsDefault()
        {
            IsVisible = !ResetDefaultValue.IsDefault(CurrentValue, DefaultValue);
        }

        private void ResetDefaultButton_PointerPressed(object sender, PointerPressedEventArgs e)
        {
            SetCurrentValue(CurrentValueProperty, ResetDefaultValue.ForReset(CurrentValue, DefaultValue));
        }
    }
}

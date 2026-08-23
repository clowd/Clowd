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

        // The WPF equality cascade, with the first step upgraded from reference equality to
        // Equals so a boxed value set from code compares by value: then string equality, then
        // Convert.ToDouble equality (swallowing conversion failures). The string step only runs
        // when a string is actually involved — two non-string values would both cast to null and
        // compare "equal", permanently hiding the dot (bit every enum-valued binding).
        private void EvaluateIsDefault()
        {
            bool isDefault = Equals(CurrentValue, DefaultValue);

            if (!isDefault && (CurrentValue is string || DefaultValue is string))
            {
                isDefault = (CurrentValue as string) == (DefaultValue as string);
            }

            if (!isDefault)
            {
                try
                {
                    isDefault = Convert.ToDouble(CurrentValue) == Convert.ToDouble(DefaultValue);
                }
                catch { }
            }

            IsVisible = !isDefault;
        }

        private void ResetDefaultButton_PointerPressed(object sender, PointerPressedEventArgs e)
        {
            SetCurrentValue(CurrentValueProperty, DefaultValue);
        }
    }
}

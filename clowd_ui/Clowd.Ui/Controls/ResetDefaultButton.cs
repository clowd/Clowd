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

        public object DefaultValue { get; set; }

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

            if (change.Property == CurrentValueProperty)
                EvaluateIsDefault();
        }

        // The WPF equality cascade kept verbatim: reference equality, then string equality,
        // then Convert.ToDouble equality (swallowing conversion failures).
        private void EvaluateIsDefault()
        {
            bool isDefault = CurrentValue == DefaultValue;

            if (!isDefault)
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

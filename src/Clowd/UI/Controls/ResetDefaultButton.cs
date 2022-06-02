using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Clowd.UI.Controls
{
    class ResetDefaultButton : Border
    {
        public object CurrentValue
        {
            get { return (object)GetValue(CurrentValueProperty); }
            set { SetValue(CurrentValueProperty, value); }
        }

        public static readonly DependencyProperty CurrentValueProperty = DependencyProperty.Register(nameof(CurrentValue), typeof(object), typeof(ResetDefaultButton), new PropertyMetadata(null, CurrentValueChanged));

        private static void CurrentValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var ths = (d as ResetDefaultButton);

            bool isDefault = ths.CurrentValue == ths.DefaultValue;

            if (!isDefault)
            {
                isDefault = (ths.CurrentValue as string) == (ths.DefaultValue as string);
            }

            if (!isDefault)
            {
                try
                {
                    isDefault = Convert.ToDouble(ths.CurrentValue) == Convert.ToDouble(ths.DefaultValue);
                }
                catch { }
            }

            if (isDefault)
            {
                ths.Visibility = Visibility.Collapsed;
            }
            else
            {
                ths.Visibility = Visibility.Visible;
            }
        }

        public object DefaultValue { get; set; }

        public ResetDefaultButton()
        {
            this.Height = 10;
            this.Width = 10;
            this.Background = new SolidColorBrush(Color.FromRgb(106, 177, 235));
            ToolTip = "Reset to default";
            this.Cursor = Cursors.Hand;
            this.MouseDown += ResetDefaultButton_MouseDown;
            this.CornerRadius = new CornerRadius(5);
        }

        private void ResetDefaultButton_MouseDown(object sender, MouseButtonEventArgs e)
        {
            CurrentValue = DefaultValue;
        }
    }
}

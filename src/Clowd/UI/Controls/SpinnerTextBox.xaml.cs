using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Clowd.Util;

namespace Clowd.UI.Controls
{
    public partial class SpinnerTextBox : UserControl
    {
        public string Text
        {
            get => (string)GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(SpinnerTextBox), new PropertyMetadata(""));

        public double SpinAmount
        {
            get => (double)GetValue(SpinAmountProperty);
            set => SetValue(SpinAmountProperty, value);
        }

        public static readonly DependencyProperty SpinAmountProperty = DependencyProperty.Register(nameof(SpinAmount), typeof(double), typeof(SpinnerTextBox), new PropertyMetadata(1d));

        public double? Max
        {
            get => (double?)GetValue(MaxProperty);
            set => SetValue(MaxProperty, value);
        }

        public static readonly DependencyProperty MaxProperty = DependencyProperty.Register(nameof(Max), typeof(double?), typeof(SpinnerTextBox), new PropertyMetadata(null));

        public double? Min
        {
            get => (double?)GetValue(MinProperty);
            set => SetValue(MinProperty, value);
        }

        public static readonly DependencyProperty MinProperty = DependencyProperty.Register(nameof(Min), typeof(double?), typeof(SpinnerTextBox), new PropertyMetadata(null));

        public bool SnapToWholeNumber
        {
            get => (bool)GetValue(SnapToWholeNumberProperty);
            set => SetValue(SnapToWholeNumberProperty, value);
        }

        public static readonly DependencyProperty SnapToWholeNumberProperty = DependencyProperty.Register(nameof(SnapToWholeNumber), typeof(bool), typeof(SpinnerTextBox), new PropertyMetadata(false));

        public SpinnerTextBox()
        {
            InitializeComponent();
        }

        private void SpinDown(object sender, RoutedEventArgs e)
        {
            var expr = this.GetBindingExpression(TextProperty);
            dynamic source = Exposed.From(expr.ResolvedSource);

            try
            {
                var curObj = source[expr.ResolvedSourcePropertyName];

                var currentValue = Convert.ToDouble(curObj);

                if (SnapToWholeNumber)
                    currentValue = Math.Round(currentValue);

                currentValue -= SpinAmount;

                if (Max.HasValue && Min.HasValue && currentValue < Min.Value)
                {
                    // if both min and max are set, lets let the spinner loop around 
                    currentValue = Max.Value + (currentValue - Min.Value);
                }
                else
                {
                    if (Max.HasValue)
                        currentValue = Math.Min(Max.Value, currentValue);
                    if (Min.HasValue)
                        currentValue = Math.Max(Min.Value, currentValue);
                }

                source[expr.ResolvedSourcePropertyName] = currentValue;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }

        private void SpinUp(object sender, RoutedEventArgs e)
        {
            var expr = this.GetBindingExpression(TextProperty);
            dynamic source = Exposed.From(expr.ResolvedSource);

            try
            {
                var curObj = source[expr.ResolvedSourcePropertyName];

                var currentValue = Convert.ToDouble(curObj);

                if (SnapToWholeNumber)
                    currentValue = Math.Round(currentValue);

                currentValue += SpinAmount;

                if (Max.HasValue && Min.HasValue && currentValue > Max.Value)
                {
                    // if both min and max are set, lets let the spinner loop around
                    currentValue = Min.Value + (currentValue - Max.Value);
                }
                else
                {
                    if (Max.HasValue)
                        currentValue = Math.Min(Max.Value, currentValue);
                    if (Min.HasValue)
                        currentValue = Math.Max(Min.Value, currentValue);
                }

                source[expr.ResolvedSourcePropertyName] = currentValue;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }

        private void TextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Up || e.Key == Key.PageUp)
            {
                e.Handled = true;
                SpinUp(sender, e);
            }
            else if (e.Key == Key.Down || e.Key == Key.PageDown)
            {
                e.Handled = true;
                SpinDown(sender, e);
            }
        }

        private void Grid_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            e.Handled = true;
            if (e.Delta > 0)
                SpinUp(sender, e);
            else
                SpinDown(sender, e);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace AntFu7.LiveDraw
{
    class ActivatableButton : Button
    {
        public static readonly DependencyProperty IsActivatedProperty = DependencyProperty.Register(
            "IsActivated", typeof(bool), typeof(ActivatableButton), new PropertyMetadata(default(bool)));

        public bool IsActivated
        {
            get { return (bool)GetValue(IsActivatedProperty); }
            set { SetValue(IsActivatedProperty, value); }
        }
    }

    enum ColorPickerButtonSize
    {
        Small,
        Middle,
        Large
    }

    internal class ColorPicker : ActivatableButton
    {
        public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
            "Size", typeof(ColorPickerButtonSize), typeof(ColorPicker),
            new PropertyMetadata(default(ColorPickerButtonSize), OnColorPickerSizeChanged));

        public ColorPickerButtonSize Size
        {
            get { return (ColorPickerButtonSize)GetValue(SizeProperty); }
            set { SetValue(SizeProperty, value); }
        }

        private static void OnColorPickerSizeChanged(DependencyObject dependencyObject,
            DependencyPropertyChangedEventArgs eventArgs)
        {
            var v = (ColorPickerButtonSize)eventArgs.NewValue;
            var obj = dependencyObject as ColorPicker;
            if (obj == null) return;
            var w = 0.0;
            switch (v)
            {
                case ColorPickerButtonSize.Small:
                    w = (double)Application.Current.Resources["ColorPickerSmall"];
                    break;
                case ColorPickerButtonSize.Middle:
                    w = (double)Application.Current.Resources["ColorPickerMiddle"];
                    break;
                default:
                    w = (double)Application.Current.Resources["ColorPickerLarge"];
                    break;
            }

            obj.Width = w;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using DependencyPropertyGenerator;

namespace Clowd.UI.Controls
{
    [DependencyProperty<UIElement>("IconPath")]
    [DependencyProperty<UIElement>("IconPathAlternate")]
    [DependencyProperty<UIElement>("Overlay")]
    [DependencyProperty<bool>("PulseBackground")]
    [DependencyProperty<bool>("ShowAlternateIcon")]
    [DependencyProperty<bool>("ShowHover", DefaultValue = true)]
    [DependencyProperty<double>("IconSize", DefaultValue = 26d)]
    [DependencyProperty<string>("Text")]
    [DependencyProperty<bool>("Primary")]
    public partial class CaptureToolButton : Button
    {
        public bool IsDragHandle { get; set; }

        public List<SimpleKeyGesture> Gestures { get; set; } = new List<SimpleKeyGesture>();

        public EventHandler Executed { get; set; }

        static CaptureToolButton()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(CaptureToolButton), new FrameworkPropertyMetadata(typeof(CaptureToolButton)));
        }

        public CaptureToolButton()
        {
            this.Resources = Application.Current.Resources;
            this.Click += CaptureToolButton_Click;
        }

        public bool ProcessKeyState(ModifierKeys modifier, Key key)
        {
            foreach (var g in Gestures)
            {
                if (g.Key == key && g.Modifiers == modifier)
                {
                    Executed?.Invoke(this, new EventArgs());
                    return true;
                }
            }

            return false;
        }

        partial void OnPrimaryChanged(bool newValue)
        {
            if (newValue)
            {
                Background = AppStyles.AccentBackgroundBrush;
            }
            else
            {
                Background = AppStyles.IdealBackgroundBrush;
            }
        }

        partial void OnTextChanged(string newValue)
        {
            var tb = new TextBlock();
            tb.HorizontalAlignment = HorizontalAlignment.Center;
            tb.VerticalAlignment = VerticalAlignment.Bottom;
            tb.FontSize = 10;
            tb.FontWeight = FontWeights.DemiBold;
            tb.Foreground = Brushes.White;

            if (newValue != null)
            {
                var upper = newValue.ToUpper();
                var idx = upper.IndexOf('_');
                if (idx >= 0)
                {
                    tb.Inlines.Add(upper.Substring(0, idx));
                    tb.Inlines.Add(new Run() { TextDecorations = TextDecorations.Underline, Text = upper.Substring(idx + 1, 1) });
                    tb.Inlines.Add(upper.Substring(idx + 2));
                }
                else
                {
                    tb.Inlines.Add(upper);
                }

                this.Content = tb;
            }
        }

        private void CaptureToolButton_Click(object sender, RoutedEventArgs e)
        {
            Executed?.Invoke(this, e);
        }
    }
}

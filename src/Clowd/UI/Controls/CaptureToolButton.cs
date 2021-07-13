using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Clowd.Config;

namespace Clowd.UI.Controls
{
    public class CaptureToolButton : Button
    {
        public UIElement IconPath
        {
            get { return (UIElement)GetValue(IconPathProperty); }
            set { SetValue(IconPathProperty, value); }
        }

        public static readonly DependencyProperty IconPathProperty = DependencyProperty.Register("IconPath", typeof(UIElement), typeof(CaptureToolButton), new PropertyMetadata(null));

        public UIElement IconPathAlternate
        {
            get { return (UIElement)GetValue(IconPathAlternateProperty); }
            set { SetValue(IconPathAlternateProperty, value); }
        }

        public static readonly DependencyProperty IconPathAlternateProperty = DependencyProperty.Register("IconPathAlternate", typeof(UIElement), typeof(CaptureToolButton), new PropertyMetadata(null));

        public UIElement Overlay
        {
            get { return (UIElement)GetValue(OverlayProperty); }
            set { SetValue(OverlayProperty, value); }
        }

        public static readonly DependencyProperty OverlayProperty = DependencyProperty.Register("Overlay", typeof(UIElement), typeof(CaptureToolButton), new PropertyMetadata(null));

        public bool PulseBackground
        {
            get { return (bool)GetValue(PulseBackgroundProperty); }
            set { SetValue(PulseBackgroundProperty, value); }
        }

        public static readonly DependencyProperty PulseBackgroundProperty = DependencyProperty.Register("PulseBackground", typeof(bool), typeof(CaptureToolButton), new PropertyMetadata(false));

        public bool ShowAlternateIcon
        {
            get { return (bool)GetValue(ShowAlternateIconProperty); }
            set { SetValue(ShowAlternateIconProperty, value); }
        }

        public static readonly DependencyProperty ShowAlternateIconProperty = DependencyProperty.Register("ShowAlternateIcon", typeof(bool), typeof(CaptureToolButton), new PropertyMetadata(false));

        public bool ShowHover
        {
            get { return (bool)GetValue(ShowHoverProperty); }
            set { SetValue(ShowHoverProperty, value); }
        }

        public static readonly DependencyProperty ShowHoverProperty = DependencyProperty.Register("ShowHover", typeof(bool), typeof(CaptureToolButton), new PropertyMetadata(true));

        public double IconSize
        {
            get { return (double)GetValue(IconSizeProperty); }
            set { SetValue(IconSizeProperty, value); }
        }

        public static readonly DependencyProperty IconSizeProperty = DependencyProperty.Register("IconSize", typeof(double), typeof(CaptureToolButton), new PropertyMetadata(26d));

        public string Text
        {
            set => UpdateText(value);
        }

        public bool Primary
        {
            set
            {
                if (value)
                {
                    this.Background = AppStyles.AccentBackgroundBrush;
                }
                else
                {
                    this.Background = AppStyles.IdealBackgroundBrush;
                }
            }
        }

        public bool IsDragHandle { get; set; }

        public List<StorableKeyGesture> Gestures { get; set; } = new List<StorableKeyGesture>();

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

        private void CaptureToolButton_Click(object sender, RoutedEventArgs e)
        {
            Executed?.Invoke(this, e);
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

        public void UpdateText(string setTxt)
        {
            var tb = new TextBlock();
            tb.HorizontalAlignment = HorizontalAlignment.Center;
            tb.VerticalAlignment = VerticalAlignment.Bottom;
            tb.FontSize = 10;
            tb.FontWeight = FontWeights.DemiBold;
            tb.Foreground = Brushes.White;

            if (setTxt != null)
            {
                var upper = setTxt.ToUpper();
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
    }
}

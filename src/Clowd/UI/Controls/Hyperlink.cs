using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Clowd.UI.Controls
{
    public class Hyperlink : TextBlock
    {
        public static readonly DependencyProperty NavigateUriProperty = DependencyProperty.Register(
            "NavigateUri", typeof(string), typeof(Hyperlink), new PropertyMetadata(default(string)));

        public string NavigateUri
        {
            get { return (string)GetValue(NavigateUriProperty); }
            set { SetValue(NavigateUriProperty, value); }
        }

        public Hyperlink()
        {
            this.TextDecorations = System.Windows.TextDecorations.Underline;
            this.Foreground = new SolidColorBrush(AppStyles.AccentColor);
            this.MouseDown += OnMouseDown;
            this.Cursor = Cursors.Hand;
        }

        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!String.IsNullOrWhiteSpace(NavigateUri))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = NavigateUri,
                    UseShellExecute = true
                });
            }
        }
    }
}

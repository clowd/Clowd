using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Clowd.UI.Controls
{
    public class CaptureToolButton : Button
    {
        private UIElement iconPath;
        private string textb;
        private double iconSize = 26d;

        public UIElement IconPath
        {
            get => iconPath;
            set
            {
                iconPath = value;
                Update();
            }
        }

        public string Text
        {
            get => textb;
            set
            {
                textb = value;
                Update();
            }
        }

        public double IconSize
        {
            get => iconSize;
            set
            {
                iconSize = value;
                Update();
            }
        }

        Viewbox view;
        TextBlock text;
        StackPanel grid;

        static CaptureToolButton()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(CaptureToolButton), new FrameworkPropertyMetadata(typeof(CaptureToolButton)));
        }

        public CaptureToolButton()
        {
            grid = new StackPanel();
            grid.HorizontalAlignment = HorizontalAlignment.Center;
            grid.VerticalAlignment = VerticalAlignment.Center;
            grid.Orientation = Orientation.Vertical;

            view = new Viewbox();
            view.HorizontalAlignment = HorizontalAlignment.Center;
            view.VerticalAlignment = VerticalAlignment.Top;
            grid.Children.Add(view);

            text = new TextBlock();
            text.HorizontalAlignment = HorizontalAlignment.Center;
            text.VerticalAlignment = VerticalAlignment.Bottom;
            text.FontSize = 10;
            text.FontWeight = FontWeights.DemiBold;
            text.Foreground = Brushes.White;
            grid.Children.Add(text);

            this.Content = grid;

            Update();
        }

        public void Update()
        {
            view.Width = view.Height = IconSize;
            view.Child = IconPath;

            text.Inlines.Clear();

            if (Text != null)
            {
                var upper = Text.ToUpper();
                var idx = upper.IndexOf('_');
                if (idx >= 0)
                {
                    text.Inlines.Add(upper.Substring(0, idx));
                    text.Inlines.Add(new Run() { TextDecorations = TextDecorations.Underline, Text = upper.Substring(idx + 1, 1) });
                    text.Inlines.Add(upper.Substring(idx + 2));
                }
                else
                {
                    text.Inlines.Add(upper);
                }
            }
        }
    }
}

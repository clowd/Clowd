using Clowd.Utilities;
using MahApps.Metro.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CS.Wpf;

namespace Clowd
{
    public class TemplatedWindow
    {
        public static Window CreateWindow(string title, Control content)
        {
            return CreateWindow(title, content, App.Current.Settings.UseCustomWindowChrome);
        }
        public static Window CreateWindow(string title, Control content, bool customChrome)
        {
            double titleHeight, borderWidth;
            Window window;
            if (customChrome)
            {
                window = CreateMetroWindow();
                titleHeight = DpiScale.UpScaleY(40);
                borderWidth = 1;
            }
            else
            {
                window = CreateNormalWindow();
                titleHeight = SystemParameters.WindowCaptionHeight + SystemParameters.ResizeFrameHorizontalBorderHeight;
                borderWidth = SystemParameters.ResizeFrameVerticalBorderWidth;
            }
            var tcc = new Controls.TransitioningContentControl();
            tcc.HorizontalAlignment = HorizontalAlignment.Stretch;
            tcc.VerticalAlignment = VerticalAlignment.Stretch;

            window.Content = tcc;
            tcc.Content = content;

            window.Title = title;
            window.Height = content.Height + titleHeight + borderWidth;
            window.Width = content.Width + borderWidth * 2;
            content.Width = Double.NaN;
            content.Height = Double.NaN;
            content.VerticalAlignment = VerticalAlignment.Stretch;
            content.HorizontalAlignment = HorizontalAlignment.Stretch;

            return window;
        }
        public static void SetContent(Window window, Control content)
        {
            var tcc = (Controls.TransitioningContentControl)window.Content;
            content.Width = Double.NaN;
            content.Height = Double.NaN;
            tcc.Content = content;
        }
        public static void SetContent(Control currentHost, Control content)
        {
            var window = GetWindow(currentHost);
            var tcc = (Controls.TransitioningContentControl)window.Content;
            content.Width = Double.NaN;
            content.Height = Double.NaN;
            tcc.Content = content;
        }
        public static void SetContentToSpinner(Window window)
        {
            var tcc = (Controls.TransitioningContentControl)window.Content;
            Style style = Application.Current.FindResource("CircleProgressRingStyle") as Style;
            Controls.ProgressRing pr = new Controls.ProgressRing();
            pr.IsActive = true;
            pr.Style = style;
            pr.Width = 100;
            pr.SetResourceReference(Controls.ProgressRing.ForegroundProperty, "AccentColorBrush");
            pr.Height = 100;
            pr.HorizontalAlignment = HorizontalAlignment.Center;
            pr.VerticalAlignment = VerticalAlignment.Center;
            tcc.Content = pr;
        }
        public static Window GetWindow(Control content)
        {
            return Window.GetWindow(content);
        }
        public static Window GetWindow(Type contentType)
        {
            foreach(Window wnd in Application.Current.Windows)
            {
                var tcc = wnd.Content as Controls.TransitioningContentControl;
                if(tcc != null)
                {
                    if (tcc.Content.GetType() == contentType)
                        return wnd;
                }
            }
            return null;
        }

        private static MetroWindow CreateMetroWindow()
        {
            MetroWindow mw = new MetroWindow();
            mw.IconTemplate = (DataTemplate)App.Current.FindResource("MetroIconTemplate");
            mw.ResizeMode = ResizeMode.CanResizeWithGrip;
            mw.BorderThickness = new Thickness(1, 1, 1, 1);
            mw.SetResourceReference(MetroWindow.BorderBrushProperty, "WindowTitleColorBrush");
            mw.WindowTransitionsEnabled = false;
            mw.Icon = new BitmapImage(new Uri("pack://application:,,,/Images/default.ico"));
            return mw;
        }
        private static Window CreateNormalWindow()
        {
            Window w = new Window();
            w.Icon = new BitmapImage(new Uri("pack://application:,,,/Images/default.ico"));
            return w;
        }
    }
}

using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using MahApps.Metro.Controls;
using RT.Util.ExtensionMethods;
using ScreenVersusWpf;

namespace Clowd.UI.Helpers
{
    public abstract class TemplatedControl : UserControl
    {
        public abstract string Title { get; }
        protected bool IsActivated { get; private set; }

        protected virtual void OnActivated(Window wnd) { }
        protected virtual void OnDeactivated() { }

        public static ITemplatable CreateTemplatedWindow()
        {
            bool customChrome = App.Current.Settings.UseCustomWindowChrome;
            ITemplatable window = customChrome ? (ITemplatable)new modernWindow() : new regWindow();
            return window;
        }

        public interface ITemplatable
        {
            void SetContent(FrameworkElement control);
            FrameworkElement GetContent();
        }
        private class modernWindow : MetroWindow, ITemplatable
        {
            private readonly Controls.TransitioningContentControl _content;
            public modernWindow()
            {
                IconTemplate = (DataTemplate)App.Current.FindResource("MetroIconTemplate");
                ResizeMode = ResizeMode.CanResizeWithGrip;
                BorderThickness = new Thickness(1, 1, 1, 1);
                SetResourceReference(MetroWindow.BorderBrushProperty, "WindowTitleColorBrush");
                WindowTransitionsEnabled = false;

                Uri iconUri = new Uri("pack://application:,,,/Images/default.ico", UriKind.RelativeOrAbsolute);
                this.Icon = BitmapFrame.Create(iconUri, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

                var tcc = new Controls.TransitioningContentControl();
                tcc.HorizontalAlignment = HorizontalAlignment.Stretch;
                tcc.VerticalAlignment = VerticalAlignment.Stretch;
                this.Content = tcc;
                _content = tcc;

                this.Loaded += OnLoaded;
            }

            private void OnLoaded(object sender, RoutedEventArgs routedEventArgs)
            {
                var templated = _content.Content as TemplatedControl;
                if (templated != null)
                {
                    templated.IsActivated = true;
                    templated.OnActivated(this);
                }
            }

            public void SetContent(FrameworkElement control)
            {
                var templatedOld = _content.Content as TemplatedControl;
                if (templatedOld != null)
                {
                    templatedOld.IsActivated = false;
                    templatedOld.OnDeactivated();
                }

                _content.Content = control;
                if (this.IsLoaded)
                {
                    var templated = control as TemplatedControl;
                    if (templated != null)
                    {
                        this.Title = templated.Title;
                        templated.IsActivated = true;
                        templated.OnActivated(this);
                    }
                }
            }
            public FrameworkElement GetContent()
            {
                return (Control)_content.Content;
            }
        }
        private class regWindow : Window, ITemplatable
        {
            private readonly Controls.TransitioningContentControl _content;
            public regWindow()
            {
                Uri iconUri = new Uri("pack://application:,,,/Images/default.ico", UriKind.RelativeOrAbsolute);
                this.Icon = BitmapFrame.Create(iconUri, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

                var tcc = new Controls.TransitioningContentControl();
                tcc.HorizontalAlignment = HorizontalAlignment.Stretch;
                tcc.VerticalAlignment = VerticalAlignment.Stretch;
                this.Content = tcc;
                _content = tcc;

                this.Loaded += OnLoaded;
            }

            private void OnLoaded(object sender, RoutedEventArgs routedEventArgs)
            {
                var templated = _content.Content as TemplatedControl;
                if (templated != null)
                {
                    templated.IsActivated = true;
                    templated.OnActivated(this);
                }
            }

            public void SetContent(FrameworkElement control)
            {
                var templatedOld = _content.Content as TemplatedControl;
                if (templatedOld != null)
                {
                    templatedOld.IsActivated = false;
                    templatedOld.OnDeactivated();
                }

                _content.Content = control;

                if (this.IsLoaded)
                {
                    var templated = control as TemplatedControl;
                    if (templated != null)
                    {
                        this.Title = templated.Title;
                        templated.IsActivated = true;
                        templated.OnActivated(this);
                    }
                }
            }
            public FrameworkElement GetContent()
            {
                return (Control)_content.Content;
            }
        }
    }

    public sealed class TemplatedWindow
    {
        public static Window CreateWindow(string title, FrameworkElement content)
        {
            var template = TemplatedControl.CreateTemplatedWindow();
            var window = (Window)template;
            window.Closing += (s, e) => { System.Windows.Input.Keyboard.ClearFocus(); };
            window.Title = title;

            if (!Double.IsNaN(content.Width) && !Double.IsNaN(content.Height))
            {
                SizeToContent(window, new Size(content.Width, content.Height));
                content.Width = Double.NaN;
                content.Height = Double.NaN;
            }

            SetContent(window, content);

            return window;
        }

        //public static bool SizeToBounds(Window window, WpfRect bounds, double offsetX = 0, double offsetY = 0)
        //{
        //    var border = GetWindowBorderSize(window);
        //    var primaryScreen = ScreenTools.Screens.First().Bounds.ToWpfRect();
        //    window.Left = bounds.Left - primaryScreen.Left - border.Width + offsetX;
        //    window.Top = bounds.Top - primaryScreen.Top - border.Height + offsetY;
        //    return SizeToContent(window, new Size(bounds.Width, bounds.Height));
        //}

        public static bool SizeToContent(Window window, Size size, double contentLeft = Double.NaN, double contentTop = Double.NaN)
        {
            var border = GetWindowBorderSize(window);

            var wndHeight = size.Height + border.TotalBorderHeight;
            var wndWidth = size.Width + border.TotalBorderWidth;

            if (!Double.IsNaN(contentLeft) && !Double.IsNaN(contentTop))
            {
                window.Left = contentLeft - border.BorderEdgeSize;
                window.Top = contentTop - border.TitleBarHeight;
            }

            if (!Double.IsNaN(window.Left) && !Double.IsNaN(window.Top))
            {
                WpfRect primaryScreenRect = ScreenTools.Screens.First().Bounds.ToWpfRect();
                WpfRect ctrlRect = new WpfRect(window.Left + primaryScreenRect.Left, window.Top + primaryScreenRect.Top, window.Width, window.Height);
                WpfRect screenRect = ScreenTools.GetScreenContaining(ctrlRect.ToScreenRect())?.WorkingArea.ToWpfRect() ?? primaryScreenRect;

                if (wndWidth > screenRect.Width || wndHeight > screenRect.Height)
                {
                    window.WindowState = WindowState.Maximized;
                }
                else
                {
                    window.Height = wndHeight;
                    window.Width = wndWidth;

                    window.Left = window.Left.Clip(screenRect.Left - primaryScreenRect.Left, screenRect.Right - window.Width - primaryScreenRect.Left);
                    window.Top = window.Top.Clip(screenRect.Top - primaryScreenRect.Top, screenRect.Bottom - window.Height - primaryScreenRect.Top);
                    return true;
                }
            }
            else
            {
                window.Height = wndHeight;
                window.Width = wndWidth;
            }
            return false;
        }

        public static void SetContent(Window window, FrameworkElement content)
        {
            var template = (TemplatedControl.ITemplatable)window;
            window.MinHeight = 0;
            window.MinWidth = 0;

            var border = GetWindowBorderSize(window);
            if (!Double.IsNaN(content.MinHeight))
                window.MinHeight = content.MinHeight + border.TotalBorderHeight;
            if (!Double.IsNaN(content.MinWidth))
                window.MinWidth = content.MinWidth + border.TotalBorderWidth;

            content.Width = Double.NaN;
            content.Height = Double.NaN;
            content.VerticalAlignment = VerticalAlignment.Stretch;
            content.HorizontalAlignment = HorizontalAlignment.Stretch;

            template.SetContent(content);
        }
        public static void SetContent(FrameworkElement currentHost, FrameworkElement content)
        {
            var window = GetWindow(currentHost);
            SetContent(window, content);
        }
        public static void SetContentToSpinner(Window window)
        {
            var template = (TemplatedControl.ITemplatable)window;
            Style style = Application.Current.FindResource("CircleProgressRingStyle") as Style;
            Controls.ProgressRing pr = new Controls.ProgressRing();
            pr.IsActive = true;
            pr.Style = style;
            pr.Width = 100;
            pr.SetResourceReference(Controls.ProgressRing.ForegroundProperty, "AccentColorBrush");
            pr.Height = 100;
            pr.HorizontalAlignment = HorizontalAlignment.Center;
            pr.VerticalAlignment = VerticalAlignment.Center;
            template.SetContent(pr);
        }

        public static Window GetWindow(FrameworkElement content)
        {
            var openWindows = Application.Current.Windows.Cast<Window>().ToArray();

            var parent = Window.GetWindow(content);

            if (parent != null && openWindows.Contains(parent))
                return parent;

            return null;
        }
        public static Window GetWindow(Type contentType)
        {
            var windows = Application.Current.Windows.Cast<Window>().ToArray();
            return (
                from window in Application.Current.Windows.Cast<Window>()
                let content = window.Content as ContentControl
                where content != null && content.Content != null
                where content.Content.GetType() == contentType
                select window
            ).LastOrDefault();

            //return Application.Current.Windows
            //    .Cast<Window>()
            //    .Select(w => w.Content as ContentControl)
            //    .Where(c => c != null)
            //    .Where(c => c.Content.GetType() == contentType)
            //    .LastOrDefault();


            //foreach (Window wnd in Application.Current.Windows)
            //{
            //    var tcc = wnd.Content as Controls.TransitioningContentControl;
            //    if (tcc != null)
            //    {
            //        if (tcc.Content.GetType() == contentType)
            //            return wnd;
            //    }
            //}
            //return null;
        }

        public static T GetContent<T>(Window window) where T : FrameworkElement
        {
            var wnd = GetWindow(typeof(T));
            if (wnd != null)
                return (wnd.Content as ContentControl).Content as T;
            return null;
        }

        public static BorderSize GetWindowBorderSize(Window wnd)
        {
            double titleHeight, borderWidth;
            if (wnd is MetroWindow)
            {
                var metro = (MetroWindow)wnd;
                titleHeight = metro.TitlebarHeight;
                borderWidth = 1;
            }
            else
            {
                titleHeight = SystemParameters.WindowCaptionHeight + SystemParameters.ResizeFrameHorizontalBorderHeight;
                borderWidth = SystemParameters.ResizeFrameVerticalBorderWidth;
            }

            return new BorderSize(titleHeight, borderWidth);
        }

        public class BorderSize
        {
            public double TitleBarHeight { get; set; }
            public double BorderEdgeSize { get; set; }
            public double TotalBorderWidth => BorderEdgeSize * 2;
            public double TotalBorderHeight => TitleBarHeight + BorderEdgeSize;
            public BorderSize(double titleBar, double borderEdge)
            {
                TitleBarHeight = titleBar;
                BorderEdgeSize = borderEdge;
            }
        }
    }
}

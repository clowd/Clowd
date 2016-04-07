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
using ScreenVersusWpf;

namespace Clowd
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
            void SetContent(Control control);
            Control GetContent();
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

            public void SetContent(Control control)
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
            public Control GetContent()
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

            public void SetContent(Control control)
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
            public Control GetContent()
            {
                return (Control)_content.Content;
            }
        }
    }

    public sealed class TemplatedWindow
    {
        public static Window CreateWindow(string title, Control content)
        {
            var template = TemplatedControl.CreateTemplatedWindow();
            var window = (Window)template;
            window.Title = title;

            if (!Double.IsNaN(content.Width) && !Double.IsNaN(content.Height))
            {
                SizeToContent(window, new Size(content.Width, content.Height));
                content.Width = Double.NaN;
                content.Height = Double.NaN;
            }
            content.VerticalAlignment = VerticalAlignment.Stretch;
            content.HorizontalAlignment = HorizontalAlignment.Stretch;

            template.SetContent(content);

            return window;
        }

        public static bool SizeToContent(Window window, Size size)
        {
            double titleHeight, borderWidth;
            if (window is MetroWindow)
            {
                var metro = (MetroWindow)window;
                titleHeight = metro.TitlebarHeight;
                borderWidth = 1;
            }
            else
            {
                titleHeight = SystemParameters.WindowCaptionHeight + SystemParameters.ResizeFrameHorizontalBorderHeight;
                borderWidth = SystemParameters.ResizeFrameVerticalBorderWidth;
            }

            var wndHeight = size.Height + titleHeight + borderWidth;
            var wndWidth = size.Width + (borderWidth * 2);

            if (!Double.IsNaN(window.Left) && !Double.IsNaN(window.Top))
            {
                WpfRect ctrlRect = new WpfRect(window.Left, window.Top, window.Width, window.Height);
                WpfRect screenRect = ScreenTools.GetScreenContaining(ctrlRect.ToScreenRect()).WorkingArea.ToWpfRect();

                if (wndWidth > screenRect.Width || wndHeight > screenRect.Height)
                {
                    window.WindowState = WindowState.Maximized;
                }
                else
                {
                    window.Height = wndHeight;
                    window.Width = wndWidth;

                    window.Left += Math.Min(0, screenRect.Left + screenRect.Width - window.Left - window.Width);
                    window.Left -= Math.Min(0, window.Left - screenRect.Left);
                    window.Top += Math.Min(0, screenRect.Top + screenRect.Height - window.Top - window.Height);
                    window.Top -= Math.Min(0, window.Top - screenRect.Top);
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

        public static void SetContent(Window window, Control content)
        {
            var template = (TemplatedControl.ITemplatable)window;
            content.Width = Double.NaN;
            content.Height = Double.NaN;
            content.VerticalAlignment = VerticalAlignment.Stretch;
            content.HorizontalAlignment = HorizontalAlignment.Stretch;
            template.SetContent(content);
        }
        public static void SetContent(Control currentHost, Control content)
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

        public static Window GetWindow(Control content)
        {
            return Window.GetWindow(content);
        }
        public static Window GetWindow(Type contentType)
        {
            foreach (Window wnd in Application.Current.Windows)
            {
                var tcc = wnd.Content as Controls.TransitioningContentControl;
                if (tcc != null)
                {
                    if (tcc.Content.GetType() == contentType)
                        return wnd;
                }
            }
            return null;
        }
    }
}

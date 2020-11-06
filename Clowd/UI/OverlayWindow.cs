using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ScreenVersusWpf;

namespace Clowd.UI
{
    public class OverlayWindow : InteropWindow
    {
        public bool CloseOnLostFocus { get; set; } = false;

        public WpfRect SelectionRectangle
        {
            get { return (WpfRect)GetValue(SelectionRectangleProperty); }
            set { SetValue(SelectionRectangleProperty, value); }
        }

        public static readonly WpfRect SelectionRectanglePropertyDefaultValue = default(WpfRect);

        public static readonly DependencyProperty SelectionRectangleProperty =
            DependencyProperty.Register(nameof(SelectionRectangle), typeof(WpfRect), typeof(OverlayWindow),
                new PropertyMetadata(SelectionRectanglePropertyDefaultValue, (s, e) => (s as OverlayWindow)?.OnSelectionRectangleChanged(s, e)));

        public event DependencyPropertyChangedEventHandler SelectionRectangleChanged;

        protected virtual void OnSelectionRectangleChanged(object sender, DependencyPropertyChangedEventArgs e)
            => this.SelectionRectangleChanged?.Invoke(sender, e);

        public bool IsCapturing
        {
            get { return (bool)GetValue(IsCapturingProperty); }
            set { SetValue(IsCapturingProperty, value); }
        }

        public static readonly bool IsCapturingDefaultValue = false;

        public static readonly DependencyProperty IsCapturingProperty =
            DependencyProperty.Register(nameof(IsCapturing), typeof(bool), typeof(OverlayWindow),
                new PropertyMetadata(IsCapturingDefaultValue, (s, e) => (s as OverlayWindow)?.OnIsCapturingChanged(s, e)));

        public event DependencyPropertyChangedEventHandler IsCapturingChanged;

        protected virtual void OnIsCapturingChanged(object sender, DependencyPropertyChangedEventArgs e)
            => this.IsCapturingChanged?.Invoke(sender, e);

        public OverlayWindow()
        {
            this.Activated += OverlayWindow_Activated;
            this.Closing += OverlayWindow_Closing;
            this.ContentRendered += OverlayWindow_ContentRendered;

            this.WindowStyle = WindowStyle.None;
            this.ShowInTaskbar = false;
            this.ResizeMode = ResizeMode.NoResize;

            var primary = ScreenTools.Screens.First().Bounds;
            var virt = ScreenTools.VirtualScreen.Bounds;
            ScreenPosition = new ScreenRect(-primary.Left, -primary.Top, virt.Width, virt.Height);

            this.EnsureHandle();
        }

        private void OverlayWindow_ContentRendered(object sender, EventArgs e)
        {
            this.Activate();
        }

        private void OverlayWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            Deactivated -= OverlayWindow_Deactivated;
        }

        private void OverlayWindow_Activated(object sender, EventArgs e)
        {
            this.Deactivated += OverlayWindow_Deactivated;
        }

        private void OverlayWindow_Deactivated(object sender, EventArgs e)
        {
            if (CloseOnLostFocus)
                this.Close();
        }
    }
}

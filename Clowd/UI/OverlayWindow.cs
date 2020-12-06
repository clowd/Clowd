using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Clowd.Util;
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

        protected void UpdateButtonPanelPosition(StackPanel buttonPanel)
        {
            var numberOfActiveButtons = buttonPanel.Children
                .Cast<FrameworkElement>()
                .Where(f => f is Button)
                .Cast<Button>()
                .Where(b => b.Visibility != Visibility.Collapsed)
                .Count();

            SetPanelCanvasPositionRelativeToSelection(buttonPanel, SelectionRectangle, 2, 10, 50, numberOfActiveButtons * 50 + 3);
        }

        protected void SetPanelCanvasPositionRelativeToSelection(StackPanel panel, WpfRect selection, int minDistance, int maxDistance, int shortEdgePx, int longEdgePx)
        {
            var scr = ScreenTools.GetScreenContaining(selection.ToScreenRect());
            if (scr == null)
                return;

            var selectionScreen = scr.Bounds.ToWpfRect();
            // subtract 2 as that's the selection border width
            var bottomSpace = Math.Max(selectionScreen.Bottom - selection.Bottom, 0) - minDistance;
            var rightSpace = Math.Max(selectionScreen.Right - selection.Right, 0) - minDistance;
            var leftSpace = Math.Max(selection.Left - selectionScreen.Left, 0) - minDistance;
            double indLeft = 0, indTop = 0;

            //we want to display (and clip) the controls on/to the primary screen -
            //where the primary screen is the screen that contains the center of the cropping rectangle
            var intersecting = selectionScreen.Intersect(selection);
            if (intersecting == WpfRect.Empty)
                return; // not supposed to happen since selectionScreen contains the center of selection rect

            if (bottomSpace >= shortEdgePx)
            {
                panel.Orientation = Orientation.Horizontal;
                indLeft = intersecting.Left + intersecting.Width / 2 - longEdgePx / 2;
                indTop = Math.Min(selectionScreen.Bottom, intersecting.Bottom + maxDistance + shortEdgePx) - shortEdgePx;
            }
            else if (rightSpace >= shortEdgePx)
            {
                panel.Orientation = Orientation.Vertical;
                indLeft = Math.Min(selectionScreen.Right, intersecting.Right + maxDistance + shortEdgePx) - shortEdgePx;
                indTop = intersecting.Bottom - longEdgePx;
            }
            else if (leftSpace >= shortEdgePx)
            {
                panel.Orientation = Orientation.Vertical;
                indLeft = Math.Max(intersecting.Left - maxDistance - shortEdgePx, 0);
                indTop = intersecting.Bottom - longEdgePx;
            }
            else // inside capture rect
            {
                panel.Orientation = Orientation.Horizontal;
                indLeft = intersecting.Left + intersecting.Width / 2 - longEdgePx / 2;
                indTop = intersecting.Bottom - shortEdgePx - (maxDistance * 2);
            }

            var horizontalSize = panel.Orientation == Orientation.Horizontal ? longEdgePx : shortEdgePx;

            if (indLeft < selectionScreen.Left)
                indLeft = selectionScreen.Left;
            else if (indLeft + horizontalSize > selectionScreen.Right)
                indLeft = selectionScreen.Right - horizontalSize;

            Canvas.SetLeft(panel, indLeft);
            Canvas.SetTop(panel, indTop);
        }
    }
}

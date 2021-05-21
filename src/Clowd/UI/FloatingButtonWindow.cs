using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Clowd.Config;
using Clowd.Interop;
using Clowd.Interop.DwmApi;
using Clowd.Interop.Shcore;
using Clowd.UI.Controls;
using PropertyChanged;

namespace Clowd.UI
{
    [ImplementPropertyChanged]
    public class FloatingButtonDetail
    {
        public bool Primary { get; set; }
        public EventHandler Executed { get; set; }
        public string IconResourceName { get; set; }
        public string IconResourceNameAlternate { get; set; }
        public string Label { get; set; }
        public bool Enabled { get; set; }
        public StorableKeyGesture[] Gestures { get; set; }
    }

    internal sealed class FloatingButtonWindow : InteropWindow
    {
        public Canvas MainGrid { get; private set; }
        public StackPanel MainPanel { get; private set; }

        public ReadOnlyCollection<FloatingButtonDetail> ButtonDetails { get; private set; }

        private List<CaptureToolButton> ButtonElements { get; set; }

        private System.Drawing.Rectangle _lastSelection;
        private bool _isVisible;
        private readonly object _lock = new object();

        private FloatingButtonWindow(IList<FloatingButtonDetail> buttons)
        {
            this.Resources = Application.Current.Resources;
            NameScope.SetNameScope(this, new NameScope());

            NeverActivate = true;
            TransitionsDisabled = true;
            ResizeMode = ResizeMode.NoResize;
            WindowStyle = WindowStyle.None;
            ShowActivated = false;
            ShowInTaskbar = false;
            Width = 0;
            Height = 0;
            KeyDown += FloatingButtonWindow_KeyDown;
            LayoutUpdated += FloatingButtonWindow_LayoutUpdated;

            var ro = buttons.ToList().AsReadOnly();

            var els = ro.Select(b =>
            {
                var btn = new CaptureToolButton();
                btn.Text = b.Label;

                UIElement icon = null;
                UIElement iconAlt = null;

                if (!String.IsNullOrEmpty(b.IconResourceName))
                    icon = this.Resources[b.IconResourceName] as UIElement;

                if (!String.IsNullOrEmpty(b.IconResourceNameAlternate))
                    icon = this.Resources[b.IconResourceNameAlternate] as UIElement;

                btn.IconPath = icon;
                btn.IconPathAlternate = iconAlt;

                if (b.Primary)
                {
                    btn.Background = this.Resources["HighlightBrush"] as Brush;
                }

                var enabledBinding = new Binding(nameof(FloatingButtonDetail.Enabled));
                enabledBinding.Source = b;
                enabledBinding.Mode = BindingMode.TwoWay;
                btn.SetBinding(CaptureToolButton.IsEnabledProperty, enabledBinding);

                if (b.Executed != null)
                {
                    btn.Click += (s, e) =>
                    {
                        b.Executed(this, new EventArgs());
                    };
                }
                else
                {
                    btn.IsEnabled = false;
                }

                return btn;
            });

            ButtonDetails = ro;
            ButtonElements = els.ToList();

            var sp = new StackPanel();
            sp.Orientation = Orientation.Horizontal;
            sp.Background = this.Resources["IdealBackgroundBrush"] as Brush;
            ButtonElements.ForEach(b => sp.Children.Add(b));
            MainPanel = sp;

            var grid = new Canvas();
            grid.Children.Add(sp);
            MainGrid = grid;

            this.Content = grid;
            CommandManager.InvalidateRequerySuggested();
        }

        private void FloatingButtonWindow_LayoutUpdated(object sender, EventArgs e)
        {
            var selection = _lastSelection;
            var desiredSize = MainPanel.DesiredSize;

            // get bounds and dpi of target display (display which contains the center point of the rect)
            RECT nativeSel = selection;
            var hMon = USER32.MonitorFromRect(ref nativeSel, MonitorOptions.MONITOR_DEFAULTTONEAREST);
            var monInfo = new MONITORINFO();
            monInfo.cbSize = (uint)Marshal.SizeOf<MONITORINFO>();
            USER32.GetMonitorInfo(hMon, ref monInfo);
            System.Drawing.Rectangle screenBounds = monInfo.rcMonitor;

            uint dpiX = 0, dpiY = 0;
            SHCORE.GetDpiForMonitor(hMon, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, ref dpiX, ref dpiY);
            double dpiZoom = dpiX / 96.0;

            // padding measurements
            int minDistance = (int)Math.Ceiling(2 * dpiZoom);
            int maxDistance = (int)Math.Ceiling(15 * dpiZoom);

            // clip selection to monitor
            selection.Intersect(screenBounds);

            // calculate panel size & position
            int panelWidth = (int)Math.Ceiling(desiredSize.Width * dpiZoom);
            int panelHeight = (int)Math.Ceiling(desiredSize.Height * dpiZoom);

            var bottomSpace = Math.Max(screenBounds.Bottom - selection.Bottom, 0) - minDistance;
            var rightSpace = Math.Max(screenBounds.Right - selection.Right, 0) - minDistance;
            var leftSpace = Math.Max(selection.Left - screenBounds.Left, 0) - minDistance;

            int shortEdgePx = Math.Min(panelWidth, panelHeight);
            int longEdgePx = Math.Max(panelWidth, panelHeight);

            int indTop, indLeft;

            if (bottomSpace >= shortEdgePx)
            {
                MainPanel.Orientation = Orientation.Horizontal;
                indLeft = selection.Left + selection.Width / 2 - longEdgePx / 2;
                indTop = Math.Min(screenBounds.Bottom, selection.Bottom + maxDistance + shortEdgePx) - shortEdgePx;
            }
            else if (rightSpace >= shortEdgePx)
            {
                MainPanel.Orientation = Orientation.Vertical;
                indLeft = Math.Min(screenBounds.Right, selection.Right + maxDistance + shortEdgePx) - shortEdgePx;
                indTop = selection.Bottom - longEdgePx;
            }
            else if (leftSpace >= shortEdgePx)
            {
                MainPanel.Orientation = Orientation.Vertical;
                indLeft = Math.Max(selection.Left - maxDistance - shortEdgePx, 0);
                indTop = selection.Bottom - longEdgePx;
            }
            else // inside capture rect
            {
                MainPanel.Orientation = Orientation.Horizontal;
                indLeft = selection.Left + selection.Width / 2 - longEdgePx / 2;
                indTop = selection.Bottom - shortEdgePx - (maxDistance * 2);
            }

            var horizontalSize = MainPanel.Orientation == Orientation.Horizontal ? longEdgePx : shortEdgePx;
            var verticalSize = MainPanel.Orientation == Orientation.Horizontal ? shortEdgePx : longEdgePx;

            if (indLeft < screenBounds.Left)
                indLeft = screenBounds.Left;
            else if (indLeft + horizontalSize > screenBounds.Right)
                indLeft = screenBounds.Right - horizontalSize;

            USER32.SetWindowPos(Handle, SWP_HWND.HWND_TOPMOST, indLeft, indTop, horizontalSize, verticalSize, SWP.NOACTIVATE);
        }

        public static FloatingButtonWindow Create(IList<FloatingButtonDetail> buttons)
        {
            var w = new FloatingButtonWindow(buttons);
            w.Show(); // need to show to kick of wpf/directx rendering pipeline
            w.HidePanel();
            w.Topmost = true;
            return w;
        }

        public void ShowPanel(System.Drawing.Rectangle selection, IntPtr owner)
        {
            lock (_lock)
            {
                if (_lastSelection == selection && _isVisible)
                    return;

                this.Dispatcher.Invoke(() =>
                {
                    _lastSelection = selection;
                    this.InvalidateMeasure();
                    this.UpdateLayout();

                    if (!_isVisible)
                    {
                        _isVisible = true;
                        SetHwndOwner(owner);
                        USER32.SetWindowPos(Handle, SWP_HWND.HWND_TOPMOST, 0, 0, 0, 0, SWP.NOSIZE | SWP.NOACTIVATE | SWP.NOMOVE | SWP.SHOWWINDOW);
                    }
                });
            }
        }

        public void HidePanel()
        {
            lock (_lock)
            {
                this.Dispatcher.Invoke(() =>
                {
                    SetHwndOwner(IntPtr.Zero);
                    USER32.SetWindowPos(Handle, SWP_HWND.HWND_TOPMOST, 0, 0, 0, 0, SWP.NOSIZE | SWP.NOACTIVATE | SWP.NOMOVE | SWP.HIDEWINDOW);
                    _isVisible = false;
                });
            }
        }

        public Task ShowConfirmationRipple(string message)
        {
            TaskCompletionSource<bool> tcs = new TaskCompletionSource<bool>();

            var sz = MainPanel.DesiredSize;

            // create elements
            var ell = new EllipseGeometry();
            ell.Center = new Point(0, 0);
            ell.RadiusX = 50;
            ell.RadiusY = 50;

            var path = new Path();
            path.Data = ell;
            path.Fill = this.Resources["HighlightBrush"] as Brush;
            path.Width = sz.Width;
            path.Height = sz.Height;

            var text = new TextBlock();
            text.TextWrapping = TextWrapping.Wrap;
            text.Text = message;
            text.VerticalAlignment = VerticalAlignment.Center;
            text.HorizontalAlignment = HorizontalAlignment.Center;
            text.FontWeight = FontWeights.Bold;
            text.Foreground = Brushes.White;

            var border = new Border();
            border.Width = sz.Width;
            border.Height = sz.Height;
            border.Clip = ell;
            border.Child = text;

            // create animation
            string name = "ell" + DateTime.Now.Ticks;
            this.RegisterName(name, ell);

            var szMax = Math.Max(sz.Width, sz.Height) + Math.Min(sz.Width, sz.Height);
            var time = TimeSpan.FromMilliseconds(600);
            var sb = new Storyboard();

            var ease = new CubicEase();

            DoubleAnimation aniWidth = new DoubleAnimation(1, szMax, time);
            Storyboard.SetTargetName(aniWidth, name);
            Storyboard.SetTargetProperty(aniWidth, new PropertyPath(EllipseGeometry.RadiusXProperty));
            aniWidth.EasingFunction = ease;
            sb.Children.Add(aniWidth);

            DoubleAnimation aniHeight = new DoubleAnimation(1, szMax, time);
            Storyboard.SetTargetName(aniHeight, name);
            Storyboard.SetTargetProperty(aniHeight, new PropertyPath(EllipseGeometry.RadiusYProperty));
            aniHeight.EasingFunction = ease;
            sb.Children.Add(aniHeight);

            path.Loaded += (s, e) =>
            {
                sb.Begin(path);
            };

            sb.Completed += async (s, e) =>
            {
                await Task.Delay(300);
                HidePanel();
                MainGrid.Children.Remove(border);
                MainGrid.Children.Remove(path);
                tcs.SetResult(true);
            };

            MainGrid.Children.Add(path);
            MainGrid.Children.Add(border);

            return tcs.Task;
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            this.InvalidateMeasure();
            this.UpdateLayout();
        }

        private void FloatingButtonWindow_KeyDown(object sender, KeyEventArgs e)
        {
            e.Handled = ProcessKey(e.Key);
        }

        public bool ProcessKey(Key k)
        {
            if (!_isVisible)
                return false;

            var mods = Keyboard.Modifiers;
            for (int i = 0; i < ButtonDetails.Count; i++)
            {
                var detail = ButtonDetails[i];
                foreach (var g in detail.Gestures)
                {
                    if (g.Key == k && g.Modifiers == mods)
                    {
                        this.Dispatcher.Invoke(() =>
                        {
                            if (detail.Executed != null)
                            {
                                detail.Executed(this, new EventArgs());
                            }
                        });
                        return true;
                    }
                }
            }
            return false;
        }
    }
}

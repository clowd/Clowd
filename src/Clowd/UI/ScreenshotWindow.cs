using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Clowd.Interop;
using ScreenVersusWpf;

namespace Clowd.UI
{
    class ScreenshotWindow : IScreenCapturePage
    {
        static Size _lastSize;
        static System.Drawing.Rectangle _lastSel;
        static bool _showing;
        static ScreenshotButtonWindow _wbtn;
        static ClowdWin64.DXCaptureWindow _wdxc;
        static ScreenshotWindow()
        {
            _wbtn = new ScreenshotButtonWindow();
            _wbtn.ShowInTaskbar = false;
            _wbtn.ShowActivated = false;
            _wbtn.Width = 0;
            _wbtn.Height = 0;
            _wbtn.KeyDown += (s, e) =>
            {
                e.Handled = HandleKeyPress(e.Key);
            };
            _wbtn.Show();
            // need to call show initially to get WPF to start DirectX rendering pipeline

            USER32.SetWindowPos(_wbtn.Handle, SWP_HWND.HWND_TOPMOST, 0, 0, 0, 0, SWP.NOSIZE | SWP.NOACTIVATE | SWP.HIDEWINDOW | SWP.NOMOVE);
            _wbtn.SizeToContent = SizeToContent.WidthAndHeight;
            _wbtn.Topmost = true;
        }

        static bool HandleKeyPress(Key k)
        {
            return false;
        }

        static void ShowButtons(IntPtr owner, System.Drawing.Rectangle sel)
        {
            var size = _wbtn.toolActionBarStackPanel.DesiredSize;
            if (_showing && _lastSel == sel && size == _lastSize)
                return;

            _lastSize = size;
            _lastSel = sel;
            _showing = true;

            _wbtn.Dispatcher.Invoke(() =>
            {
                _wbtn.SetHwndOwner(owner);
                var pt = GetPanelCanvasPositionRelativeToSelection(_wbtn.toolActionBarStackPanel, sel, 2, 10, Math.Min(size.Height, size.Width), Math.Max(size.Height, size.Width));
                USER32.SetWindowPos(_wbtn.Handle, SWP_HWND.HWND_TOPMOST, pt.X, pt.Y, 0, 0, SWP.NOSIZE | SWP.NOACTIVATE | SWP.SHOWWINDOW);
            });
        }

        static void HideButtons()
        {
            if (!_showing)
                return;

            USER32.SetWindowPos(_wbtn.Handle, SWP_HWND.HWND_TOPMOST, 0, 0, 0, 0, SWP.NOSIZE | SWP.NOACTIVATE | SWP.HIDEWINDOW | SWP.NOMOVE);
        }


        public event EventHandler Closed;

        public void Open()
        {
            OpenInternal();
        }

        public void Open(ScreenRect captureArea)
        {
            OpenInternal(rect: captureArea.ToSystem());
        }

        public void Open(IntPtr captureWindow)
        {
            OpenInternal(wnd: captureWindow);
        }

        private void OpenInternal(System.Drawing.Rectangle? rect = null, IntPtr? wnd = null)
        {
            var clr = System.Drawing.Color.FromArgb(App.Current.AccentColor.A, App.Current.AccentColor.R, App.Current.AccentColor.G, App.Current.AccentColor.B);

            // create new capture
            var dx = new ClowdWin64.DXCaptureWindow(clr, true);
            dx.Disposed += _wdxc_Disposed;
            dx.LayoutUpdated += _wdxc_LayoutUpdated;
            dx.KeyDown += _wdxc_KeyDown;
            dx.Show();

            // close old capture (if any)
            Dispose();

            // assign new capture
            _wdxc = dx;
        }

        public void Close()
        {
            Dispose();
        }

        public void Dispose()
        {
            HideButtons();
            if (_wdxc != null)
            {
                _wdxc.Disposed -= _wdxc_Disposed;
                _wdxc.LayoutUpdated -= _wdxc_LayoutUpdated;
                _wdxc.KeyDown -= _wdxc_KeyDown;
                _wdxc.Dispose();
            }
        }

        private void _wdxc_KeyDown(object sender, ClowdWin64.CWKeyDownEventArgs e)
        {
            HandleKeyPress(KeyInterop.KeyFromVirtualKey(e.KeyCode));
        }

        private void _wdxc_Disposed(object sender, EventArgs e)
        {
            HideButtons();
        }

        private void _wdxc_LayoutUpdated(object sender, ClowdWin64.CWLayoutUpdatedEventArgs e)
        {
            if (_wdxc.HasCapturedArea)
            {
                ShowButtons(_wdxc.Handle, _wdxc.Selection);
            }
            else
            {
                HideButtons();
            }
        }

        protected static System.Drawing.Point GetPanelCanvasPositionRelativeToSelection(StackPanel panel, System.Drawing.Rectangle selection, int minDistance, int maxDistance, double shortEdgePx, double longEdgePx)
        {
            var syssel = ScreenRect.FromSystem(selection);
            var wpfsel = syssel.ToWpfRect();

            var scr = ScreenTools.GetScreenContaining(syssel);
            if (scr == null)
                return default;

            var selectionScreen = scr.Bounds.ToWpfRect();
            // subtract 2 as that's the selection border width
            var bottomSpace = Math.Max(selectionScreen.Bottom - selection.Bottom, 0) - minDistance;
            var rightSpace = Math.Max(selectionScreen.Right - selection.Right, 0) - minDistance;
            var leftSpace = Math.Max(selection.Left - selectionScreen.Left, 0) - minDistance;
            double indLeft = 0, indTop = 0;

            //we want to display (and clip) the controls on/to the primary screen -
            //where the primary screen is the screen that contains the center of the cropping rectangle
            var intersecting = selectionScreen.Intersect(wpfsel);
            if (intersecting == WpfRect.Empty)
                return default; // not supposed to happen since selectionScreen contains the center of selection rect

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

            WpfPoint wp = new WpfPoint(indLeft, indTop);
            return wp.ToScreenPoint().ToSystem();
        }
    }
}

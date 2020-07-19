using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Clowd.Controls;
using Clowd.Utilities;
using ScreenVersusWpf;

namespace Clowd
{
    public partial class CaptureWindow2 : Window
    {
        public IntPtr Handle { get; private set; }

        public CaptureWindow2()
        {
            InitializeComponent();
            this.SourceInitialized += CaptureWindow2_SourceInitialized;
        }

        private static CaptureWindow2 _readyWindow;
        public static void ShowNewCapture()
        {
            if (_readyWindow == null)
            {
                _readyWindow = new CaptureWindow2();
                _readyWindow.Show();
            }

            _readyWindow.ShowCapture();

            _readyWindow.Closed += (s, e) =>
            {
                _readyWindow = new CaptureWindow2();
                _readyWindow.Show();
            };
        }

        private void ShowCapture()
        {
            fastCapturer.DoFastCapture();
            UpdateSelfPosition();
            if (!System.Diagnostics.Debugger.IsAttached)
                this.Topmost = true;
            Interop.USER32.SetForegroundWindow(this.Handle);
        }

        private void CaptureWindow2_SourceInitialized(object sender, EventArgs e)
        {
            this.Handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        }

        private void UpdateSelfPosition()
        {
            var primary = ScreenTools.Screens.First().Bounds;
            var virt = ScreenTools.VirtualScreen.Bounds;
            // WPF makes some fairly inconvenient DPI conversions to Left and Top which have also changed between NET 4.5 and 4.8; just use WinAPI instead of de-converting them
            Interop.USER32.SetWindowPos(this.Handle, 0, -primary.Left, -primary.Top, virt.Width, virt.Height, Interop.SWP.SHOWWINDOW);
        }

        private void CloseExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            this.Close();
        }
    }
}

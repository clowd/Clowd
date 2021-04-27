using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using Clowd.Interop;
using Clowd.Interop.DwmApi;
using ScreenVersusWpf;

namespace Clowd.UI
{
    public class InteropWindow : Window
    {
        public IntPtr Handle
        {
            get
            {
                EnsureHandle();
                return _handle;
            }
        }

        public ScreenRect? ScreenPosition
        {
            get
            {
                if (SourceCreated)
                {
                    USER32.GetWindowRect(_handle, out var rect);
                    return new ScreenRect(rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top);
                }
                else
                {
                    return _screenPosition;
                }
            }
            set
            {
                _screenPosition = value;
                SetWindowPosition();
            }
        }

        public bool TransitionsDisabled
        {
            get => _transitionsDisabled ?? false;
            set
            {
                _transitionsDisabled = value;
                SetTransitionsDisabled();
            }
        }

        public bool SourceCreated { get; private set; }

        private bool? _transitionsDisabled;
        private ScreenRect? _screenPosition;
        private IntPtr _handle;

        public InteropWindow()
        {
            this.SourceInitialized += InteropWindow_SourceInitialized;
            this.SnapsToDevicePixels = true;
        }

        private void InteropWindow_SourceInitialized(object sender, EventArgs e)
        {
            var interop = new WindowInteropHelper(this);
            _handle = interop.Handle;
            SourceCreated = true;
            SetWindowPosition();
            SetTransitionsDisabled();
        }

        public void SetHwndOwner(IntPtr owner)
        {
            WindowInteropHelper helper = new WindowInteropHelper(this);
            helper.Owner = owner;
        }

        public void EnsureHandle()
        {
            if (SourceCreated) return;
            var interop = new WindowInteropHelper(this);
            interop.EnsureHandle();
        }

        private void SetWindowPosition()
        {
            if (_screenPosition.HasValue && SourceCreated)
            {
                var rect = _screenPosition.Value;
                var swp = (this.Topmost && !Debugger.IsAttached) ? SWP_HWND.HWND_TOPMOST : SWP_HWND.HWND_TOP;
                USER32.SetWindowPos(_handle, swp, rect.Left, rect.Top, rect.Width, rect.Height, SWP.NOACTIVATE);
            }
        }

        private unsafe void SetTransitionsDisabled()
        {
            if (_transitionsDisabled.HasValue && SourceCreated)
            {
                int disabled = _transitionsDisabled == true ? 1 : 0;
                DWMAPI.DwmSetWindowAttribute(_handle, DWMWINDOWATTRIBUTE.DWMWA_TRANSITIONS_FORCEDISABLED, &disabled, sizeof(int));
            }
        }
    }
}

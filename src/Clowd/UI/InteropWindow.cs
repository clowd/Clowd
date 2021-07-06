using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using Clowd.PlatformUtil;
using Clowd.PlatformUtil.Windows;

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

        public ScreenRect ScreenPosition
        {
            get
            {
                if (SourceCreated)
                {
                    return PlatformWindow.WindowBounds;
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

        public bool NeverActivate
        {
            get => _neverActivate ?? false;
            set
            {
                _neverActivate = value;
                SetNeverActivate();
            }
        }

        public bool SourceCreated { get; private set; }

        private bool? _neverActivate;
        private bool? _transitionsDisabled;
        private ScreenRect _screenPosition;
        private IntPtr _handle;
        public User32Window PlatformWindow => User32Window.FromHandle(Handle);

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
            SetNeverActivate();

            HwndSource source = HwndSource.FromHwnd(_handle);
            source.AddHook(new HwndSourceHook(WndProc));
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
            var rect = _screenPosition;
            if (rect != null && SourceCreated)
            {
                PlatformWindow.WindowBounds = _screenPosition;
            }
        }

        private void SetTransitionsDisabled()
        {
            if (_transitionsDisabled.HasValue)
                PlatformWindow.DwmSetTransitionsDisabled(_transitionsDisabled == true);
        }

        private void SetNeverActivate()
        {
            if (_neverActivate.HasValue)
                PlatformWindow.SetNeverActivateStyle(_neverActivate == true);
        }

        protected virtual IntPtr WndProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (NeverActivate && msg == 0x0021) // WM_MOUSEACTIVATE
            {
                // Does not activate the window, and does not discard the mouse message.
                handled = true;
                return (IntPtr)3; // MA_NOACTIVATE
            }

            return IntPtr.Zero;
        }
    }
}

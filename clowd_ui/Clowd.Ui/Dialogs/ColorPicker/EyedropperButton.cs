using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Clowd.UI.Controls;
using Clowd.Util;

namespace Clowd.UI.Dialogs.ColorPicker
{
    /// <summary>
    /// Press-and-drag eyedropper. Holding the button and dragging samples whatever is under the
    /// pointer — anywhere on the desktop, not just over Clowd — raising <see cref="Preview"/> as
    /// it moves and <see cref="Picked"/> on release. Releasing without ever leaving the button
    /// (a plain click) is treated as a canceled drag, since the pixel under the button is the
    /// button itself.
    /// </summary>
    /// <remarks>
    /// Pointer capture is what makes this work past the window edge: Avalonia maps it to Win32
    /// SetCapture, so moves outside the owning window (or popup) keep arriving here.
    ///
    /// Hidden entirely where <see cref="ScreenColorReader"/> cannot sample the screen at all
    /// (Linux), but only disabled — with a tooltip saying why — when the platform could sample and
    /// macOS Screen Recording permission is what's missing. That's a state the user can fix, and a
    /// vanished button reads as a feature Clowd doesn't have.
    /// </remarks>
    public class EyedropperButton : ToolButton
    {
        /// <summary>Fires when a drag begins, before any sample — the caller's cue to snapshot
        /// whatever <see cref="Canceled"/> would have to restore.</summary>
        public event Action Started;

        /// <summary>Fires continuously while dragging, for live preview. The argument is the
        /// color under the pointer.</summary>
        public event Action<Color> Preview;

        /// <summary>Fires once, on release over a valid pixel, with the color to commit.</summary>
        public event Action<Color> Picked;

        /// <summary>Fires when a drag ends without a pick (released on the button, or off-screen),
        /// so the caller can undo whatever <see cref="Preview"/> applied.</summary>
        public event Action Canceled;

        private bool _dragging;

        private bool _leftButton;

        private Color? _lastSampled;

        public EyedropperButton()
        {
            Cursor = new Cursor(StandardCursorType.Cross);

            // no screen sampling on this platform — an eyedropper that cannot sample is worse
            // than no eyedropper, so take it out of the layout entirely
            if (!ScreenColorReader.IsSupported)
                IsVisible = false;

            // permission can be granted while a picker is open, and this control is created fresh
            // each time a dialog opens, so re-check on the way into the tree rather than only here.
            AttachedToVisualTree += (s, e) => ApplyPermissionState();
            ApplyPermissionState();
        }

        /// <summary>Grays the button out, with the reason as its tooltip, while macOS Screen
        /// Recording is missing.</summary>
        private void ApplyPermissionState()
        {
            if (!ScreenColorReader.IsSupported)
                return;

            var permitted = ScreenColorReader.IsPermitted;
            IsEnabled = permitted;

            // ShowOnDisabled is off by default, and the tooltip is the only thing explaining why the
            // button is grayed out — without this the disabled state has no explanation at all.
            ToolTip.SetShowOnDisabled(this, true);
            ToolTip.SetTip(this, permitted
                ? null
                : "Clowd needs Screen Recording permission to pick a color from the screen. "
                  + "Grant it under Settings → General → Permissions.");
        }

        protected override Type StyleKeyOverride => typeof(ToolButton);

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);

            if (!ScreenColorReader.IsAvailable)
                return;

            _dragging = true;
            _leftButton = false;
            _lastSampled = null;
            e.Pointer.Capture(this);
            e.Handled = true;
            Started?.Invoke();
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);

            if (!_dragging || !Equals(e.Pointer.Captured, this))
                return;

            // ignore samples taken while still over the button: they would read the button's own
            // pixels, and a click that never moves off should not change the color at all
            var local = e.GetPosition(this);
            if (local.X >= 0 && local.Y >= 0 && local.X < Bounds.Width && local.Y < Bounds.Height)
                return;

            _leftButton = true;

            var color = Sample(e);
            if (color.HasValue)
            {
                _lastSampled = color;
                Preview?.Invoke(color.Value);
            }
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);

            if (!_dragging)
                return;

            _dragging = false;
            e.Pointer.Capture(null);
            e.Handled = true;

            // prefer a fresh sample at the release point; fall back to the last one seen while
            // dragging so a release over a non-sampleable spot still commits something sensible
            var color = _leftButton ? Sample(e) ?? _lastSampled : null;

            if (color.HasValue)
                Picked?.Invoke(color.Value);
            else
                Canceled?.Invoke();
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);

            if (!_dragging)
                return;

            _dragging = false;
            Canceled?.Invoke();
        }

        private Color? Sample(PointerEventArgs e)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null)
                return null;

            // PointToScreen applies the top level's scaling, so the result is the physical pixel
            // GDI addresses even on a mixed-DPI desktop
            var screenPoint = topLevel.PointToScreen(e.GetPosition(topLevel));
            var color = ScreenColorReader.GetColorAt(screenPoint);

            return color;
        }
    }
}

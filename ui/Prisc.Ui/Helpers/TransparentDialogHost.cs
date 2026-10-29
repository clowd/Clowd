using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Prisc.Interop;

namespace Prisc.UI.Helpers
{
    /// <summary>
    /// A window nobody sees, to own a native file picker that has no Prisc window to belong to —
    /// the Upload File hotkey or tray item in a tray-only session. A picker needs a top level's
    /// StorageProvider, and comes up in front only over a foreground owner, so this one takes the
    /// foreground as it opens (<see cref="WindowNativeExtensions.ForceForeground"/>). It is shown
    /// for the picker's lifetime and closed by its caller (<see cref="NiceDialog"/>).
    /// <para>
    /// One pixel, centred on the work area of the monitor the mouse is on (the picker centres on its
    /// owner), transparent, and click-through: WS_EX_TRANSPARENT plus HTTRANSPARENT on Windows,
    /// ignoresMouseEvents on macOS. Unlike the other overlays it may activate — taking the keyboard
    /// is its whole job — and it stays off the taskbar and Alt+Tab.
    /// </para>
    /// </summary>
    internal sealed class TransparentDialogHost : Window
    {
        public TransparentDialogHost()
        {
            Title = "Prisc";
            WindowDecorations = WindowDecorations.None;
            Background = Brushes.Transparent;
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
            ShowInTaskbar = false;
            CanResize = false;
            SizeToContent = SizeToContent.Manual;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Width = 1;
            Height = 1;

            // registered before Show() so they apply from the first frame; never repainted as
            // layered without the OpaqueLayered setup, which suits a window with nothing to draw
            WindowNativeExtensions.AddExStyles(this,
                WindowNativeExtensions.WS_EX_TRANSPARENT | WindowNativeExtensions.WS_EX_LAYERED | WindowNativeExtensions.WS_EX_TOOLWINDOW);
            WindowNativeExtensions.AddHitTestTransparentHook(this);

            var screen = (CursorPosition.TryGet(out var cursor) ? DesktopScreens.FromPoint(this, cursor) : null)
                ?? DesktopScreens.Primary(this);
            if (screen != null)
                Position = screen.WorkingArea.Center;

            Opened += OnOpened;
        }

        private void OnOpened(object sender, EventArgs e)
        {
            // click-through only: no OpaqueLayered (nothing to draw) and no menu-bar level
            WindowNativeExtensions.PrepareOverlay(this, OverlayFlags.ClickThrough);
            WindowNativeExtensions.ForceForeground(this);
            Activate();
        }
    }
}

using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Clowd.UI.Helpers
{
    /// <summary>
    /// macOS window-management key equivalents that AppKit would normally provide through the
    /// menu bar. Clowd is a tray app with no application menu of its own, so the standard
    /// Cmd+W "close window" never reaches our windows (issue #73) — every shell window opts in
    /// here instead. Safe to call on any OS: a no-op off macOS, so callers need no guard.
    /// </summary>
    internal static class MacWindowShortcuts
    {
        /// <summary>
        /// Closes <paramref name="window"/> on Cmd+W. Pass <paramref name="close"/> for windows
        /// whose cancel path carries a result (dialogs), so Cmd+W resolves exactly as Escape does;
        /// the default is a plain <see cref="Window.Close()"/>, matching the titlebar close button.
        /// </summary>
        /// <remarks>
        /// Tunnelling rather than a <see cref="Window.KeyBindings"/> entry: the gesture must fire
        /// from a focused TextBox too, and the editor's own tunnel handler bails out early on
        /// TextBox input.
        /// </remarks>
        public static void AddCloseShortcut(Window window, Action close = null)
        {
            if (!OperatingSystem.IsMacOS())
                return;

            window.AddHandler(InputElement.KeyDownEvent, (object sender, KeyEventArgs e) =>
            {
                // Meta alone — Cmd+Shift+W and friends are not the close gesture.
                if (e.Key != Key.W || e.KeyModifiers != KeyModifiers.Meta)
                    return;

                e.Handled = true;
                if (close != null)
                    close();
                else
                    window.Close();
            }, RoutingStrategies.Tunnel);
        }
    }
}

using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Clowd.Config;

namespace Clowd.UI.Helpers
{
    /// <summary>
    /// What a top level is, as far as hiding it from screen capture goes. Each kind is hidden from
    /// one step of <see cref="HideFromCapture"/> upward (<see cref="CaptureExclusion.IsExcluded"/>).
    /// </summary>
    public enum CaptureRole
    {
        /// <summary>An ordinary window: the editors, the main window, dialogs, menus. Hidden only by
        /// "All windows". What every window and popup is unless it says otherwise.</summary>
        Window,

        /// <summary>A floating button panel (every <c>FloatingTrayWindow</c>). Hidden from
        /// "Toolbars" up.</summary>
        Toolbar,

        /// <summary>Clowd's own tooltip chips and cards, and the frame around a recorded or shared
        /// region (BorderWindow and the share-region resize overlay that stands in for it). Hidden
        /// from "Tooltips" up.</summary>
        Tooltip,

        /// <summary>Content that is meant to be captured: the draw-on-screen ink. Never hidden, or
        /// "All windows" would take the user's annotations out of the very recording they are
        /// drawing them for.</summary>
        Content,
    }

    /// <summary>
    /// Keeps Clowd's own windows out of screen capture according to
    /// <see cref="SettingsGeneral.HideFromScreenCapture"/>. Installed once for the whole app: every
    /// <see cref="Window"/> and every popup (tooltips, menus, flyouts) is picked up by class handlers
    /// as it opens, so no window decides for itself whether it is hidden — it only declares what it
    /// is, with <see cref="RoleProperty"/>, and a change of setting is applied live to everything
    /// open.
    /// </summary>
    /// <remarks>
    /// A popup's role is the one set on the popup itself (StripToolTips does), else the one on its
    /// content (the tooltip themes set it on the ToolTip), else that of the window it opened from:
    /// a floating tray's menu hides along with the tray. Overlay-layer popups have no top level of
    /// their own and simply follow the window they are drawn in.
    /// </remarks>
    public static class CaptureExclusion
    {
        public static readonly AttachedProperty<CaptureRole?> RoleProperty =
            AvaloniaProperty.RegisterAttached<Control, CaptureRole?>("Role", typeof(CaptureExclusion));

        public static void SetRole(Control control, CaptureRole? value) => control.SetValue(RoleProperty, value);

        public static CaptureRole? GetRole(Control control) => control.GetValue(RoleProperty);

        private sealed class Entry
        {
            public CaptureRole Role;

            // what was last applied to the native window; a fresh one is not excluded
            public bool Excluded;
        }

        // every open top level (windows and popup roots), so a change of setting reaches them all
        private static readonly Dictionary<TopLevel, Entry> _open = new();

        // the root each open popup is showing in, remembered because a closed popup has let go of it
        private static readonly Dictionary<Popup, TopLevel> _popupRoots = new();

        private static bool _installed;

        /// <summary>Call once at startup, after the settings are loaded. Idempotent.</summary>
        public static void Install()
        {
            if (_installed)
                return;

            _installed = true;

            // WindowOpened is raised at the start of Show(), before any of the window's own Opened
            // handlers have run their native setup (layered styles, levels); the affinity goes on
            // after them, at the end of Show, as it did when each window applied it itself.
            Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) =>
            {
                window.Opened -= OnWindowOpened;
                window.Opened += OnWindowOpened;
            });
            Window.WindowClosedEvent.AddClassHandler<Window>((window, _) => _open.Remove(window));

            // registered after Popup's own IsOpen handler (its static constructor runs on first
            // touch of the property, before this line), so the popup's host is already shown here
            Popup.IsOpenProperty.Changed.AddClassHandler<Popup>(OnPopupIsOpenChanged);

            SettingsRoot.Current.General.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(SettingsGeneral.HideFromScreenCapture))
                    Dispatcher.UIThread.Post(ApplyAll);
            };
        }

        /// <summary>Whether a top level of the given role is hidden under the given setting. The
        /// setting's steps are cumulative: each one hides everything the one before it did.</summary>
        public static bool IsExcluded(CaptureRole role, HideFromCapture setting) => role switch
        {
            CaptureRole.Tooltip => setting >= HideFromCapture.Tooltips,
            CaptureRole.Toolbar => setting >= HideFromCapture.Toolbars,
            CaptureRole.Window => setting >= HideFromCapture.AllWindows,
            _ => false,
        };

        private static void OnWindowOpened(object sender, EventArgs e)
        {
            var window = (Window)sender;
            window.Opened -= OnWindowOpened;
            Track(window, GetRole(window) ?? CaptureRole.Window);
        }

        private static void OnPopupIsOpenChanged(Popup popup, AvaloniaPropertyChangedEventArgs e)
        {
            if (_popupRoots.Remove(popup, out var previous))
                _open.Remove(previous);

            if (!e.GetNewValue<bool>())
                return;

            // a popup asked to open before its target was in a window opens later, when it attaches
            if (TopLevel.GetTopLevel(popup.Child) == null)
            {
                popup.Opened -= OnPopupOpenedLate;
                popup.Opened += OnPopupOpenedLate;
                return;
            }

            TrackPopup(popup);
        }

        private static void OnPopupOpenedLate(object sender, EventArgs e)
        {
            var popup = (Popup)sender;
            popup.Opened -= OnPopupOpenedLate;
            TrackPopup(popup);
        }

        private static void TrackPopup(Popup popup)
        {
            // the popup's host is not public, but its child is parented to it while it is open; an
            // overlay-layer host is the window itself, which is already covered
            if (TopLevel.GetTopLevel(popup.Child) is not PopupRoot root)
                return;

            _popupRoots[popup] = root;
            Track(root, ResolvePopupRole(popup));
        }

        private static CaptureRole ResolvePopupRole(Popup popup)
        {
            if (GetRole(popup) is { } own)
                return own;

            if (popup.Child is Control child && GetRole(child) is { } content)
                return content;

            // the control the popup opened from, found the way Popup.Open finds it
            var owner = popup.PlacementTarget ?? popup.FindLogicalAncestorOfType<Control>();
            var role = TopLevel.GetTopLevel(owner) switch
            {
                { } top when _open.TryGetValue(top, out var entry) => entry.Role,
                Window window => GetRole(window) ?? CaptureRole.Window,
                _ => CaptureRole.Window,
            };

            // a menu off the ink is chrome, not ink
            return role == CaptureRole.Content ? CaptureRole.Window : role;
        }

        private static void Track(TopLevel topLevel, CaptureRole role)
        {
            if (!_open.TryGetValue(topLevel, out var entry))
                _open[topLevel] = entry = new Entry();

            entry.Role = role;
            Apply(topLevel, entry);
        }

        private static void ApplyAll()
        {
            foreach (var (topLevel, entry) in _open)
                Apply(topLevel, entry);
        }

        private static void Apply(TopLevel topLevel, Entry entry)
        {
            var setting = SettingsRoot.Current?.General.HideFromScreenCapture ?? HideFromCapture.Tooltips;
            var excluded = IsExcluded(entry.Role, setting);
            if (excluded == entry.Excluded)
                return;

            entry.Excluded = excluded;
            WindowNativeExtensions.SetExcludedFromScreenCapture(topLevel, excluded);
        }
    }
}

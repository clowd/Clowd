using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Prisc.UI.Controls
{
    /// <summary>
    /// The flyout that renames a session — what the Recent page lists it as and what both editors
    /// show in their top bar. Shared by the image editor, the video editor and the Recent page's
    /// rows: put one in the tree and call <see cref="Open"/> from the rename button's Click.
    ///
    /// Save writes <see cref="SessionInfo.Name"/> straight onto the session, which persists it and
    /// raises PropertyChanged, so every window showing the session follows without being told.
    /// </summary>
    public partial class RenameSessionPopup : UserControl
    {
        private SessionInfo _session;
        private Control _target;

        public RenameSessionPopup()
        {
            InitializeComponent();

            btnCancel.Click += (_, _) => Close();
            btnSave.Click += (_, _) => Save();

            // posted: on open the popup root is still laying out, so an immediate Focus() is
            // dropped on the way in (the measure units flyout does the same)
            popup.Opened += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                if (!popup.IsOpen)
                    return;
                txtName.Focus();
                txtName.SelectAll();
            }, DispatcherPriority.Input);

            // covers every close path, light dismiss included
            popup.Closed += (_, _) =>
            {
                _session = null;
                if (_target != null && _target.IsEffectivelyVisible)
                    _target.Focus(NavigationMethod.Tab);
                else
                    FallbackFocus?.Focus();
                _target = null;
            };

            // The popup lives in its own PopupRoot with its own focus scope, so the window's key
            // handlers never see keys pressed in it. Hook the root itself (it only exists while
            // the popup is open, hence on attach) so Enter and Escape work whichever child has focus.
            TopLevel keyRoot = null;
            popupRoot.AttachedToVisualTree += (_, _) =>
            {
                keyRoot = TopLevel.GetTopLevel(popupRoot);
                keyRoot?.AddHandler(KeyDownEvent, Root_KeyDown, RoutingStrategies.Tunnel);
            };
            popupRoot.DetachedFromVisualTree += (_, _) =>
            {
                keyRoot?.RemoveHandler(KeyDownEvent, Root_KeyDown);
                keyRoot = null;
            };
        }

        /// <summary>Where the keyboard goes when the flyout closes and the button that opened it
        /// has gone with it (a Recent row's hover button, once the pointer has left the row).</summary>
        public Control FallbackFocus { get; set; }

        public bool IsOpen => popup.IsOpen;

        /// <summary>Shows the flyout under <paramref name="target"/> (the rename button), seeded
        /// with <paramref name="session"/>'s current name.</summary>
        public void Open(SessionInfo session, Control target)
        {
            if (session == null)
                return;

            _session = session;
            _target = target;
            txtName.Text = session.Name ?? "";
            txtName.Classes.Remove("error");

            popup.PlacementTarget = target;
            popup.IsOpen = true;
        }

        public void Close() => popup.IsOpen = false;

        /// <summary>Enter saves and Escape cancels. Public so a host whose window still holds the
        /// keyboard while the flyout is open (focus is posted in) can route the two keys here
        /// instead of acting on them itself; returns whether the key was one of them.</summary>
        public bool HandleKey(KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                Save();
            else if (e.Key == Key.Escape)
                Close();
            else
                return false;
            e.Handled = true;
            return true;
        }

        private void Root_KeyDown(object sender, KeyEventArgs e) => HandleKey(e);

        private void Save()
        {
            if (_session == null)
            {
                Close();
                return;
            }

            // a session always has a name to list it by, so an empty one is refused rather
            // than saved; whitespace inside the name is the user's to keep
            var name = txtName.Text?.Trim();
            if (String.IsNullOrEmpty(name))
            {
                txtName.Classes.Add("error");
                txtName.Focus();
                return;
            }

            _session.Name = name;
            Close();
        }
    }
}

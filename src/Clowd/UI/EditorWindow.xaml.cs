using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Clowd.Config;
using Clowd.PlatformUtil;
using Vanara.PInvoke;

namespace Clowd.UI
{
    public partial class EditorWindow : SystemThemedWindow
    {
        private SessionInfo _info;

        private EditorWindow(SessionInfo info)
        {
            _info = info;
            InitializeComponent();
            Closing += EditorWindow_Closing;
            Content = new ImageEditorPage(info);
            Loaded += (_, _) => UpdateSessionInfo();
            Deactivated += (_, _) => UpdateSessionInfo();
            Activated += (_, _) => UpdateSessionInfo();
        }

        private void UpdateSessionInfo()
        {
            if (_info != null)
            {
                _info.OpenEditor = new SessionOpenEditor()
                {
                    IsTopMost = Topmost,
                    Position = ScreenPosition,
                    VirtualDesktopId = PlatformWindow?.VirtualDesktopId,
                };
            }
        }

        private void EditorWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _info.OpenEditor = null;
            _info = null;
        }

        public static void ShowSession(SessionInfo session)
        {
            var openWnd = App.Current.Windows.OfType<EditorWindow>().FirstOrDefault(f => f._info == session);
            if (openWnd != null)
            {
                openWnd.PlatformWindow.Activate();
            }
            else
            {
                var wnd = new EditorWindow(session);
                if (session.OpenEditor != null)
                {
                    wnd.EnsureHandle();
                    wnd.WindowStartupLocation = WindowStartupLocation.Manual;
                    wnd.Topmost = session.OpenEditor.IsTopMost;
                    wnd.ScreenPosition = session.OpenEditor.Position;
                    if (session.OpenEditor.VirtualDesktopId != null)
                        wnd.PlatformWindow.MoveToDesktop(session.OpenEditor.VirtualDesktopId.Value);
                }
                else
                {
                    // do something, like size the window to the capture
                }
                
                wnd.Show();
                wnd.PlatformWindow.Activate();
            }
        }

        public static void ShowAllPreviouslyActiveSessions()
        {
            var sessions = SessionManager.Current.Sessions
                .Where(s => s.OpenEditor != null).ToArray();

            foreach (var g in sessions)
            {
                ShowSession(g);
            }
        }
    }
}

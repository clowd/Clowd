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
        public ImageEditorPage EditorPage { get; }

        private SessionInfo _info;

        private EditorWindow(SessionInfo info)
        {
            _info = info;
            InitializeComponent();
            Closing += EditorWindow_Closing;
            Content = EditorPage = new ImageEditorPage(info);
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
                wnd.EnsureHandle();
                wnd.WindowStartupLocation = WindowStartupLocation.Manual;

                if (session.OpenEditor != null)
                {
                    // this session was not closed properly, restore it to it's previous location
                    wnd.Topmost = session.OpenEditor.IsTopMost;
                    wnd.ShowActivated = false;
                    if (session.OpenEditor.VirtualDesktopId != null)
                        wnd.PlatformWindow.MoveToDesktop(session.OpenEditor.VirtualDesktopId.Value);
                    wnd.ScreenPosition = session.OpenEditor.Position;
                    wnd.Show();
                }
                else
                {
                    // this is a brand new session. we'll show it on top of the captured area.
                    var screen = Platform.Current.GetScreenFromRect(session.CroppedRect);
                    var dpi = screen.ToDpiContext();

                    var logicalImageSize = dpi.ToWorldSize(session.CroppedRect.Size);

                    // add 30 because of default toolbar size. 
                    var padding = SettingsRoot.Current.Editor.StartupPadding;
                    var requiredSize = new Size(logicalImageSize.Width + 30 + padding, logicalImageSize.Height + 30 + padding);

                    var page = wnd.EditorPage;
                    page.Measure(requiredSize);

                    var rect = new ScreenRect(
                        session.CroppedRect.X - dpi.ToScreenWH(page.ToolBar.DesiredSize.Width) - padding,
                        session.CroppedRect.Y - dpi.ToScreenWH(page.PropertiesBar.DesiredSize.Height) - padding,
                        session.CroppedRect.Width + dpi.ToScreenWH(page.ToolBar.DesiredSize.Width) + padding * 2,
                        session.CroppedRect.Height + dpi.ToScreenWH(page.PropertiesBar.DesiredSize.Height) + padding * 2);

                    var wndRect = wnd.PlatformWindow.GetWindowRectFromIdealClientRect(rect);
                    wnd.PlatformWindow.WindowBounds = wndRect.Intersect(screen.WorkingArea);

                    wnd.Show();
                    wnd.PlatformWindow.Activate();
                }
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

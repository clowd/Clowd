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
            if (session == null)
            {
                session = SessionManager.Current.CreateNewSession();
                var wnd = new EditorWindow(session);
                wnd.Show();
                wnd.PlatformWindow.Activate();
                return;
            }
            
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
                    var workArea = screen.WorkingArea;
                    var dpi = screen.ToDpiContext();

                    // adjust working area to account for the invisible resizing border around the window
                    var resizePadding = (int)(SystemParameters.ResizeFrameVerticalBorderWidth + SystemParameters.FixedFrameVerticalBorderWidth);
                    workArea = new ScreenRect(
                        workArea.Left - resizePadding,
                        workArea.Y,
                        workArea.Width + (resizePadding * 2),
                        workArea.Height + resizePadding);

                    // calculate needed client rect; add 30 because of default toolbar size.
                    var logicalImageSize = dpi.ToWorldSize(session.CroppedRect.Size);
                    var padding = SettingsRoot.Current.Editor.StartupPadding;
                    var requiredSize = new Size(logicalImageSize.Width + 30 + padding, logicalImageSize.Height + 30 + padding);

                    // measure the page to see if any of the tool bars wrap
                    var page = wnd.EditorPage;
                    page.Measure(requiredSize);

                    var rect = new ScreenRect(
                        session.CroppedRect.X - dpi.ToScreenWH(page.ToolBar.DesiredSize.Width) - padding,
                        session.CroppedRect.Y - dpi.ToScreenWH(page.PropertiesBar.DesiredSize.Height) - padding,
                        session.CroppedRect.Width + dpi.ToScreenWH(page.ToolBar.DesiredSize.Width) + padding * 2,
                        session.CroppedRect.Height + dpi.ToScreenWH(page.PropertiesBar.DesiredSize.Height) + padding * 2);

                    // this is the 'ideal' rect that places the window precisely on top of the captured area,
                    // but part of the window may be outside the monitor
                    var idealRect = wnd.PlatformWindow.GetWindowRectFromIdealClientRect(rect);

                    // we shuffle the ideal rect around each edge if it is off screen to try and 
                    // achieve a window location that can show with 100% zoom.
                    if (idealRect.Left < workArea.Left) idealRect = idealRect.Translate(workArea.Left - idealRect.Left, 0);
                    if (idealRect.Top < workArea.Top) idealRect = idealRect.Translate(0, workArea.Top - idealRect.Top);
                    if (idealRect.Right > workArea.Right) idealRect = idealRect.Translate(workArea.Right - idealRect.Right, 0);
                    if (idealRect.Bottom > workArea.Bottom) idealRect = idealRect.Translate(0, workArea.Bottom - idealRect.Bottom);

                    // finally intersect with screen to crop if the image really can't fit.
                    wnd.PlatformWindow.WindowBounds = idealRect.Intersect(workArea);

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

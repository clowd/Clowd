using System;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Clowd.Config;
using Clowd.PlatformUtil;

namespace Clowd.UI
{
    public partial class EditorWindow : SystemThemedWindow
    {
        public DateTime LastTouched { get; private set; } = DateTime.Now;

        public string WindowId { get; } = Guid.NewGuid().ToString().ToLower();

        public EditorWindow()
        {
            InitializeComponent();

            //var b = new Binding(nameof(SettingsEditor.TabsEnabled));
            //b.Source = SettingsRoot.Current.Editor;
            //b.Mode = BindingMode.TwoWay;
            //TabView.SetBinding(TabablzControl.IsHeaderPanelVisibleProperty, b);

            //TabView.NewItemFactory = () =>
            //{
            //    return GetTabFromSession(SessionManager.Current.CreateNewSession());
            //};


            //TabView.ItemsChanged += TabView_ItemsChanged;
            //TabView.ClosingItemCallback = TabClosing;
            Closing += EditorWindow_Closing;
        }

        private void TabView_ItemsChanged(object sender, EventArgs e)
        {
            // loop through my items and make sure their WindowId is up to date
            // items can move from window to window
            foreach (var t in TabView.Items)
            {
                var session = GetSessionFromTab(t);
                session.ActiveWindowId = WindowId;
            }
        }

        private void EditorWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // reset all tab sessions window id to null 
            foreach (var t in TabView.Items)
            {
                var session = GetSessionFromTab(t);
                session.ActiveWindowId = null;
            }
        }

        //private void TabClosing(ItemActionCallbackArgs<TabablzControl> args)
        //{
        //    // reset session window id to null for closing tab
        //    var session = GetSessionFromTab(args.DragablzItem);
        //    session.ActiveWindowId = null;
        //}

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            LastTouched = DateTime.Now;
        }

        protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
        {
            base.OnPreviewMouseDown(e);
            LastTouched = DateTime.Now;
        }

        private static SessionInfo GetSessionFromTab(object tabObj)
        {
            //if (tabObj is DragablzItem drag)
            //    return GetSessionFromTab(drag.Content);

            if (tabObj is TabItem tab)
                return GetSessionFromTab(tab.DataContext);

            if (tabObj is SessionInfo session)
                return session;

            throw new InvalidOperationException($"Unable to convert object of type '{tabObj.GetType()}' to SessionInfo");
        }

        private TabItem GetTabFromSession(SessionInfo info)
        {
            if (info.ActiveWindowId != null)
                throw new InvalidOperationException("Document can only be open in one window at a time");

            var newItem = new TabItem();
            newItem.DataContext = info;

            // bind icon
            //var icon = new SymbolIcon();
            //icon.SetBinding(SymbolIcon.SymbolProperty, new Binding(nameof(SessionInfo.Icon)) { Source = info });
            //TabItemHelper.SetIcon(newItem, icon);

            // bind tab name
            newItem.SetBinding(TabItem.HeaderProperty, new Binding(nameof(SessionInfo.Name)));

            newItem.Content = new ImageEditorPage(info);
            return newItem;
        }

        public void AddSession(SessionInfo session)
        {
            var newItem = GetTabFromSession(session);
            TabView.Items.Add(newItem);
            TabView.SelectedItem = newItem;
            this.PlatformWindow.Activate();
        }

        public static void ShowAllPreviouslyActiveSessions()
        {
            var sessions = SessionManager.Current.Sessions
                .Where(s => !String.IsNullOrWhiteSpace(s.ActiveWindowId))
                .GroupBy(s => s.ActiveWindowId);

            foreach (var g in sessions)
            {
                var ew = new EditorWindow();
                foreach (var s in g)
                {
                    s.ActiveWindowId = null;
                    ew.AddSession(s);
                }
                ew.Show();
            }
        }

        public static void ShowSession(SessionInfo session)
        {
            if (session == null)
                throw new ArgumentNullException(nameof(session));

            // if the session is already open somewhere, just switch to that window / tab
            if (session.ActiveWindowId != null)
            {
                var openWnd = App.Current.Windows.OfType<EditorWindow>().FirstOrDefault(f => f.WindowId == session.ActiveWindowId);
                if (openWnd != null)
                {
                    openWnd.PlatformWindow.Activate();
                    var tab = openWnd.TabView.Items.OfType<TabItem>().FirstOrDefault(t => GetSessionFromTab(t) == session);
                    if (tab != null)
                        openWnd.TabView.SelectedItem = tab;
                    return;
                }
                else
                {
                    session.ActiveWindowId = null;
                }
            }

            // look for a candidate window to open this tab in, or open a new window if one can't be found
            var query = from w in App.Current.Windows.OfType<EditorWindow>()
                        where w.PlatformWindow.IsCurrentVirtualDesktop
                        //where w.TabView.IsHeaderPanelVisible
                        orderby w.LastTouched descending
                        select w;

            var items = query.ToArray();

            var preferredScreen = session.CroppedRect == null ? Platform.Current.PrimaryScreen : Platform.Current.GetScreenFromRect(session.CroppedRect);
            var preferred = items.FirstOrDefault(w => Platform.Current.GetScreenFromRect(w.ScreenPosition) == preferredScreen);
            var any = items.FirstOrDefault();

            if (preferred != null)
            {
                preferred.AddSession(session);
            }
            else if (any != null)
            {
                any.AddSession(session);
            }
            else
            {
                // no suitable window exists, lets open a new window
                var ed = new EditorWindow();
                ed.AddSession(session);
                ed.Show();
            }
        }
    }
}

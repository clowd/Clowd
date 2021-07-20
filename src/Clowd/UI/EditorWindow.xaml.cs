using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Clowd.Config;
using Clowd.PlatformUtil;
using Clowd.UI.Helpers;
using Dragablz;
using ModernWpf.Controls;
using ModernWpf.Controls.Primitives;

namespace Clowd.UI
{
    public partial class EditorWindow : SystemThemedWindow
    {
        public DateTime LastTouched { get; private set; } = DateTime.Now;

        public EditorWindow()
        {
            InitializeComponent();

            var b = new Binding(nameof(EditorSettings.TabsEnabled));
            b.Source = ClowdSettings.Current.Editor;
            b.Mode = BindingMode.TwoWay;
            TabView.SetBinding(TabablzControl.IsHeaderPanelVisibleProperty, b);

            TabView.NewItemFactory = () =>
            {
                var newItem = new TabItem { Header = "Document" };
                TabItemHelper.SetIcon(newItem, new SymbolIcon(Symbol.Document));
                newItem.Content = new ImageEditorPage(SessionUtil.CreateNewSession());
                return newItem;
            };
        }

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

        public void AddSession(SessionInfo session)
        {
            var newItem = new TabItem { Header = "Capture" };
            TabItemHelper.SetIcon(newItem, new SymbolIcon(Symbol.Camera));
            newItem.Content = new ImageEditorPage(session);
            TabView.Items.Add(newItem);
            TabView.SelectedItem = newItem;
            this.PlatformWindow.Activate();
        }

        public static void ShowSession(SessionInfo session)
        {
            if (session == null)
                throw new ArgumentNullException(nameof(session));

            var preferredScreen = Platform.Current.GetScreenFromRect(session.SelectionRect);

            var query = from w in App.Current.Windows.OfType<EditorWindow>()
                        let p = w.GetPlatformWindow()
                        where p.IsCurrentVirtualDesktop
                        where w.TabView.IsHeaderPanelVisible
                        orderby w.LastTouched descending
                        select w;

            var items = query.ToArray();

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

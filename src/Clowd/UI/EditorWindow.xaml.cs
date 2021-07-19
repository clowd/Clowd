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
using Clowd.PlatformUtil;
using ModernWpf.Controls;
using ModernWpf.Controls.Primitives;

namespace Clowd.UI
{
    public partial class EditorWindow : SystemThemedWindow
    {
        private EditorWindow()
        {
            InitializeComponent();
            //TabView.NewItemFactory = () =>
            //{
            //    var newItem = new TabItem { Header = "New Document" };
            //    TabItemHelper.SetIcon(newItem, new SymbolIcon(Symbol.Document));
            //    return newItem;
            //};
        }

        public void AddSession(SessionInfo session)
        {
            var newItem = new TabItem { Header = "Capture" };
            TabItemHelper.SetIcon(newItem, new SymbolIcon(Symbol.Camera));
            newItem.Content = new ImageEditorPage(session);
            TabView.Items.Add(newItem);
        }

        public static void ShowSession(SessionInfo session)
        {
            if (session != null)
            {
                var preferredScreen = Platform.Current.GetScreenFromRect(session.SelectionRect);

                var query = from w in App.Current.Windows.OfType<EditorWindow>()
                            let p = w.GetPlatformWindow()
                            where p.IsCurrentVirtualDesktop
                            select w;

                var preferred = query.FirstOrDefault(w => Platform.Current.GetScreenFromRect(w.ScreenPosition) == preferredScreen);
                if (preferred != null)
                    preferred.AddSession(session);

                var any = query.FirstOrDefault();
                if (any != null)
                    any.AddSession(session);
            }

            // no suitable window exists, lets open a new window
            var ed = new EditorWindow();
            ed.AddSession(session);
            ed.Show();
        }
    }
}

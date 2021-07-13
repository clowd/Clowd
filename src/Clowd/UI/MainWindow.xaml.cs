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
using System.Windows.Navigation;
using System.Windows.Shapes;
using Clowd.Config;
using Clowd.UI.Config;
using ModernWpf.Controls;
using ModernWpf.Media.Animation;
using Page = ModernWpf.Controls.Page;

namespace Clowd.UI
{
    public partial class MainWindow : SystemThemedWindow
    {
        private Dictionary<string, FrameworkElement> _tagCache = new Dictionary<string, FrameworkElement>();

        public MainWindow()
        {
            InitializeComponent();

            // set DrillIn page transition
            var trans = new NavigationThemeTransition();
            trans.DefaultNavigationTransitionInfo = new DrillInNavigationTransitionInfo();
            var col = new TransitionCollection();
            col.Add(trans);
            ContentFrame.ContentTransitions = col;
        }

        private void NavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            var selectedItem = (NavigationViewItem)args.SelectedItem;
            sender.Header = selectedItem.Content as string;

            var tag = selectedItem.Tag as string;
            if (tag == null)
                return;

            if (_tagCache.TryGetValue(tag, out var panel))
            {
                ContentFrame.Navigate(panel);
            }
            else
            {
                var item = GetPanelForTag(tag);
                if (item != null)
                {
                    _tagCache[tag] = item;
                    ContentFrame.Navigate(item);
                }
            }
        }

        private FrameworkElement GetPanelForTag(string tag)
        {
            var settingsPage = typeof(ClowdSettings).GetProperties().FirstOrDefault(f => f.Name == tag);
            if (settingsPage != null)
                return new SettingsControlFactory(this, settingsPage.GetValue(ClowdSettings.Current)).GetSettingsPanel();

            return null;
        }
    }
}

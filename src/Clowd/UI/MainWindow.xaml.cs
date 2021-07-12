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
        public MainWindow()
        {
            InitializeComponent();
        }

        private void NavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            var selectedItem = (NavigationViewItem)args.SelectedItem;
            sender.Header = selectedItem.Content as string;

            var settingsPage = typeof(ClowdSettings).GetProperties().FirstOrDefault(f => f.Name == selectedItem.Tag as string);
            if (settingsPage != null)
                ContentFrame.Navigate(typeof(ModernSettingsPage), settingsPage.GetValue(ClowdSettings.Current), new DrillInNavigationTransitionInfo());
        }
    }

    public class ModernSettingsPage : Page
    {
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            this.Content = new SettingsControlFactory(e.ExtraData).GetSettingsPanel();
        }
    }
}

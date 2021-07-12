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
using ModernWpf.Controls;
using ModernWpf.Media.Animation;
using PropertyTools.Wpf;
using Page = ModernWpf.Controls.Page;

namespace Clowd.UI
{
    public partial class MainNavWindow : SystemThemedWindow
    {
        public MainNavWindow()
        {
            InitializeComponent();
        }

        private void NavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            var selectedItem = (NavigationViewItem)args.SelectedItem;
            sender.Header = selectedItem.Content as string;
            ContentFrame.Navigate(typeof(ModernSettingsPage), selectedItem.Tag, new DrillInNavigationTransitionInfo());
        }
    }

    public class ModernSettingsPage : Page
    {
        public ModernSettingsPage()
        {
            var prop = new PropertyGrid();
            prop.SelectedObject = App.Current.Settings.VideoSettings;
            prop.PropertyControlFactory = new AppSettingsControlFactory();
            prop.PropertyItemFactory = new AppSettingsItemFactory();
            prop.TabVisibility = TabVisibility.Collapsed;
            prop.EnumAsRadioButtonsLimit = 0;
            prop.Padding = new Thickness(24, 20, 24, 20);
            //prop.CategoryControlTemplate = new ControlTemplate();// new Label();
            //prop.CategoryHeaderTemplate = new DataTemplate();
            prop.CategoryControlType = CategoryControlType.GroupBox;
            prop.Template = (ControlTemplate)FindResource("PropertyGridSimplified");

            var wrap = new Border();
            //wrap.Margin = new Thickness(24, 20, 24, 20);
            wrap.Child = prop;

            Content = wrap;
        }
    }
}

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

namespace Clowd.Installer
{
    /// <summary>
    /// Interaction logic for ActionsPage.xaml
    /// </summary>
    public partial class ActionsPage : Page
    {
        public ActionsPage()
        {
            InitializeComponent();
        }

        private void Install_Click(object sender, RoutedEventArgs e)
        {
            NavigationService nav = NavigationService.GetNavigationService(this);
            nav?.Navigate(new LocationsPage());
        }

        private void Modify_Click(object sender, RoutedEventArgs e)
        {

        }

        private void Repair_Click(object sender, RoutedEventArgs e)
        {

        }

        private void Uninstall_Click(object sender, RoutedEventArgs e)
        {

        }
    }
}

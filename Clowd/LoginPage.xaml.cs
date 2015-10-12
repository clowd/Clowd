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

namespace Clowd
{
    /// <summary>
    /// Interaction logic for Home.xaml
    /// </summary>
    public partial class LoginPage : UserControl
    {
        public LoginPage()
        {
            InitializeComponent();
        }

        private void loginButton_Click(object sender, RoutedEventArgs e)
        {
            var parentWindow = Window.GetWindow(this);
            parentWindow.Content = new HomePage();
        }

        private void useAnon_Click(object sender, MouseButtonEventArgs e)
        {

        }

        private void register_Click(object sender, MouseButtonEventArgs e)
        {
            var cache = this.Content;
            WebBrowser wb = new WebBrowser();
            this.Content = wb;
            wb.Navigate("http://example.com");
        }
    }
}

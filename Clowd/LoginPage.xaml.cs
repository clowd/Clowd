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
            this.Loaded += LoginPage_Loaded;
        }

        private void LoginPage_Loaded(object sender, RoutedEventArgs e)
        {
            Keyboard.Focus(textUsername);
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

        private async void LoginExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            var window = Window.GetWindow(this);
            TemplatedWindow.SetContentToSpinner(window);
            AuthResult result;
            using (var details = new Credentials(textUsername.Text, textPassword.SecurePassword))
            {
                result = await UploadManager.Login(details);
            }

            if (result == AuthResult.Success)
            {
                TemplatedWindow.SetContent(window, new HomePage());
            }
            else if (result == AuthResult.InvalidCredentials)
            {
                this.invalidText.Text = "Invalid Username/Password";
                TemplatedWindow.SetContent(window, this);
                this.invalidIndicator.Visibility = Visibility.Visible;
                Keyboard.Focus(textUsername);
            }
            else if (result == AuthResult.NetworkError)
            {
                this.invalidText.Text = "Network Error";
                TemplatedWindow.SetContent(window, this);
                this.invalidIndicator.Visibility = Visibility.Visible;
                Keyboard.Focus(textUsername);
            }
        }
    }
}

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
        AuthResult? prevResult;
        public LoginPage()
        {
            InitializeComponent();
            this.Loaded += LoginPage_Loaded;
        }
        public LoginPage(AuthResult result, string username)
        {
            InitializeComponent();
            textUsername.Text = username;
            prevResult = result;
            this.Loaded += LoginPage_Loaded;
        }

        private void LoginPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (prevResult != null)
            {
                if (prevResult == AuthResult.InvalidCredentials)
                    ShowError("Invalid username or password.");
                else if (prevResult == AuthResult.NetworkError)
                    ShowError("Network error - try again later.");
            }
            RefocusKeyboard();
        }

        private void useAnon_Click(object sender, MouseButtonEventArgs e)
        {
            TemplatedWindow.SetContent(this, new HomePage());
            App.Current.Settings.Username = "anon";
            App.Current.FinishInit();
        }

        private void register_Click(object sender, MouseButtonEventArgs e)
        {
            //var cache = this.Content;
            //WebBrowser wb = new WebBrowser();
            //this.Content = wb;
            //wb.Navigate("http://example.com");
        }

        private async void LoginExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            if (String.IsNullOrWhiteSpace(textUsername.Text))
            {
                ShowError("Username can not be empty");
                return;
            }
            if (textPassword.SecurePassword.Length <= 0)
            {
                ShowError("Password can not be empty");
                return;
            }

            var window = Window.GetWindow(this);
            TemplatedWindow.SetContentToSpinner(window);
            AuthResult result;
            using (var details = new Credentials(textUsername.Text, textPassword.SecurePassword))
            {
                result = await UploadManager.Login(details);

                if (result == AuthResult.Success)
                {
                    TemplatedWindow.SetContent(window, new HomePage());
                    App.Current.Settings.Username = details.Username;
                    App.Current.Settings.PasswordHash = details.PasswordHash;
                    App.Current.FinishInit();
                }
                else if (result == AuthResult.InvalidCredentials)
                {
                    TemplatedWindow.SetContent(window, this);
                    ShowError("Invalid username or password.");
                }
                else if (result == AuthResult.NetworkError)
                {
                    TemplatedWindow.SetContent(window, this);
                    ShowError("Network error - try again later.");
                }
            }
        }

        private void ShowError(string message)
        {
            this.invalidIndicator.Visibility = Visibility.Hidden;
            this.invalidText.Text = message;
            this.invalidIndicator.Visibility = Visibility.Visible;
            RefocusKeyboard();
        }

        private void RefocusKeyboard()
        {
            if (String.IsNullOrEmpty(textUsername.Text))
                Keyboard.Focus(textUsername);
            else
                Keyboard.Focus(textPassword);
        }
    }
}

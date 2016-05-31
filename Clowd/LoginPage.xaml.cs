using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
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
using CS.Util.Extensions;
using MahApps.Metro.Controls;

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
            ValidateInput(false);
        }

        private void useAnon_Click(object sender, MouseButtonEventArgs e)
        {
            TemplatedWindow.SetContent(this, new HomePage());
            App.Current.Settings.Username = "anon";
            App.Current.FinishInit();
        }

        private void register_Click(object sender, MouseButtonEventArgs e)
        {
            if (stackInput.Expanded)
            {
                labelTitle.Text = "Login";
                loginButton.Content = "Login";
                labelRegister.Text = "Register an account";
                stackInput.Collapse();
            }
            else
            {
                textEmail.Text = "";
                textPasswordConfirm.Password = "";
                labelTitle.Text = "Register";
                loginButton.Content = "Register";
                labelRegister.Text = "I have an account";
                stackInput.Expand();
            }
        }

        private async void LoginExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            if (!ValidateInput(true))
                return;

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
        }

        private bool ValidateInput(bool showError)
        {
            var foo = new EmailAddressAttribute();
            if (String.IsNullOrWhiteSpace(textUsername.Text))
            {
                if (showError)
                    ShowError("Username can not be empty.");
                Keyboard.Focus(textUsername);
                return false;
            }
            if (stackInput.Expanded && String.IsNullOrWhiteSpace(textEmail.Text))
            {
                if (showError)
                    ShowError("Email can not be empty.");
                Keyboard.Focus(textEmail);
                return false;
            }
            if (stackInput.Expanded && !foo.IsValid(textEmail.Text))
            {
                if (showError)
                    ShowError("Email must be in a valid format.");
                Keyboard.Focus(textEmail);
                return false;
            }
            if (textPassword.SecurePassword.Length <= 0)
            {
                if (showError)
                    ShowError("Password can not be empty.");
                Keyboard.Focus(textPassword);
                return false;
            }
            if (stackInput.Expanded && textPasswordConfirm.SecurePassword.Length <= 0)
            {
                if (showError)
                    ShowError("Confirm Password can not be empty.");
                Keyboard.Focus(textPasswordConfirm);
                return false;
            }
            if (stackInput.Expanded && !textPassword.SecurePassword.UseSecurely(pass =>
                textPasswordConfirm.SecurePassword.UseSecurely(confirm => confirm == pass)))
            {
                if (showError)
                    ShowError("The two password entries must be the same.");
                Keyboard.Focus(textPasswordConfirm);
                return false;
            }
            return true;
        }
    }
}

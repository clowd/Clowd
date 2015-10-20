using MahApps.Metro.Controls;
using MahApps.Metro.Controls.Dialogs;
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
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Security;
using Clowd.Shared;

namespace Clowd
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    [PropertyChanged.ImplementPropertyChanged]
    public partial class MainWindow : MetroWindow
    {
        public ObservableCollection<UploadDTO> Uploads { get; set; }
        public Action LoginCallback { get; set; }

        private object loginContent = null;
        private StackPanel homeContent = null;
        private bool startLoggedIn = false;
        private string savedPassword = null;
        private AuthResult? appError = null;

        public MainWindow()
        {
            InitializeComponent();
            this.Loaded += MainWindow_Loaded;
            loginContent = transitioningContent.Content;
            homeContent = homeGrid;
            contentGrid.Children.Remove(homeContent);
            homeContent.Visibility = Visibility.Visible;
        }
        public MainWindow(bool loggedIn)
            :this()
        {
            startLoggedIn = loggedIn;
            if (startLoggedIn)
            {
                transitioningContent.Content = homeContent;
            }
        }
        //public MainWindow(string asd, bool test)
        //    :this()
        //{
        //    transitioningContent.Content = homeContent;
        //    var uploads = new List<UploadDTO>
        //    {
        //        new UploadDTO() { DisplayName = "v1.png", Enabled = true, Key = "v1", Password = "pass", Views = 101, Url = "localhost", UploadDate = DateTime.Now },
        //        new UploadDTO() { DisplayName = "v2.png", Enabled = true, Key = "v2", Password = "pass", Views = 101, Url = "localhost", UploadDate = DateTime.Now },
        //        new UploadDTO() { DisplayName = "v3.zip", Enabled = true, Key = "v3", Views = 101, ViewLimit = 200, Url = "localhost", UploadDate = DateTime.Now },
        //        new UploadDTO() { DisplayName = "v4.zip", Enabled = true, Key = "v4", Views = 101, ValidUntil = DateTime.Now.AddDays(1), Url = "localhost", UploadDate = DateTime.Now },
        //        new UploadDTO() { DisplayName = "v5.zip", Enabled = true, Key = "v5", Views = 101, Url = "localhost", UploadDate = DateTime.Now },
        //        new UploadDTO() { DisplayName = "v6.zip", Enabled = true, Key = "v6", Views = 101, Url = "localhost", UploadDate = DateTime.Now },
        //        new UploadDTO() { DisplayName = "v7.zip", Enabled = true, Key = "v7", Views = 101, Url = "localhost", UploadDate = DateTime.Now }
        //    };
        //    Uploads = new ObservableCollection<UploadDTO>(uploads);
        //}
        public MainWindow(Tuple<string, string> loginDetails, AuthResult error)
            :this()
        {
            if (loginDetails != null)
            {
                appError = error;
                textUsername.Text = loginDetails.Item1;
                textPassword.Password = "********";
                savedPassword = loginDetails.Item2;
            }
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (appError != null)
            {
               ShowLoginError(appError.Value);
            }
            else if (startLoggedIn)
            {
                var uploads = await UploadManager.MyUploads(0,20);
                Uploads = new ObservableCollection<UploadDTO>(uploads);
            }
        }
        private async void loginButton_Click(object sender, RoutedEventArgs e)
        {
            Style style = Application.Current.FindResource("CircleProgressRingStyle") as Style;
            Controls.ProgressRing pr = new Controls.ProgressRing();
            pr.IsActive = true;
            pr.Style = style;
            pr.Width = 100;
            pr.Height = 100;
            pr.HorizontalAlignment = HorizontalAlignment.Center;
            pr.VerticalAlignment = VerticalAlignment.Center;
            transitioningContent.Content = pr;

            //string pass;
            //if (textPassword.Password == "********" && savedPassword != null)
            //    pass = savedPassword;
            //else
            //    pass = Utilities.PasswordHelper.GetHashFromPassword(textPassword.Password);
            //var details = new Tuple<string, string>(textUsername.Text, pass);
            //var login = await UploadManager.Login(details);

            //if (login == AuthResult.Success)
            //{
            //    //Helpers.PasswordHelper.SaveUserAndHash(details.Item1, details.Item2);
            //    closeHintPanel.Visibility = Visibility.Visible;

            //    LoginCallback();
            //    LoginCallback = null;
            //    var uploads = await UploadManager.MyUploads();
            //    Uploads = new ObservableCollection<UploadDTO>(uploads);
            //    transitioningContent.Content = homeGrid;
            //}
            //else
            //{
            //    transitioningContent.Content = loginContent;
            //    ShowLoginError(login);
            //}
        }
        private async void ShowLoginError(AuthResult login)
        {
            var mySettings = new MetroDialogSettings()
            {
                AffirmativeButtonText = "OK",
                ColorScheme = MetroDialogColorScheme.Accented
            };

            if (login == AuthResult.InvalidCredentials)
            {
                MessageDialogResult result = await this.ShowMessageAsync("Login Failed", "The login details are incorrect.",
                    MessageDialogStyle.Affirmative, mySettings);
            }
            else if (login == AuthResult.NetworkError)
            {
                MessageDialogResult result = await this.ShowMessageAsync("Login Failed", "The internet seems to be taking a nap, try again later.",
                    MessageDialogStyle.Affirmative, mySettings);
            }
        }

        private void useAnon_Click(object sender, MouseButtonEventArgs e)
        {
            LoginCallback = null;
            LoginCallback();
            this.Close();
        }

        private void globalSettings_Click(object sender, RoutedEventArgs e)
        {
            var flyout = this.Flyouts.Items[0] as Flyout;
            flyout.IsOpen = !flyout.IsOpen;
        }

        private void Flyout_IsOpenChanged(object sender, RoutedEventArgs e)
        {
            var flyout = this.Flyouts.Items[0] as Flyout;
            if(flyout.IsOpen == false)
            {
                //flyout closed, save changes:
            }
        }

        private void uploadEdit_Click(object sender, MouseButtonEventArgs e)
        {

        }

        private void uploadOpen_Click(object sender, MouseButtonEventArgs e)
        {
            var dep = (DependencyObject)e.OriginalSource;
            // iteratively traverse the visual tree
            while ((dep != null) && !(dep is ListBoxItem))
            {
                dep = VisualTreeHelper.GetParent(dep);
            }
            var item = (dep as ListBoxItem);
            var upload = item.DataContext as UploadDTO;

            System.Diagnostics.Process.Start(upload.Url);
        }
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Clowd.UI.Helpers;

namespace Clowd.UI
{
    /// <summary>
    /// Interaction logic for HomePage.xaml
    /// </summary>
    public partial class HomePage : UserControl
    {
        public HomePage()
        {
            InitializeComponent();
        }

        private void Capture_Click(object sender, RoutedEventArgs e)
        {
            //var window = TemplatedWindow.GetWindow(this);
            //window.WindowState = WindowState.Minimized;
            //await Task.Delay(600);
            //var capture = CaptureWindow.ShowNew();
            //System.ComponentModel.CancelEventHandler close = null;
            //close = (s, evargs) =>
            //{
            //    window.WindowState = WindowState.Normal;
            //    capture.Closing -= close;
            //};
            //capture.Closing += close;
        }

        private void History_Click(object sender, RoutedEventArgs e)
        {

        }

        private void Upload_Click(object sender, RoutedEventArgs e)
        {
            App.Current.UploadFile(TemplatedWindow.GetWindow(this));
        }

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            TemplatedWindow.SetContent(this, new SettingsPage());
        }

        private void PasteExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            App.Current.Paste();
        }
    }
}

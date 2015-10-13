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
    /// Interaction logic for HomePage.xaml
    /// </summary>
    public partial class HomePage : UserControl
    {
        public HomePage()
        {
            InitializeComponent();
        }

        private async void Capture_Click(object sender, RoutedEventArgs e)
        {
            var window = TemplatedWindow.GetWindow(this);
            window.WindowState = WindowState.Minimized;
            Capture.CaptureWindow capture = new Capture.CaptureWindow();
            await Task.Delay(500);
            EventHandler close = null;
            close = (s, evargs) =>
            {
                window.WindowState = WindowState.Normal;
                capture.Closed -= close;
            };
            capture.Closed += close;
            capture.Show();
        }

        private void History_Click(object sender, RoutedEventArgs e)
        {

        }

        private void Upload_Click(object sender, RoutedEventArgs e)
        {

        }

        private void Paste_Click(object sender, RoutedEventArgs e)
        {

        }

        private void Settings_Click(object sender, RoutedEventArgs e)
        {

        }
    }
}

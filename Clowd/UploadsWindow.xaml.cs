using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
using System.Windows.Threading;

namespace Clowd
{
    [PropertyChanged.ImplementPropertyChanged]
    public partial class UploadsWindow : Window
    {
        public ObservableCollection<Controls.UploadProgressBar> Uploads { get; set; }

        DispatcherTimer mIdle;
        private const long cIdleSeconds = 10;

        public bool CloseTimerEnabled
        {
            get { return mIdle.IsEnabled; }
            set { mIdle.IsEnabled = value; }
        }

        public UploadsWindow()
        {
            this.Loaded += UploadsWindow_Loaded;
            this.SizeChanged += UploadsWindow_SizeChanged;
            Uploads = new ObservableCollection<Controls.UploadProgressBar>();
            InitializeComponent();

            UploadListbox.ItemsSource = Uploads;
            InputManager.Current.PreProcessInput += Idle_PreProcessInput;
            mIdle = new DispatcherTimer();
            mIdle.Interval = new TimeSpan(cIdleSeconds * 1000 * 10000);
            mIdle.Tick += Idle_Tick;
        }

        private void UploadsWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var desktopWorkingArea = System.Windows.SystemParameters.WorkArea;
            this.Top = desktopWorkingArea.Bottom - e.NewSize.Height;
        }

        private void UploadsWindow_Loaded(object sender, RoutedEventArgs e)
        {
            var desktopWorkingArea = System.Windows.SystemParameters.WorkArea;
            this.Left = desktopWorkingArea.Right - this.Width;
            this.Top = desktopWorkingArea.Bottom - this.Height;
        }

        void Idle_Tick(object sender, EventArgs e)
        {
            if (!this.IsMouseOver)
            {
                CloseTimerEnabled = false;
                this.Hide();
                Uploads.Where(up => up.ActionClicked).ToList().ForEach(up => Uploads.Remove(up));
            }
        }

        void Idle_PreProcessInput(object sender, PreProcessInputEventArgs e)
        {
            if (mIdle.IsEnabled)
            {
                //restart idle timer
                mIdle.IsEnabled = false;
                mIdle.IsEnabled = true;
            }
        }

        private void Border_MouseDown(object sender, MouseButtonEventArgs e)
        {
            var dep = (DependencyObject)e.OriginalSource;
            // iteratively traverse the visual tree
            while ((dep != null) && !(dep is Controls.UploadProgressBar))
            {
                dep = VisualTreeHelper.GetParent(dep);
            }
            var uploadBar = (dep as Controls.UploadProgressBar);

            if (!uploadBar.ActionAvailable) return;

            Clipboard.SetText(uploadBar.ActionLink);
            if (uploadBar.Progress >= 100)
            {
                uploadBar.ActionClicked = true;
                uploadBar.Foreground = Brushes.LightGray;
            }
            var otherUploads = Uploads.Where(bar => bar != uploadBar);
            if (!otherUploads.Any() && uploadBar.Progress >= 100)
            {
                Uploads.Remove(uploadBar);
                CloseTimerEnabled = false;
                this.Hide();
            }
            else
            {
                var uncompl = otherUploads.Any(up => up.Progress < 100);
                if (!uncompl)
                    CloseTimerEnabled = true;
            }
        }

        private void Hide_MouseDown(object sender, MouseButtonEventArgs e)
        {
            CloseTimerEnabled = false;
            this.Hide();
            Uploads.Where(up => up.ActionClicked).ToList().ForEach(up => Uploads.Remove(up));
        }
    }
}

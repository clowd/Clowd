using System.Windows;
using System.Windows.Controls;

namespace Clowd.UI.Controls
{
    public class UploadProgressBar : ListBoxItem
    {
        public bool ActionClicked { get; set; }
        public bool UploadFailed { get; set; }
        public double Progress
        {
            get { return (double)GetValue(ProgressProperty); }
            set { SetValue(ProgressProperty, value); }
        }

        public static readonly DependencyProperty ProgressProperty =
            DependencyProperty.Register("Progress", typeof(double), typeof(UploadProgressBar), new PropertyMetadata((double)4));

        public string CurrentSizeDisplay
        {
            get { return (string)GetValue(CurrentSizeDisplayProperty); }
            set { SetValue(CurrentSizeDisplayProperty, value); }
        }

        // Using a DependencyProperty as the backing store for CurrentSizeDisplay.  This enables animation, styling, binding, etc...
        public static readonly DependencyProperty CurrentSizeDisplayProperty =
            DependencyProperty.Register("CurrentSizeDisplay", typeof(string), typeof(UploadProgressBar), new PropertyMetadata("0 B"));

        public string DisplayText
        {
            get { return (string)GetValue(DisplayTextProperty); }
            set { SetValue(DisplayTextProperty, value); }
        }
        public static readonly DependencyProperty DisplayTextProperty =
            DependencyProperty.Register("DisplayText", typeof(string), typeof(UploadProgressBar), new PropertyMetadata(""));

        public bool ActionAvailable
        {
            get { return (bool)GetValue(ActionAvailableProperty); }
            set { SetValue(ActionAvailableProperty, value); }
        }

        // Using a DependencyProperty as the backing store for ActionAvailable.  This enables animation, styling, binding, etc...
        public static readonly DependencyProperty ActionAvailableProperty =
            DependencyProperty.Register("ActionAvailable", typeof(bool), typeof(UploadProgressBar), new PropertyMetadata(false));

        public string ActionLink
        {
            get { return (string)GetValue(ActionLinkProperty); }
            set { SetValue(ActionLinkProperty, value); }
        }

        // Using a DependencyProperty as the backing store for ActionLink.  This enables animation, styling, binding, etc...
        public static readonly DependencyProperty ActionLinkProperty =
            DependencyProperty.Register("ActionLink", typeof(string), typeof(UploadProgressBar), new PropertyMetadata(""));
    }
}

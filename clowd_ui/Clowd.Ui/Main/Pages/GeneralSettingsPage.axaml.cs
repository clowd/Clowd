using Avalonia.Controls;
using Clowd.Config;

namespace Clowd.UI.Pages
{
    public partial class GeneralSettingsPage : UserControl
    {
        public GeneralSettingsPage()
        {
            InitializeComponent();
            DataContext = SettingsRoot.Current.General;
        }
    }
}

using Avalonia.Controls;
using Clowd.Localization;
using Clowd.Localization.Resources;

namespace Clowd.Avalonia.Pages
{
    public partial class GeneralSettingsPage : UserControl
    {
        public GeneralSettingsPage()
        {
            InitializeComponent();
            SelectLanguageBox.SelectionChanged += SelectLanguageBox_SelectionChanged;
        }

        private void SelectLanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SelectLanguageBox.SelectedItem is LanguageInfo l)
            {
                l.SetAsCurrentCulture();
            }
        }
    }
}

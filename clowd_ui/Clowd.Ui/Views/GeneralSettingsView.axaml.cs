using Avalonia.Controls;
using Clowd.Ui.Models.Settings;

namespace Clowd.Ui.Views;

public partial class GeneralSettingsView : UserControl
{
    public GeneralSettingsView()
    {
        InitializeComponent();
    }

    public GeneralSettingsView(SettingsGeneral settings) : this()
    {
        DataContext = settings;
    }
}

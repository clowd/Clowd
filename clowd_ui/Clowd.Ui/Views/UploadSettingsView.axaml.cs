using Avalonia.Controls;
using Clowd.Ui.Models.Settings;

namespace Clowd.Ui.Views;

public partial class UploadSettingsView : UserControl
{
    public UploadSettingsView()
    {
        InitializeComponent();
    }

    public UploadSettingsView(SettingsUpload settings) : this()
    {
        DataContext = settings;
    }
}

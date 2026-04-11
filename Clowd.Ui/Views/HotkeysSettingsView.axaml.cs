using Avalonia.Controls;
using Avalonia.Interactivity;
using Clowd.Ui.Models.Settings;
using Clowd.Ui.ViewModels.Pages;
using Clowd.Ui.Views.Dialogs;

namespace Clowd.Ui.Views;

public partial class HotkeysSettingsView : UserControl
{
    public HotkeysSettingsView()
    {
        InitializeComponent();
    }

    public HotkeysSettingsView(HotkeysViewModel vm) : this()
    {
        DataContext = vm;
    }

    private async void OnChangeClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: GlobalTrigger trigger }) return;

        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;

        var dialog = new KeyCaptureDialog();
        var result = await dialog.ShowDialogAsync(owner);
        if (result != null)
        {
            trigger.KeyGesture = result;
        }
    }
}

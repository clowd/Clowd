using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Clowd.Ui.Models.Settings;
using Clowd.Ui.ViewModels.Pages;
using Clowd.Ui.Views.Dialogs;

namespace Clowd.Ui.Views;

public partial class EditorSettingsView : UserControl
{
    public EditorSettingsView()
    {
        InitializeComponent();
        UnitCombo.ItemsSource = Enum.GetValues<TimeOptionUnit>();
    }

    public EditorSettingsView(EditorSettingsViewModel vm) : this()
    {
        DataContext = vm;
    }

    private async void OnPickColor(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not EditorSettingsViewModel vm) return;
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;

        var dialog = new ColorPickerDialog(vm.Settings.CanvasBackground);
        var result = await dialog.ShowDialogAsync(owner);
        if (result.HasValue)
            vm.Settings.CanvasBackground = result.Value;
    }
}

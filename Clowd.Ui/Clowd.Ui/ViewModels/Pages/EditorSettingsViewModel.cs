using Avalonia.Media;
using Clowd.Ui.Models.Common;
using Clowd.Ui.Models.Settings;

namespace Clowd.Ui.ViewModels.Pages;

/// <summary>
/// Wraps SettingsEditor with friendly bindings (decimal for NumericUpDown, IBrush for swatches).
/// </summary>
public sealed class EditorSettingsViewModel : ObservableObject
{
    public SettingsEditor Settings { get; }

    public EditorSettingsViewModel(SettingsEditor settings)
    {
        Settings = settings;
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsEditor.CanvasBackground))
                OnPropertyChanged(nameof(CanvasBackgroundBrush));
            if (e.PropertyName == nameof(SettingsEditor.StartupPadding))
                OnPropertyChanged(nameof(StartupPaddingDecimal));
        };
        settings.DeleteSessionsAfter.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TimeOption.Number))
                OnPropertyChanged(nameof(DeleteSessionsNumberDecimal));
        };
    }

    public IBrush CanvasBackgroundBrush => new SolidColorBrush(Settings.CanvasBackground);

    public decimal StartupPaddingDecimal
    {
        get => Settings.StartupPadding;
        set => Settings.StartupPadding = (int)value;
    }

    public decimal DeleteSessionsNumberDecimal
    {
        get => Settings.DeleteSessionsAfter.Number;
        set => Settings.DeleteSessionsAfter.Number = (int)value;
    }
}

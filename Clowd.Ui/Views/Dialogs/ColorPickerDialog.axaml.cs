using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Clowd.Ui.Views.Dialogs;

public partial class ColorPickerDialog : Window
{
    public ColorPickerDialog()
    {
        InitializeComponent();
    }

    public ColorPickerDialog(Color initial) : this()
    {
        Picker.Color = initial;
    }

    public Task<Color?> ShowDialogAsync(Window owner)
    {
        return ShowDialog<Color?>(owner);
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        Close((Color?)Picker.Color);
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close((Color?)null);
    }
}

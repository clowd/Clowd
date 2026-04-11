using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Clowd.Ui.Models.Settings;

namespace Clowd.Ui.Views.Dialogs;

public partial class KeyCaptureDialog : Window
{
    private SimpleKeyGesture? _captured;

    public KeyCaptureDialog()
    {
        InitializeComponent();
    }

    public Task<SimpleKeyGesture?> ShowDialogAsync(Window owner)
    {
        return ShowDialog<SimpleKeyGesture?>(owner);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Ignore lone modifier presses; wait for a real key.
        if (IsModifier(e.Key))
        {
            base.OnKeyDown(e);
            return;
        }

        _captured = new SimpleKeyGesture(e.Key, e.KeyModifiers);
        GestureText.Text = _captured.ToString();
        e.Handled = true;
    }

    private static bool IsModifier(Key k) =>
        k is Key.LeftCtrl or Key.RightCtrl
          or Key.LeftShift or Key.RightShift
          or Key.LeftAlt or Key.RightAlt
          or Key.LWin or Key.RWin;

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        Close(_captured);
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close((SimpleKeyGesture?)null);
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Clowd.UI.Helpers
{
    /// <summary>
    /// Attached behavior: when Active is set on a TextBox, all text is selected when the
    /// TextBox receives focus (decision table #65 — simplified from the WPF keyboard/mouse
    /// focus heuristics).
    /// </summary>
    public class SelectTextOnFocus : AvaloniaObject
    {
        public static readonly AttachedProperty<bool> ActiveProperty =
            AvaloniaProperty.RegisterAttached<SelectTextOnFocus, TextBox, bool>("Active");

        static SelectTextOnFocus()
        {
            ActiveProperty.Changed.AddClassHandler<TextBox>(OnActiveChanged);
        }

        public static bool GetActive(TextBox textBox) => textBox.GetValue(ActiveProperty);

        public static void SetActive(TextBox textBox, bool value) => textBox.SetValue(ActiveProperty, value);

        private static void OnActiveChanged(TextBox textBox, AvaloniaPropertyChangedEventArgs e)
        {
            textBox.GotFocus -= OnGotFocus;
            if (e.GetNewValue<bool>())
                textBox.GotFocus += OnGotFocus;
        }

        // Avalonia 12 unified GotFocusEventArgs/LostFocusEventArgs into FocusChangedEventArgs.
        private static void OnGotFocus(object sender, FocusChangedEventArgs e)
        {
            (sender as TextBox)?.SelectAll();
        }
    }
}

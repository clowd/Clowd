using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Clowd.UI.Helpers
{
    public class SelectTextOnFocus : DependencyObject
    {
        public static readonly DependencyProperty ActiveProperty = DependencyProperty.RegisterAttached(
            "Active",
            typeof(bool),
            typeof(SelectTextOnFocus),
            new PropertyMetadata(false, ActivePropertyChanged));

        [AttachedPropertyBrowsableForChildren(IncludeDescendants = false)]
        [AttachedPropertyBrowsableForType(typeof(TextBox))]
        public static bool GetActive(DependencyObject obj) => (bool)obj.GetValue(ActiveProperty);

        public static void SetActive(DependencyObject obj, bool value) => obj.SetValue(ActiveProperty, value);

        public static readonly DependencyProperty MouseDownProperty = DependencyProperty.RegisterAttached(
            "MouseDown",
            typeof(bool),
            typeof(SelectTextOnFocus),
            new PropertyMetadata(false));

        public static bool GetMouseDown(DependencyObject obj) => (bool)obj.GetValue(MouseDownProperty);
        
        public static void SetMouseDown(DependencyObject obj, bool value) => obj.SetValue(MouseDownProperty, value);

        private static void ActivePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TextBox textBox)
            {
                if ((e.NewValue as bool?).GetValueOrDefault(false))
                {
                    textBox.GotKeyboardFocus += OnGotKeyboardFocus;
                    textBox.LostKeyboardFocus += OnLostKeyboardFocus;
                    textBox.PreviewMouseLeftButtonDown += OnMouseLeftButtonDown;
                    textBox.PreviewMouseLeftButtonUp += OnMouseLeftButtonUp;
                }
                else
                {
                    textBox.GotKeyboardFocus -= OnGotKeyboardFocus;
                    textBox.LostKeyboardFocus += OnLostKeyboardFocus;
                    textBox.PreviewMouseLeftButtonDown += OnMouseLeftButtonDown;
                    textBox.PreviewMouseLeftButtonUp -= OnMouseLeftButtonUp;
                }
            }
        }

        private static void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (GetParentFromVisualTree(e.OriginalSource) is not TextBox textBox)
                return;

            if (!textBox.IsKeyboardFocusWithin)
            {
                SetMouseDown(textBox, true);
            }
        }

        private static void OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (e.OriginalSource is not TextBox textBox)
                return;

            textBox.Select(0, 0);
        }

        private static void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (GetParentFromVisualTree(e.OriginalSource) is not TextBox textBox)
                return;

            if (GetMouseDown(textBox) && textBox.SelectionLength < 1)
                textBox.SelectAll();

            SetMouseDown(textBox, false);
        }

        private static DependencyObject GetParentFromVisualTree(object source)
        {
            DependencyObject parent = source as UIElement;
            while (parent != null && !(parent is TextBox))
                parent = VisualTreeHelper.GetParent(parent);

            return parent;
        }

        private static void OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (e.OriginalSource is not TextBox textBox)
                return;

            if (Keyboard.PrimaryDevice.IsKeyDown(Key.Tab))
            {
                textBox.SelectAll();
            }
        }
    }
}

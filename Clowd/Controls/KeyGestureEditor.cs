using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Clowd.Utilities;

namespace Clowd.Controls
{
    public class KeyGestureEditor : UserControl
    {
        public GlobalTrigger Trigger
        {
            get { return (GlobalTrigger)GetValue(TriggerProperty); }
            set { SetValue(TriggerProperty, value); }
        }

        public static readonly DependencyProperty TriggerProperty =
            DependencyProperty.Register("Trigger", typeof(GlobalTrigger), typeof(KeyGestureEditor),
                new PropertyMetadata(null, GestureChangedCallback));

        public bool IsEditing { get; private set; }

        private static void GestureChangedCallback(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs dependencyPropertyChangedEventArgs)
        {
            var ths = (KeyGestureEditor)dependencyObject;
            ths.UpdateControls();
        }

        private Button _button;
        private Border _status;

        public KeyGestureEditor()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(10) });
            grid.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(50) });

            _button = new Button();
            _button.Click += _button_Click;
            _button.ToolTip = "Click to edit the current gesture";
            _button.Style = null;
            _button.FocusVisualStyle = null;
            grid.Children.Add(_button);

            _status = new Border();
            _status.BorderBrush = Brushes.DarkGray;
            _status.BorderThickness = new Thickness(1);
            Grid.SetColumn(_status, 2);
            grid.Children.Add(_status);

            this.Content = grid;

            UpdateControls();
        }

        private void _button_Click(object sender, RoutedEventArgs e)
        {
            if (IsEditing)
                return;
            IsEditing = true;
            this.KeyDown += OnKeyDown;
            this.KeyUp += OnKeyUp;
            UpdateControls();
        }

        private void OnKeyUp(object sender, KeyEventArgs e)
        {
            // this is because the PrtScr button only shows up in the KeyUp handler
            Key key = (e.Key == Key.System ? e.SystemKey : e.Key);
            if (key == Key.PrintScreen)
                FinishEditing(key, Keyboard.Modifiers);
            else
                UpdateControls();
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            Key key = (e.Key == Key.System ? e.SystemKey : e.Key);
            var keyCode = (int)key;
            // ignore any known modifier keys.
            if (keyCode == 70 || keyCode == 71 || (keyCode >= 116 && keyCode <= 121))
                UpdateControls();
            else
                FinishEditing(key, Keyboard.Modifiers);
        }

        private void FinishEditing(Key key, ModifierKeys modifiers)
        {
            IsEditing = false;
            this.KeyDown -= OnKeyDown;
            this.KeyUp -= OnKeyUp;
            try
            {
                Trigger.Gesture = new KeyGesture(key, modifiers);
            }
            catch
            {
                // invalid keygesture
                Trigger.Gesture = null;
            }
            UpdateControls();
        }
        private void UpdateControls()
        {
            if (IsEditing)
            {
                _status.Background = Brushes.PaleGoldenrod;
                StringBuilder key = new StringBuilder();
                foreach (var en in GetUniqueFlags(Keyboard.Modifiers))
                {
                    key.Append((ModifierKeys)en);
                    key.Append('+');
                }
                key.Append(" ...");
                _button.Content = key.ToString();
            }
            else
            {
                if (Trigger == null || Trigger.Gesture == null)
                {
                    _button.Content = "(undefined)";
                    _status.Background = Brushes.PaleVioletRed;
                }
                else
                {
                    _button.Content = Trigger.Gesture.GetDisplayStringForCulture(CultureInfo.CurrentUICulture);
                    _status.Background = Brushes.PaleGreen;
                }
            }
        }

        private IEnumerable<Enum> GetUniqueFlags(Enum flags)
        {
            ulong flag = 1;
            foreach (var value in Enum.GetValues(flags.GetType()).Cast<Enum>())
            {
                ulong bits = Convert.ToUInt64(value);
                while (flag < bits)
                {
                    flag <<= 1;
                }

                if (flag == bits && flags.HasFlag(value))
                {
                    yield return value;
                }
            }
        }
    }
}

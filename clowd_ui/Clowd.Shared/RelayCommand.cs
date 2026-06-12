using System;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;

namespace Clowd.UI.Helpers
{
    public delegate void RelayExecute(object parameter);

    public delegate bool RelayCanExecute(object parameter);

    public class RelayCommand : SimpleNotifyObject, ICommand
    {
        public RelayExecute Executed
        {
            get => _executed;
            set => Set(ref _executed, value);
        }

        public RelayCanExecute CanExecute
        {
            get => _canExecute;
            set => Set(ref _canExecute, value);
        }

        public string Text
        {
            get => _text;
            set => Set(ref _text, value);
        }

        public string GestureText
        {
            get => _gestureText;
            set => Set(ref _gestureText, value);
        }

        public SimpleKeyGesture Gesture
        {
            get => _gesture;
            set
            {
                if (Set(ref _gesture, value))
                    GestureText = value?.ToString();
            }
        }

        public object Icon
        {
            get => _icon;
            set => Set(ref _icon, value);
        }

        public bool IsBareGesture => Gesture != null && Gesture.Modifiers == KeyModifiers.None;

        private RelayExecute _executed;
        private RelayCanExecute _canExecute;
        private string _text;
        private string _gestureText;
        private SimpleKeyGesture _gesture;
        private object _icon;

        public event EventHandler CanExecuteChanged;

        public void RaiseCanExecuteChanged()
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }

        bool ICommand.CanExecute(object parameter)
        {
            return CanExecute?.Invoke(parameter) ?? true;
        }

        void ICommand.Execute(object parameter)
        {
            Executed?.Invoke(parameter);
        }

        /// <summary>
        /// Creates an Avalonia KeyBinding for this command's gesture. Returns null when there is no
        /// gesture or the gesture is bare (modifier-less) — bare gestures are routed exclusively by
        /// the EditorWindow tunnel KeyDown handler.
        /// </summary>
        public KeyBinding CreateKeyBinding()
        {
            if (Gesture == null || IsBareGesture)
                return null;

            return new KeyBinding { Command = this, Gesture = Gesture.ToKeyGesture() };
        }

        public MenuItem CreateMenuItem()
        {
            var menu = new MenuItem();
            menu.Header = Text?.Replace("_", "");

            var gesture = Gesture?.ToKeyGesture();
            if (gesture == null && !string.IsNullOrEmpty(GestureText))
            {
                try
                {
                    gesture = KeyGesture.Parse(GestureText);
                }
                catch
                {
                    // display-only; ignore unparseable gesture text
                }
            }

            if (gesture != null)
                menu.InputGesture = gesture;

            if (Icon != null)
                menu.Icon = Icon;

            menu.Command = this;
            return menu;
        }
    }
}

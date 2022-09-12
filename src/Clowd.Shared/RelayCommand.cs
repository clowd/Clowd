using System;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using DependencyPropertyGenerator;

namespace Clowd.UI.Helpers
{
    public delegate void RelayExecute(object parameter);
    public delegate bool RelayCanExecute(object parameter);

    [DependencyProperty<RelayExecute>("Executed")]
    [DependencyProperty<RelayCanExecute>("CanExecute")]
    [DependencyProperty<string>("Text")]
    [DependencyProperty<string>("GestureText")]
    [DependencyProperty<SimpleKeyGesture>("Gesture")]
    [DependencyProperty<object>("Icon")]
    public partial class RelayCommand : DependencyObject, ICommand
    {
        private readonly UIElement _parent;
        private InputBinding _binding;

        public RelayCommand()
        {
        }

        public RelayCommand(UIElement parent)
        {
            _parent = parent;
        }

        event EventHandler ICommand.CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }

        bool ICommand.CanExecute(object parameter)
        {
            return CanExecute?.Invoke(parameter) ?? true;
        }

        void ICommand.Execute(object parameter)
        {
            Executed?.Invoke(parameter);
        }

        partial void OnGestureChanged(SimpleKeyGesture oldValue, SimpleKeyGesture newValue)
        {
            // if we have a parent, we'll automatically register input bindings.
            if (_parent != null)
            {
                if (_binding != null)
                    _parent.InputBindings.Remove(_binding);

                _binding = CreateKeyBinding(); // can be null

                if (_binding != null)
                    _parent.InputBindings.Add(_binding);
            }

            GestureText = newValue.ToString();
        }

        public InputBinding CreateKeyBinding()
        {
            if (Gesture == null)
                return null;

            if (Gesture.Modifiers == ModifierKeys.None)
                return new BareKeyBinding(this, Gesture.Key);

            return new KeyBinding(this, Gesture.Key, Gesture.Modifiers);
        }

        public MenuItem CreateMenuItem()
        {
            var menu = new MenuItem();
            menu.SetBinding(MenuItem.HeaderProperty, new Binding(nameof(Text)) { Source = this, Mode = BindingMode.OneWay });
            menu.SetBinding(MenuItem.InputGestureTextProperty, new Binding(nameof(GestureText)) { Source = this, Mode = BindingMode.OneWay });
            menu.SetBinding(MenuItem.IconProperty, new Binding(nameof(Icon)) { Source = this, Mode = BindingMode.OneWay });
            menu.Command = this;
            return menu;
        }
    }
}

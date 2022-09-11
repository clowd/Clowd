using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;

namespace Clowd.UI.Helpers
{
    public class RelayCommand : SimpleNotifyObject, ICommand
    {
        readonly Action<object> _execute;
        readonly Predicate<object> _canExecute;

        public RelayCommand(Action<object> execute)
            : this(execute, null)
        { }

        public RelayCommand(Action<object> execute, Predicate<object> canExecute)
        {
            if (execute == null)
                throw new ArgumentNullException("execute");
            _execute = execute;
            _canExecute = canExecute;
        }

        //[DebuggerStepThrough]
        public bool CanExecute(object parameter)
        {
            return _canExecute == null ? true : _canExecute(parameter);
        }

        public event EventHandler CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }

        public void Execute(object parameter) { _execute(parameter); }
    }

    public class RelayUICommand : RelayCommand
    {
        public DataTemplate IconTemplate
        {
            get => _iconTemplate;
            set => Set(ref _iconTemplate, value);
        }

        public string Text
        {
            get => _text;
            set => Set(ref _text, value);
        }

        private DataTemplate _iconTemplate;
        private string _text;

        public RelayUICommand(Action<object> execute)
            : this(execute, null)
        { }

        public RelayUICommand(Action<object> execute, Predicate<object> canExecute)
            : this(execute, canExecute, null)
        { }

        public RelayUICommand(Action<object> execute, Predicate<object> canExecute, string text)
            : this(execute, canExecute, text, null)
        { }

        public RelayUICommand(Action<object> execute, Predicate<object> canExecute, string text, DataTemplate icon)
            : base(execute, canExecute)
        {
            Text = text;
            IconTemplate = icon;
        }
    }
}

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Clowd
{
    public abstract class SimpleNotifyObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual bool Set<T>(ref T storage, T value, [CallerMemberName] string propertyName = null, params string[] dependentProperties)
        {
            if (EqualityComparer<T>.Default.Equals(storage, value))
                return false;

            storage = value;
            OnPropertyChanged(propertyName);

            if (dependentProperties != null)
                OnPropertiesChanged(dependentProperties);

            return true;
        }

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            OnPropertyChanged(new PropertyChangedEventArgs(propertyName));
        }

        protected virtual void OnPropertyChanged(PropertyChangedEventArgs args)
        {
            PropertyChanged?.Invoke(this, args);
        }

        protected void OnPropertiesChanged(params string[] propertyNames)
        {
            foreach (string propertyName in propertyNames)
            {
                OnPropertyChanged(propertyName);
            }
        }

        protected void ClearPropertyChangedHandlers()
        {
            PropertyChanged = null;
        }
    }

    public abstract class DictionaryNotifyObject : SimpleNotifyObject
    {
        private Dictionary<string, object> _store = new Dictionary<string, object>();

        protected virtual bool Set<T>(T value, [CallerMemberName] string propertyName = null)
        {
            if (_store.TryGetValue(propertyName, out var stor) && Equals(stor, value))
                return false;

            _store[propertyName] = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        protected virtual T Get<T>([CallerMemberName] string propertyName = null)
        {
            if (_store.TryGetValue(propertyName, out var stor))
                if (stor.GetType().IsAssignableFrom(typeof(T)))
                    return (T)stor;

            return default;
        }
    }
}

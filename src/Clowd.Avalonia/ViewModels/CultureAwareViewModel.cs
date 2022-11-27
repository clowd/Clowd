﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls.Documents;
using Avalonia.Utilities;
using Clowd.Avalonia.Reactive;
using Clowd.Localization.Resources;
using ReactiveUI;

namespace Clowd.Avalonia.ViewModels
{
    internal class CultureAwareViewModel : ReactiveObject, IObserver<CultureInfo>
    {
        PropertyInfo[] _properties;

        public CultureAwareViewModel()
        {
            _properties = GetType().GetProperties().Where(p => p.PropertyType == typeof(string)).ToArray();
            Strings.GetCultureChangedObservable().ToWeakObservable().Subscribe(this);
        }

        void IObserver<CultureInfo>.OnCompleted()
        {
        }

        void IObserver<CultureInfo>.OnError(Exception error)
        {
        }

        void IObserver<CultureInfo>.OnNext(CultureInfo value)
        {
            foreach (var p in _properties)
            {
                OnPropertyChanged(p.Name);
            }
        }

        protected virtual void OnPropertyChanged(string propertyName)
        {
            this.RaisePropertyChanged(propertyName);
        }

        protected virtual void OnPropertyChanging(string propertyName)
        {
            this.RaisePropertyChanging(propertyName);
        }
    }
}
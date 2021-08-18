using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using RT.Serialization;
using RT.Util;

namespace Clowd.Config
{
    public abstract class SettingsCategoryBase : SimpleNotifyObject, IDisposable, IClassifyObjectProcessor
    {
        [ClassifyIgnore]
        private readonly List<INotifyPropertyChanged> _subscriptions = new List<INotifyPropertyChanged>();

        protected void Subscribe(params INotifyPropertyChanged[] subscriptions)
        {
            _subscriptions.AddRange(subscriptions);
            subscriptions.ToList().ForEach(a => a.PropertyChanged += Item_PropertyChanged);
        }

        private void Item_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            OnPropertyChanged(e);
        }

        public void Dispose()
        {
            _subscriptions.ForEach(a => a.PropertyChanged -= Item_PropertyChanged);
            ClearPropertyChangedHandlers();
            DisposeInternal();
        }

        void IClassifyObjectProcessor.BeforeSerialize()
        {
            BeforeSerializeInternal();
        }

        void IClassifyObjectProcessor.AfterDeserialize()
        {
            AfterDeserializeInternal();
        }

        protected virtual void DisposeInternal() { }
        protected virtual void BeforeSerializeInternal() { }
        protected virtual void AfterDeserializeInternal() { }
    }
}

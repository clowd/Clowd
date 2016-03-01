using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Clowd.Utilities
{
    public sealed class TrulyObservableCollection<T> : ObservableCollection<T>, INotifyPropertyChanged
        where T : INotifyPropertyChanged
    {
        public new event PropertyChangedEventHandler PropertyChanged
        {
            add { base.PropertyChanged += value; }
            remove { base.PropertyChanged -= value; }
        }

        public TrulyObservableCollection()
            : base()
        {
            this.CollectionChanged += this.TrulyObservableCollectionCollectionChanged;
        }

        public void TrulyObservableCollectionCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            //this is kind of stupid, because it will only ever be one item but because NetItems is an array we need to 
            //enumerate it in case this is ever implemented.
            if (e.NewItems != null)
            {
                foreach (Object item in e.NewItems)
                {
                    (item as INotifyPropertyChanged).PropertyChanged += this.ItemPropertyChanged;
                }
            }
            if (e.OldItems != null)
            {
                foreach (Object item in e.OldItems)
                {
                    (item as INotifyPropertyChanged).PropertyChanged -= this.ItemPropertyChanged;
                }
            }
        }

        public void AddRange(IEnumerable<T> dataToAdd)
        {
            this.CheckReentrancy();

            //int startingIndex = this.Count;

            foreach (var data in dataToAdd)
            {
                this.Items.Add(data);
            }

            this.OnPropertyChanged(new PropertyChangedEventArgs("Count"));
            this.OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            this.OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }

        private void ItemPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            this.OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            this.OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }
}

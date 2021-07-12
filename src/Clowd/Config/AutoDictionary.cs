using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace Clowd.Config
{
    public class AutoDictionary<TKey, TValue> : IDictionary<TKey, TValue>, INotifyPropertyChanged where TValue : INotifyPropertyChanged
    {
        private Dictionary<TKey, TValue> _impl = new Dictionary<TKey, TValue>();

        public event PropertyChangedEventHandler PropertyChanged;

        public TValue this[TKey key]
        {
            get => GetValue(key);
            set => throw new NotImplementedException(); // _impl[key] = value;
        }

        public ICollection<TKey> Keys => _impl.Keys;

        public ICollection<TValue> Values => _impl.Values;

        public int Count => _impl.Count;

        public bool IsReadOnly => false;

        public void Add(TKey key, TValue value)
        {
            _impl.Add(key, value);
            value.PropertyChanged += Item_PropertyChanged;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Collection"));
        }

        private void Item_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            PropertyChanged?.Invoke(this, e);
        }

        protected virtual TValue GetValue(TKey key)
        {
            if (!_impl.ContainsKey(key))
                _impl[key] = (TValue)Activator.CreateInstance(typeof(TValue));
            return _impl[key];
        }

        public void Add(KeyValuePair<TKey, TValue> item)
        {
            _impl.Add(item.Key, item.Value);
            item.Value.PropertyChanged += Item_PropertyChanged;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Collection"));
        }

        public void Clear()
        {
            this.Values.ToList().ForEach(v => v.PropertyChanged -= Item_PropertyChanged);
            _impl.Clear();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Collection"));
        }

        public bool Contains(KeyValuePair<TKey, TValue> item)
        {
            return _impl.Contains(item);
        }

        public bool ContainsKey(TKey key)
        {
            return _impl.ContainsKey(key);
        }

        public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex)
        {
            ((IDictionary<TKey, TValue>)_impl).CopyTo(array, arrayIndex);
        }

        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
        {
            return _impl.GetEnumerator();
        }

        public bool Remove(TKey key)
        {
            throw new NotImplementedException();
            return _impl.Remove(key);
        }

        public bool Remove(KeyValuePair<TKey, TValue> item)
        {
            throw new NotImplementedException();
            return _impl.Remove(item.Key);
        }

        public bool TryGetValue(TKey key, out TValue value)
        {
            return _impl.TryGetValue(key, out value);
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return _impl.GetEnumerator();
        }
    }
}

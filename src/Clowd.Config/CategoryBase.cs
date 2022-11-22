using System.ComponentModel;
using System.Runtime.CompilerServices;
using RT.Serialization;

namespace Clowd.Config;

public abstract class CategoryBase : SimpleNotifyObject, IDisposable, IClassifyObjectProcessor
{
    [ClassifyIgnore] private readonly List<INotifyPropertyChanged> _subscriptions = new List<INotifyPropertyChanged>();

    protected void Subscribe(params INotifyPropertyChanged[] subscriptions)
    {
        foreach (var s in subscriptions)
        {
            if (s == null) continue;
            _subscriptions.Add(s);
            s.PropertyChanged += Item_PropertyChanged;
        }
    }

    private void Item_PropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(e);
    }

    public void Dispose()
    {
        _subscriptions.ForEach(a => a.PropertyChanged -= Item_PropertyChanged);
        _subscriptions.Clear();
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

    protected override bool Set<T>(ref T storage, T value, [CallerMemberName] string propertyName = null, params string[] dependentProperties)
    {
        // unsubscribe the previous object value
        if (storage is INotifyPropertyChanged npcRemove && _subscriptions.Contains(npcRemove))
        {
            npcRemove.PropertyChanged -= Item_PropertyChanged;
            _subscriptions.Remove(npcRemove);
            if (storage is IDisposable disp)
                disp.Dispose();
        }

        // subscribe the new object value
        if (value is INotifyPropertyChanged npcAdd && !_subscriptions.Contains(npcAdd))
        {
            npcAdd.PropertyChanged += Item_PropertyChanged;
            _subscriptions.Add(npcAdd);
        }

        return base.Set(ref storage, value, propertyName, dependentProperties);
    }
}

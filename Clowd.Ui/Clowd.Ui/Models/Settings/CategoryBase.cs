using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Clowd.Ui.Models.Common;

namespace Clowd.Ui.Models.Settings;

/// <summary>
/// Base class for settings categories. Subscribes to nested INotifyPropertyChanged children
/// so that any change deep in the tree bubbles up to the SettingsRoot for save scheduling.
/// </summary>
public abstract class CategoryBase : ObservableObject
{
    [JsonIgnore]
    private readonly List<INotifyPropertyChanged> _subscriptions = new();

    protected void Subscribe(params INotifyPropertyChanged?[] subscriptions)
    {
        foreach (var s in subscriptions)
        {
            if (s == null) continue;
            if (_subscriptions.Contains(s)) continue;
            _subscriptions.Add(s);
            s.PropertyChanged += OnChildPropertyChanged;
        }
    }

    private void OnChildPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(e);
    }

    /// <summary>
    /// Setter that also wires up child INPC subscription/teardown when nested objects are swapped.
    /// </summary>
    protected bool SetWithSubscription<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (storage is INotifyPropertyChanged oldNpc && _subscriptions.Contains(oldNpc))
        {
            oldNpc.PropertyChanged -= OnChildPropertyChanged;
            _subscriptions.Remove(oldNpc);
        }

        if (value is INotifyPropertyChanged newNpc && !_subscriptions.Contains(newNpc))
        {
            _subscriptions.Add(newNpc);
            newNpc.PropertyChanged += OnChildPropertyChanged;
        }

        return Set(ref storage, value, propertyName);
    }

    public virtual void OnLoaded()
    {
        // override to re-subscribe to deserialized child objects
    }
}

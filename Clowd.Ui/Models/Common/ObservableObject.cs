using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Clowd.Ui.Models.Common;

/// <summary>
/// Minimal INotifyPropertyChanged base class. Manual implementation, no MVVM framework.
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value))
            return false;

        storage = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        PropertyChanged?.Invoke(this, e);
    }

    protected void ClearPropertyChangedHandlers()
    {
        PropertyChanged = null;
    }
}

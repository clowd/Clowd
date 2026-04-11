using System.Text.Json.Serialization;
using Avalonia.Input;
using Clowd.Ui.Models.Common;

namespace Clowd.Ui.Models.Settings;

/// <summary>
/// Pure-data global hotkey definition. The original WPF version called RegisterHotKey on construction;
/// in this cross-platform port we just store the gesture. The capture process owns OS-level
/// hotkey registration when it gets wired in.
/// </summary>
public sealed class GlobalTrigger : ObservableObject
{
    private SimpleKeyGesture? _keyGesture;

    public SimpleKeyGesture? KeyGesture
    {
        get => _keyGesture;
        set
        {
            if (Set(ref _keyGesture, value))
                OnPropertyChanged(nameof(KeyGestureText));
        }
    }

    [JsonIgnore]
    public string KeyGestureText => _keyGesture?.ToString() ?? "(unset)";

    /// <summary>Always false in this build — registration is delegated to a platform service / capture process.</summary>
    [JsonIgnore]
    public bool IsRegistered => false;

    public GlobalTrigger()
    {
    }

    public GlobalTrigger(Key key)
        : this(new SimpleKeyGesture(key))
    {
    }

    public GlobalTrigger(Key key, KeyModifiers modifiers)
        : this(new SimpleKeyGesture(key, modifiers))
    {
    }

    public GlobalTrigger(SimpleKeyGesture? gesture)
    {
        _keyGesture = gesture;
    }
}

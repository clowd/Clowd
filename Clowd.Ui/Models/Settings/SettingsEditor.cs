using Avalonia.Media;

namespace Clowd.Ui.Models.Settings;

public sealed class SettingsEditor : CategoryBase
{
    private bool _restoreSessionsOnClowdStart = true;
    private Color _canvasBackground = Colors.Transparent;
    private int _startupPadding = 30;
    private TimeOption _deleteSessionsAfter = new(30, TimeOptionUnit.Days);

    public bool RestoreSessionsOnClowdStart
    {
        get => _restoreSessionsOnClowdStart;
        set => Set(ref _restoreSessionsOnClowdStart, value);
    }

    public Color CanvasBackground
    {
        get => _canvasBackground;
        set => Set(ref _canvasBackground, value);
    }

    public int StartupPadding
    {
        get => _startupPadding;
        set => Set(ref _startupPadding, value);
    }

    public TimeOption DeleteSessionsAfter
    {
        get => _deleteSessionsAfter;
        set => SetWithSubscription(ref _deleteSessionsAfter, value);
    }

    public SettingsEditor()
    {
        Subscribe(_deleteSessionsAfter);
    }

    public override void OnLoaded()
    {
        Subscribe(_deleteSessionsAfter);
    }
}

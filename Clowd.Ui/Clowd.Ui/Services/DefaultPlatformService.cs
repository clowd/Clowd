using System;
using Clowd.Ui.Models.Settings;

namespace Clowd.Ui.Services;

/// <summary>
/// Default cross-platform implementation. Global hotkeys are handled via SharpHook
/// (libuiohook) through <see cref="GlobalHotkeyHost"/>. Shell integration and notification
/// operations are still OS-specific and remain <see cref="NotImplementedException"/> stubs
/// until platform-specific implementations land.
/// </summary>
public sealed class DefaultPlatformService : IPlatformService, IDisposable
{
    private readonly GlobalHotkeyHost _hotkeyHost = new();
    private bool _disposed;

    public IDisposable RegisterGlobalHotkey(SimpleKeyGesture gesture, Action callback)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _hotkeyHost.Register(gesture, callback);
    }

    public void SetAutoStart(bool enabled)
        => throw new NotImplementedException("Launch-on-startup is platform-specific and not yet implemented.");

    public void SetExplorerContextMenu(bool enabled)
        => throw new NotImplementedException("Shell context-menu integration is platform-specific and not yet implemented.");

    public void ShowNotification(string title, string body)
        => throw new NotImplementedException("System notifications are platform-specific and not yet implemented.");

    public bool TryAcquireSingleInstance() => true;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _hotkeyHost.Dispose();
    }
}

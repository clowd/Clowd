using System;
using Clowd.Ui.Models.Settings;

namespace Clowd.Ui.Services;

/// <summary>
/// Platform-specific operations that Avalonia doesn't already abstract for us.
/// All members throw NotImplementedException by default — Windows/Mac/Linux implementations
/// will be added separately. The capture process owns global hotkeys.
/// </summary>
public interface IPlatformService
{
    IDisposable RegisterGlobalHotkey(SimpleKeyGesture gesture, Action callback);
    void SetAutoStart(bool enabled);
    void SetExplorerContextMenu(bool enabled);
    void ShowNotification(string title, string body);
    bool TryAcquireSingleInstance();
}

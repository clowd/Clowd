using System;

namespace Clowd.Ui.Models.Settings;

public sealed class SettingsGeneral : CategoryBase
{
    private string _clientId = Guid.NewGuid().ToString().ToLowerInvariant();
    private string? _lastUploadPath;
    private string? _lastSavePath;
    private bool _experimentalUpdateChannel;
    private bool _registerExplorerContextMenu;
    private bool _registerAutoStart;

    public bool ExperimentalUpdateChannel
    {
        get => _experimentalUpdateChannel;
        set => Set(ref _experimentalUpdateChannel, value);
    }

    public string ClientId
    {
        get => _clientId;
        set => Set(ref _clientId, value);
    }

    public string? LastUploadPath
    {
        get => _lastUploadPath;
        set => Set(ref _lastUploadPath, value);
    }

    public string? LastSavePath
    {
        get => _lastSavePath;
        set => Set(ref _lastSavePath, value);
    }

    public bool RegisterAutoStart
    {
        get => _registerAutoStart;
        set => Set(ref _registerAutoStart, value);
    }

    public bool RegisterExplorerContextMenu
    {
        get => _registerExplorerContextMenu;
        set => Set(ref _registerExplorerContextMenu, value);
    }
}

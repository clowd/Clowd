using System.ComponentModel;

namespace Clowd.Config;

public class SettingsGeneral : CategoryBase
{
    public bool ExperimentalUpdateChannel
    {
        get => _experimentalUpdateChannel;
        set => Set(ref _experimentalUpdateChannel, value, nameof(ExperimentalUpdateChannel), nameof(UpdateReleaseUrl));
    }

    [Browsable(false)]
    public string UpdateReleaseUrl => ExperimentalUpdateChannel
        ? Constants.ExperimentalReleaseFeedUrl
        : Constants.StableReleaseFeedUrl;

    [Browsable(false)]
    public string LastUploadPath
    {
        get => _lastUploadPath;
        set => Set(ref _lastUploadPath, value);
    }
    
    [Browsable(false)]
    public string ClientId
    {
        get => _clientId;
        set => Set(ref _clientId, value);
    }

    [Browsable(false)]
    public string LastSavePath
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

    [DisplayName("Confirm before exit")]
    [Description("If true, Clowd will prompt for confirmation before closing.")]
    public bool ConfirmClose
    {
        get => _confirmClose;
        set => Set(ref _confirmClose, value);
    }

    private string _clientId = Guid.NewGuid().ToString().ToLower();
    private string _lastUploadPath;
    private string _lastSavePath;
    private bool _confirmClose = true;
    private bool _experimentalUpdateChannel;
    private bool _registerExplorerContextMenu = true;
    private bool _registerAutoStart = true;
}

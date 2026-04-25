using System.Text.Json.Serialization;
using Clowd.Ui.Models.Common;

namespace Clowd.Ui.Models.Upload;

/// <summary>
/// Wraps an upload provider with user-toggleable enable/default-for state.
/// The provider instance itself is stateless and constructed fresh on load.
/// </summary>
public sealed class UploadProviderInfo : ObservableObject
{
    private bool _isEnabled;
    private SupportedUploadType _defaultFor;

    [JsonInclude]
    public string ProviderTypeName { get; set; } = string.Empty;

    public bool IsEnabled
    {
        get => _isEnabled;
        set => Set(ref _isEnabled, value);
    }

    public SupportedUploadType DefaultFor
    {
        get => _defaultFor;
        set => Set(ref _defaultFor, value);
    }

    [JsonIgnore]
    public IUploadProvider? Provider { get; set; }

    public UploadProviderInfo()
    {
    }

    public UploadProviderInfo(IUploadProvider provider)
    {
        Provider = provider;
        ProviderTypeName = provider.GetType().Name;
    }
}

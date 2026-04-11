using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json.Serialization;
using Clowd.Ui.Models.Upload;
using Clowd.Ui.Models.Upload.Providers;

namespace Clowd.Ui.Models.Settings;

public sealed class SettingsUpload : CategoryBase
{
    // Explicit registration — no AppDomain reflection. Order here = display order in the page.
    private static readonly Func<IUploadProvider>[] _providerFactories =
    {
        () => new CatboxUploadProvider(),
        () => new ImgurUploadProvider(),
        () => new HastebinUploadProvider(),
        () => new VgyMeUploadProvider(),
        () => new PicsurUploadProvider(),
        () => new AzureUploadProvider(),
        () => new BackBlazeUploadProvider(),
    };

    private ObservableCollection<UploadProviderInfo> _providers = new();

    public ObservableCollection<UploadProviderInfo> Providers
    {
        get => _providers;
        set => SetWithSubscription(ref _providers, value);
    }

    public SettingsUpload()
    {
    }

    public override void OnLoaded()
    {
        // Reattach IUploadProvider instances to deserialized info wrappers, then add any
        // new factories that weren't in the saved file.
        var byTypeName = _providerFactories
            .Select(f => f())
            .ToDictionary(p => p.GetType().Name);

        // attach existing
        foreach (var info in _providers)
        {
            if (byTypeName.TryGetValue(info.ProviderTypeName, out var provider))
                info.Provider = provider;
        }

        // drop entries with unknown type names
        var stale = _providers.Where(p => p.Provider is null).ToList();
        foreach (var s in stale) _providers.Remove(s);

        // add missing
        var existing = new HashSet<string>(_providers.Select(p => p.ProviderTypeName));
        foreach (var factory in _providerFactories)
        {
            var p = factory();
            if (existing.Contains(p.GetType().Name)) continue;
            _providers.Add(new UploadProviderInfo(p));
        }

        // ensure subscription
        Subscribe(_providers);
        foreach (var info in _providers) Subscribe(info);
    }

    public IEnumerable<UploadProviderInfo> GetEnabledProviders(SupportedUploadType type)
    {
        return _providers
            .Where(p => p.IsEnabled && p.Provider != null)
            .Where(p => (p.Provider!.SupportedUpload & type) != 0);
    }

    public UploadProviderInfo? GetDefaultProvider(SupportedUploadType type)
    {
        return GetEnabledProviders(type).FirstOrDefault(p => (p.DefaultFor & type) != 0);
    }
}

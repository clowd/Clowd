using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text.Json.Serialization;

namespace Clowd.Config
{
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public class FlattenSettingsObjectAttribute : Attribute
    { }

    public class UploadProviderInfo : SimpleNotifyObject
    {
        public bool IsEnabled
        {
            get => _isEnabled;
            set => Set(ref _isEnabled, value);
        }

        [Browsable(false)]
        public SupportedUploadType DefaultFor
        {
            get => _defaultFor;
            set => Set(ref _defaultFor, value);
        }

        [FlattenSettingsObject]
        public IUploadProvider Provider
        {
            get => _provider;
        }

        [Browsable(false)]
        public bool SupportsAcceleration => _provider is IAccelerateProvider;

        public UploadProviderInfo(IUploadProvider provider)
        {
            _provider = provider;
        }

        private bool _isEnabled;
        private readonly IUploadProvider _provider;
        private SupportedUploadType _defaultFor;
    }

    /// <summary>
    /// Persisted state for a single provider. The settings file is read back through the
    /// Microsoft.Extensions.Configuration binder (string-keyed), so polymorphic provider objects
    /// cannot be serialized directly — instead each provider's writable settings properties are
    /// flattened to strings here and re-applied after <see cref="SettingsUpload.DiscoverProviders"/>
    /// instantiates the provider.
    /// </summary>
    public class UploadProviderConfig
    {
        public bool IsEnabled { get; set; }

        public SupportedUploadType DefaultFor { get; set; } = SupportedUploadType.None;

        public Dictionary<string, string> Settings { get; set; } = new();
    }

    public class SettingsUpload : SimpleNotifyObject
    {
        // runtime-discovered state — populated by DiscoverProviders(), not persisted directly.
        [Browsable(false), JsonIgnore]
        public UploadProviderInfo[] Providers => _providers.ToArray();

        /// <summary>Persisted provider state, keyed by provider type name (e.g. "ImgurUploadProvider").
        /// Kept in sync with the runtime <see cref="Providers"/> wrappers automatically.</summary>
        [Browsable(false)]
        public Dictionary<string, UploadProviderConfig> ProviderConfig { get; set; } = new(StringComparer.Ordinal);

        private List<UploadProviderInfo> _providers = new();

        public SettingsUpload()
        { }

        public void SetDefaultProvider(UploadProviderInfo provider, SupportedUploadType types)
        {
            provider.DefaultFor |= types;

            // remove this default from all other providers
            foreach (var p in _providers)
            {
                if (p == provider) continue;
                p.DefaultFor &= ~types;
            }
        }

        public void ClearDefaultProvider(UploadProviderInfo provider, SupportedUploadType types)
        {
            provider.DefaultFor &= ~types;
        }

        public void ClearAllDefaultProviders()
        {
            foreach (var p in _providers)
            {
                p.DefaultFor = SupportedUploadType.None;
            }
        }

        public UploadProviderInfo GetDefaultProvider(SupportedUploadType type)
        {
            return GetEnabledProviders(type).FirstOrDefault(p => p.DefaultFor.HasFlag(type));
        }

        public IEnumerable<UploadProviderInfo> GetEnabledProviders(SupportedUploadType type)
        {
            return Providers
                .Where(p => p.IsEnabled)
                .Where(p => p.Provider.SupportedUpload == SupportedUploadType.All || p.Provider.SupportedUpload.HasFlag(type))
                .Select(p => p);
        }

        /// <summary>Discovers IUploadProvider implementations in loaded assemblies, applies any
        /// persisted <see cref="ProviderConfig"/> to them, and starts mirroring further changes
        /// back into <see cref="ProviderConfig"/>. Called explicitly from application startup —
        /// never as a side effect of settings parsing.</summary>
        public void DiscoverProviders()
        {
            var assembliesToSearch = AppDomain.CurrentDomain.GetAssemblies();
            var type = typeof(IUploadProvider);
            var types = assembliesToSearch
                .SelectMany(GetLoadableTypes)
                .Where(p => !p.IsAbstract && !p.IsInterface)
                .Where(p => type.IsAssignableFrom(p));

            foreach (var toAdd in types.Except(_providers.Select(p => p.Provider.GetType())))
            {
                var instance = (IUploadProvider)Activator.CreateInstance(toAdd);
                var info = new UploadProviderInfo(instance);

                if (ProviderConfig.TryGetValue(toAdd.Name, out var config))
                {
                    ApplyConfig(info, config);
                }

                // subscribe after applying saved state so startup does not look like a user edit
                info.PropertyChanged += (s, e) => SyncToConfig((UploadProviderInfo)s);
                instance.PropertyChanged += (s, e) => SyncToConfig(info);

                _providers.Add(info);
            }

            _providers = _providers.OrderBy(p => p.Provider.Name, StringComparer.Ordinal).ToList();
        }

        private static void ApplyConfig(UploadProviderInfo info, UploadProviderConfig config)
        {
            info.IsEnabled = config.IsEnabled;
            info.DefaultFor = config.DefaultFor;

            if (config.Settings == null)
                return;

            foreach (PropertyDescriptor pd in TypeDescriptor.GetProperties(info.Provider))
            {
                if (pd.IsReadOnly || !pd.IsBrowsable)
                    continue;

                if (!config.Settings.TryGetValue(pd.Name, out var raw) || raw == null)
                    continue;

                try
                {
                    pd.SetValue(info.Provider, pd.Converter.ConvertFromInvariantString(raw));
                }
                catch
                {
                    // a stale/invalid saved value should not prevent the provider from loading
                }
            }
        }

        /// <summary>Mirrors the current state of a provider wrapper into <see cref="ProviderConfig"/>
        /// and raises PropertyChanged so the UI layer's auto-save persists it.</summary>
        private void SyncToConfig(UploadProviderInfo info)
        {
            var config = new UploadProviderConfig
            {
                IsEnabled = info.IsEnabled,
                DefaultFor = info.DefaultFor,
            };

            foreach (PropertyDescriptor pd in TypeDescriptor.GetProperties(info.Provider))
            {
                if (pd.IsReadOnly || !pd.IsBrowsable)
                    continue;

                var value = pd.GetValue(info.Provider);
                if (value == null)
                    continue;

                config.Settings[pd.Name] = pd.Converter.ConvertToInvariantString(value);
            }

            ProviderConfig[info.Provider.GetType().Name] = config;
            OnPropertyChanged(nameof(ProviderConfig));
        }

        private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                return e.Types.Where(t => t != null);
            }
            catch
            {
                return Type.EmptyTypes;
            }
        }
    }
}

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

        [DisplayName("Zip risky file types before uploading")]
        [Description("Wraps executables, scripts and similar files in a zip archive before uploading, "
                    + "because browsers block direct downloads of these types.")]
        public bool WrapDangerousUploadsInZip
        {
            get => _wrapDangerousUploadsInZip;
            set => Set(ref _wrapDangerousUploadsInZip, value);
        }

        private bool _wrapDangerousUploadsInZip = true;

        private List<UploadProviderInfo> _providers = new();

        // the individual (non-composite) types a provider can be the default for
        private static readonly SupportedUploadType[] _defaultableTypes =
        {
            SupportedUploadType.Image,
            SupportedUploadType.Video,
            SupportedUploadType.Binary,
            SupportedUploadType.Text,
        };

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
        /// never as a side effect of settings parsing. Providers implementing
        /// <see cref="IBuiltInProvider"/> that have no persisted config yet are seeded on (see
        /// the seeding block below).</summary>
        public void DiscoverProviders()
        {
            var assembliesToSearch = AppDomain.CurrentDomain.GetAssemblies();
            var type = typeof(IUploadProvider);
            var types = assembliesToSearch
                .SelectMany(GetLoadableTypes)
                .Where(p => !p.IsAbstract && !p.IsInterface)
                .Where(p => type.IsAssignableFrom(p));

            var seeded = new List<UploadProviderInfo>();

            foreach (var toAdd in types.Except(_providers.Select(p => p.Provider.GetType())))
            {
                var instance = (IUploadProvider)Activator.CreateInstance(toAdd);
                var info = new UploadProviderInfo(instance);

                if (ProviderConfig.TryGetValue(toAdd.Name, out var config))
                {
                    ApplyConfig(info, config);
                }
                else if (instance is IBuiltInProvider)
                {
                    // no persisted entry means this provider has never been seen — the same state
                    // on a fresh install and on an upgrade that predates the provider — so a
                    // built-in starts switched on. As soon as the user touches it SyncToConfig
                    // writes the key and their choice wins from then on.
                    info.IsEnabled = true;
                    seeded.Add(info);
                }

                // subscribe after applying saved state so startup does not look like a user edit
                info.PropertyChanged += (s, e) => SyncToConfig((UploadProviderInfo)s);
                instance.PropertyChanged += (s, e) => SyncToConfig(info);

                _providers.Add(info);
            }

            // claiming defaults has to wait until every provider's saved config has been applied:
            // discovery order is arbitrary, so before the loop finishes a type can look unclaimed
            // when a provider yet to be constructed owns it. Only fills empty slots — a default
            // the user picked themselves is never stomped.
            foreach (var info in seeded)
            {
                foreach (var uploadType in _defaultableTypes)
                {
                    if (!info.Provider.SupportedUpload.HasFlag(uploadType))
                        continue;

                    if (_providers.Any(p => p.DefaultFor.HasFlag(uploadType)))
                        continue;

                    SetDefaultProvider(info, uploadType);
                }
            }

            // built-ins first, then alphabetical
            _providers = _providers
                .OrderByDescending(p => p.Provider is IBuiltInProvider)
                .ThenBy(p => p.Provider.Name, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>Looks up the runtime wrapper for a provider by its type name (the key used in
        /// <see cref="ProviderConfig"/> and in exported strings).</summary>
        public UploadProviderInfo GetProviderByTypeName(string typeName)
        {
            return _providers.FirstOrDefault(p => String.Equals(p.Provider.GetType().Name, typeName, StringComparison.Ordinal));
        }

        /// <summary>Builds an export payload containing the current state of the named providers.
        /// Names that match no discovered provider are skipped.</summary>
        public UploadTransferPayload ExportProviders(IEnumerable<string> typeNames, string appVersion, DateTimeOffset exported)
        {
            var payload = new UploadTransferPayload
            {
                App = appVersion,
                Exported = exported,
            };

            foreach (var typeName in typeNames)
            {
                var info = GetProviderByTypeName(typeName);
                if (info == null)
                    continue;

                // read from the live provider rather than ProviderConfig: a provider the user has
                // never touched has no entry there yet.
                var config = BuildConfig(info);

                payload.Providers[typeName] = new UploadTransferEntry
                {
                    Name = info.Provider.Name,
                    IsEnabled = config.IsEnabled,
                    DefaultFor = UploadSettingsTransfer.FormatUploadTypes(config.DefaultFor),
                    Settings = new Dictionary<string, string>(config.Settings, StringComparer.Ordinal),
                };
            }

            return payload;
        }

        /// <summary>
        /// Replaces one provider's settings with an imported entry, overwriting everything the
        /// entry carries. Returns false when this build has no provider of that type.
        /// </summary>
        /// <remarks>
        /// Settings keys the entry omits are left alone rather than reset — an export written by an
        /// older Clowd should not blank out a field it never knew about. Any upload type the entry
        /// claims a default for is taken away from whichever provider currently holds it, because
        /// two defaults for the same type is not a state the rest of the code expects.
        /// </remarks>
        public bool ImportProvider(string typeName, UploadTransferEntry entry)
        {
            if (entry == null)
                return false;

            var info = GetProviderByTypeName(typeName);
            if (info == null)
                return false;

            ApplyConfig(info, new UploadProviderConfig
            {
                IsEnabled = entry.IsEnabled,
                // applied below through SetDefaultProvider so other providers give up the slot
                DefaultFor = SupportedUploadType.None,
                Settings = entry.Settings,
            });

            var defaultFor = UploadSettingsTransfer.ParseUploadTypes(entry.DefaultFor);
            foreach (var uploadType in _defaultableTypes)
            {
                if (defaultFor.HasFlag(uploadType))
                    SetDefaultProvider(info, uploadType);
                else
                    ClearDefaultProvider(info, uploadType);
            }

            // ApplyConfig/SetDefaultProvider raise PropertyChanged on the wrapper and the provider,
            // which SyncToConfig mirrors into ProviderConfig — but only for providers that changed.
            // Sync explicitly so an import that happens to be a no-op still writes the key.
            SyncToConfig(info);
            return true;
        }

        private static UploadProviderConfig BuildConfig(UploadProviderInfo info)
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

            return config;
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
            ProviderConfig[info.Provider.GetType().Name] = BuildConfig(info);
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

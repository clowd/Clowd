using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;

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

        private UploadProviderInfo()
        {
            // for serializer only
        }

        public UploadProviderInfo(IUploadProvider provider)
        {
            _provider = provider;
        }

        private bool _isEnabled;
        private IUploadProvider _provider;
        private SupportedUploadType _defaultFor;
    }

    public class SettingsUpload : CategoryBase
    {
        public UploadProviderInfo[] Providers => _providers.ToArray();

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

        public void DiscoverProviders()
        {
            // this function searches for and adds any 'IUploadProvider' classes
            // it can find that are not currently listed in the settings.
            // also, it removes any info classes which have a null provider
            // (eg. if it failed to be deserialized)
            // note: no providers ship in this build, so this typically finds nothing.

            foreach (var i in _providers.ToArray())
            {
                if (i.Provider == null)
                    _providers.Remove(i);
            }

            var assembliesToSearch = AppDomain.CurrentDomain.GetAssemblies();
            var type = typeof(IUploadProvider);
            var types = assembliesToSearch
                .SelectMany(GetLoadableTypes)
                .Where(p => !p.IsAbstract && !p.IsInterface)
                .Where(p => type.IsAssignableFrom(p));

            foreach (var toAdd in types.Except(_providers.Select(p => p.Provider.GetType())))
            {
                var instance = (IUploadProvider)Activator.CreateInstance(toAdd);
                _providers.Add(new UploadProviderInfo(instance) { IsEnabled = false });
            }

            _providers = _providers.OrderBy(p => p.Provider.Name, StringComparer.Ordinal).ToList();

            // need to subscribe to all the providers so we can propegate property changed events
            foreach (var p in _providers)
            {
                Subscribe(p);
                Subscribe(p.Provider);
            }
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

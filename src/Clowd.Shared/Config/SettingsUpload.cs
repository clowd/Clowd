using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using RT.Serialization;
using RT.Util;

namespace Clowd.Config
{
    public class UploadProviderInfo : SimpleNotifyObject
    {
        public bool IsEnabled
        {
            get => _isEnabled;
            set => Set(ref _isEnabled, value);
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
    }

    public class SettingsUpload : CategoryBase
    {
        public UploadProviderInfo[] Providers => _providers.ToArray();
        
        private List<UploadProviderInfo> _providers = new();

        public UploadProviderInfo Image
        {
            get => _image?.IsEnabled == true ? _image : null;
            set => Set(ref _image, value);
        }

        public UploadProviderInfo Video
        {
            get => _video?.IsEnabled == true ? _video : null;
            set => Set(ref _video, value);
        }

        public UploadProviderInfo Binary
        {
            get => _binary?.IsEnabled == true ? _binary : null;
            set => Set(ref _binary, value);
        }

        public UploadProviderInfo Text
        {
            get => _text?.IsEnabled == true ? _text : null;
            set => Set(ref _text, value);
        }

        private UploadProviderInfo _image;
        private UploadProviderInfo _video;
        private UploadProviderInfo _binary;
        private UploadProviderInfo _text;

        public SettingsUpload()
        {
        }

        public IEnumerable<UploadProviderInfo> GetEnabledProviders(SupportedUploadType type)
        {
            return Providers
                .Where(p => p.IsEnabled)
                .Where(p => p.Provider.SupportedUpload == SupportedUploadType.All || p.Provider.SupportedUpload.HasFlag(type))
                .Select(p => p);
        }

        protected override void AfterDeserializeInternal()
        {
            // add any providers to the list which are 
            var assembliesToSearch = AppDomain.CurrentDomain.GetAssemblies();
            var type = typeof(IUploadProvider);
            var types = assembliesToSearch
                .SelectMany(s => s.GetTypes())
                .Where(p => !p.IsAbstract && !p.IsInterface)
                .Where(p => type.IsAssignableFrom(p));

            foreach (var toAdd in types.Except(_providers.Select(p => p.Provider.GetType())))
            {
                var instance = (IUploadProvider)Activator.CreateInstance(toAdd);
                _providers.Add(new UploadProviderInfo(instance) { IsEnabled = false });
            }

            _providers.Sort(CustomComparer<UploadProviderInfo>.By(p => p.Provider.Name));

            if (_image != null && _providers.SingleOrDefault(p => p == _image)?.IsEnabled != true)
                _image = null;

            if (_video != null && _providers.SingleOrDefault(p => p == _video)?.IsEnabled != true)
                _video = null;

            if (_binary != null && _providers.SingleOrDefault(p => p == _binary)?.IsEnabled != true)
                _binary = null;

            if (_text != null && _providers.SingleOrDefault(p => p == _text)?.IsEnabled != true)
                _text = null;

            foreach (var p in _providers)
            {
                Subscribe(p);
                Subscribe(p.Provider);
            }
        }
    }
}

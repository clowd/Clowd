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
        public IUploadProvider Provider
        {
            get => _provider;
            set => Set(ref _provider, value);
        }
        
        public bool IsEnabled
        {
            get => _isEnabled;
            set => Set(ref _isEnabled, value);
        }
        
        private IUploadProvider _provider;
        private bool _isEnabled;
    }
    
    public class SettingsUpload : CategoryBase
    {
        public List<UploadProviderInfo> Providers { get; private set; } = new List<UploadProviderInfo>();
        
        public List<UploadProviderInfo> EnabledProviders => Providers.Where(x => x.IsEnabled).ToList();

        public UploadProviderInfo Image
        {
            get => _image;
            set => Set(ref _image, value);
        }

        public UploadProviderInfo Video
        {
            get => _video;
            set => Set(ref _video, value);
        }

        public UploadProviderInfo Binary
        {
            get => _binary;
            set => Set(ref _binary, value);
        }

        public UploadProviderInfo Text
        {
            get => _text;
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
        
            foreach (var toAdd in types.Except(Providers.Select(p => p.Provider.GetType())))
            {
                var instance = (IUploadProvider)Activator.CreateInstance(toAdd);
                Providers.Add(new UploadProviderInfo() { Provider = instance, IsEnabled = false });
            }
        
            Providers.Sort(CustomComparer<UploadProviderInfo>.By(p => p.Provider.Name));
            
            if (_image != null && Providers.SingleOrDefault(p => p == _image)?.IsEnabled != true)
                _image = null;
        
            if (_video != null && Providers.SingleOrDefault(p => p == _video)?.IsEnabled != true)
                _video = null;
        
            if (_binary != null && Providers.SingleOrDefault(p => p == _binary)?.IsEnabled != true)
                _binary = null;
        
            if (_text != null && Providers.SingleOrDefault(p => p == _text)?.IsEnabled != true)
                _text = null;
        }
    }
}

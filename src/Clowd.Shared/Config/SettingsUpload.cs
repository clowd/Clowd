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
    public class SettingsUpload : CategoryBase
    {
        public List<IUploadProvider> Providers { get; private set; } = new List<IUploadProvider>();

        public IUploadProvider Image
        {
            get => _image;
            set => Set(ref _image, value);
        }

        public IUploadProvider Video
        {
            get => _video;
            set => Set(ref _video, value);
        }

        public IUploadProvider Binary
        {
            get => _binary;
            set => Set(ref _binary, value);
        }

        public IUploadProvider Text
        {
            get => _text;
            set => Set(ref _text, value);
        }

        private IUploadProvider _image;
        private IUploadProvider _video;
        private IUploadProvider _binary;
        private IUploadProvider _text;

        public SettingsUpload()
        {
            Subscribe(Image, Video, Binary, Text);
        }

        public IEnumerable<IUploadProvider> GetEnabledProviders(SupportedUploadType type)
        {
            return Providers
                .Where(p => p.IsEnabled)
                .Where(p => p.SupportedUpload == SupportedUploadType.All || p.SupportedUpload.HasFlag(type));
        }

        protected override void AfterDeserializeInternal()
        {
            // add any providers to the list which are 
            var assembliesToSearch = new Assembly[] { Assembly.GetExecutingAssembly() };
            var type = typeof(IUploadProvider);
            var types = assembliesToSearch
                .SelectMany(s => s.GetTypes())
                .Where(p => type.IsAssignableFrom(p))
                .Where(p => !p.IsAbstract && !p.IsInterface);

            foreach (var toAdd in types.Except(Providers.Select(p => p.GetType())))
            {
                var instance = (IUploadProvider)Activator.CreateInstance(toAdd);
                Providers.Add(instance);
            }

            Providers = Providers.OrderBy(p => p.Name).ToList();

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

using RT.Serialization;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Clowd.Upload
{
    public class UploadSettings : INotifyPropertyChanged, IClassifyObjectProcessor
    {
        public List<IUploadProvider> Providers { get; private set; } = new List<IUploadProvider>();

        public IUploadProvider Image
        {
            get => _image;
            set
            {
                if (_image != value)
                {
                    _image = value;
                    OnPropertyChanged(nameof(Image));
                }
            }
        }

        public IUploadProvider Video
        {
            get => _video;
            set
            {
                if (_video != value)
                {
                    _video = value;
                    OnPropertyChanged(nameof(Video));
                }
            }
        }

        public IUploadProvider Binary
        {
            get => _binary;
            set
            {
                if (_binary != value)
                {
                    _binary = value;
                    OnPropertyChanged(nameof(Binary));
                }
            }
        }

        public IUploadProvider Text
        {
            get => _text;
            set
            {
                if (_text != value)
                {
                    _text = value;
                    OnPropertyChanged(nameof(Text));
                }
            }
        }

        public UploadSettings()
        {
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private IUploadProvider _image;
        private IUploadProvider _video;
        private IUploadProvider _binary;
        private IUploadProvider _text;

        public IEnumerable<IUploadProvider> GetEnabledProviders(SupportedUploadType type)
        {
            return Providers
                .Where(p => p.IsEnabled)
                .Where(p => p.SupportedUpload == SupportedUploadType.All || p.SupportedUpload.HasFlag(type));
        }

        protected virtual void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public void BeforeSerialize()
        {
        }

        public void AfterDeserialize()
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

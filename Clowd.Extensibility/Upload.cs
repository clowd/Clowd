using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Clowd
{
    public delegate void UploadProgressHandler(long bytesUploaded);

    public class UploadResult
    {
        public UploadProviderBase Provider { get; set; }
        public string UploadKey { get; set; }
        public DateTimeOffset UploadTime { get; set; }
        public string ContentType { get; set; }
        public string FileName { get; set; }
        public string PublicUrl { get; set; }
    }

    [Flags]
    public enum SupportedUploadType
    {
        None = 1 << 0,
        Image = 1 << 1,
        Video = 1 << 2,
        Binary = 1 << 3,
        Text = 1 << 4,
        All = Image | Video | Binary | Text,
    }

    public interface IUploadProvider : INotifyPropertyChanged
    {
        string Name { get; }

        string Description { get; }

        Stream Icon { get; }

        SupportedUploadType SupportedUpload { get; }

        [Browsable(false)]
        bool IsEnabled { get; set; }

        //Task<UploadResult> UploadAsync(string filePath, UploadProgressHandler progress, string uploadName);
        Task<UploadResult> UploadAsync(string filePath, UploadProgressHandler progress, string uploadName, CancellationToken cancelToken);
        //Task<UploadResult> UploadAsync(Stream fileStream, UploadProgressHandler progress, string uploadName);
        Task<UploadResult> UploadAsync(Stream fileStream, UploadProgressHandler progress, string uploadName, CancellationToken cancelToken);
    }

    public abstract class UploadProviderBase : IUploadProvider
    {
        [Browsable(false)]
        public virtual bool IsEnabled
        {
            get => _enabled;
            set
            {
                if (value != _enabled)
                {
                    _enabled = value;
                    OnPropertyChanged(nameof(IsEnabled));
                }
            }
        }

        [Browsable(false)]
        public virtual SupportedUploadType SupportedUpload { get; }

        [Browsable(false)]
        public virtual string Name { get; }

        [Browsable(false)]
        public virtual string Description { get; }

        [Browsable(false)]
        public virtual Stream Icon => EmbeddedResource.GetStream("Clowd", "default-provider-icon.png");

        public virtual event PropertyChangedEventHandler PropertyChanged;

        private bool _enabled;

        protected UploadProviderBase(string name, string description, SupportedUploadType supported)
        {
            Name = name;
            Description = description;
            SupportedUpload = supported;
        }

        //public virtual Task<UploadResult> UploadAsync(string filePath, UploadProgressHandler progress, string uploadName)
        //{
        //    return UploadAsync(filePath, progress, uploadName, CancellationToken.None);
        //}

        public virtual async Task<UploadResult> UploadAsync(string filePath, UploadProgressHandler progress, string uploadName, CancellationToken cancelToken)
        {
            using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            {
                return await UploadAsync(fs, progress, uploadName, cancelToken);
            }
        }

        //public virtual Task<UploadResult> UploadAsync(Stream fileStream, UploadProgressHandler progress, string uploadName)
        //{
        //    return UploadAsync(fileStream, progress, uploadName, CancellationToken.None);
        //}

        public abstract Task<UploadResult> UploadAsync(Stream fileStream, UploadProgressHandler progress, string uploadName, CancellationToken cancelToken);

        protected virtual void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

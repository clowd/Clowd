using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Clowd
{
    public delegate void UploadProgressHandler(long bytesUploaded);

    public class UploadResult
    {
        public UploadProviderBase Provider { get; set; }
        public string UploadKey { get; set; }
        public string DeleteKey { get; set; }
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
        public abstract SupportedUploadType SupportedUpload { get; }

        [Browsable(false)]
        public abstract string Name { get; }

        [Browsable(false)]
        public abstract string Description { get; }

        [Browsable(false)]
        public virtual Stream Icon => EmbeddedResource.GetStream("Clowd", "default-provider-icon.png");

        public virtual event PropertyChangedEventHandler PropertyChanged;

        private bool _enabled;

        protected UploadProviderBase()
        {
        }

        public virtual async Task<UploadResult> UploadAsync(string filePath, UploadProgressHandler progress, string uploadName, CancellationToken cancelToken)
        {
            using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            {
                return await UploadAsync(fs, progress, uploadName, cancelToken);
            }
        }

        public abstract Task<UploadResult> UploadAsync(Stream fileStream, UploadProgressHandler progress, string uploadName, CancellationToken cancelToken);

        protected virtual void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

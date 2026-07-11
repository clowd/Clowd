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
        public IUploadProvider Provider { get; set; }
        public string UploadKey { get; set; }
        public string DeleteKey { get; set; }
        public DateTimeOffset UploadTime { get; set; }
        public string ContentType { get; set; }
        public string FileName { get; set; }
        public string PublicUrl { get; set; }
    }

    /// <summary>The persisted keys needed to delete a previous upload from its provider.</summary>
    public class UploadDeleteInfo
    {
        public string UploadKey { get; set; }
        public string DeleteKey { get; set; }
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

        Task<UploadResult> UploadAsync(string filePath, UploadProgressHandler progress, string uploadName, CancellationToken cancelToken);

        Task<UploadResult> UploadAsync(Stream fileStream, UploadProgressHandler progress, string uploadName, CancellationToken cancelToken);

        /// <summary>True when this provider can delete the given previously-uploaded file.</summary>
        bool CanDelete(UploadDeleteInfo info);

        Task DeleteAsync(UploadDeleteInfo info, CancellationToken cancelToken);
    }
}

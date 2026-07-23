using System;
using System.Text.Json.Serialization;

namespace Clowd
{
    public sealed class UploadRecord
    {
        public string Provider { get; set; }   // provider type name, e.g. nameof(ImgurUploadProvider)
        public string Url { get; set; }
        public string FileName { get; set; }
        public string UploadKey { get; set; }
        public string DeleteKey { get; set; }
        public DateTime UploadedUtc { get; set; }
        public bool Accelerated { get; set; }

        [JsonIgnore] public bool CanDelete => UploadManager.CanDeleteUpload(this);
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Handlers;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;

namespace Clowd.Upload
{
    public class VgyMeUploadProvider : UploadProviderBase
    {
        public override string Name => "vgy.me";
        public override string Description => "A free image hosting service with private albums";
        public override SupportedUploadType SupportedUpload => SupportedUploadType.Image;
        public override Stream Icon => new Resource().VgyMeIcon;

        public string UserKey
        {
            get => _userKey;
            set => Set(ref _userKey, value);
        }
        
        private string _userKey;

        public override async Task<UploadResult> UploadAsync(Stream fileStream, UploadProgressHandler progress, string uploadName, CancellationToken cancelToken)
        {
            if (UserKey == null)
                throw new ArgumentNullException("UserKey must not be empty.");

            Dictionary<string, string> args = new()
            {
                { "userkey", UserKey }
            };

            var resp = await SendFileAsFormData("https://vgy.me/upload", fileStream, "file", progress, uploadName, args);
            var obj = JsonSerializer.Deserialize(resp, UploadJsonContext.Default.VgyResponse);

            return new UploadResult()
            {
                Provider = this,
                DeleteKey = obj.delete,
                PublicUrl = obj.image,
                FileName = obj.filename,
                UploadKey = obj.filename,
                UploadTime = DateTimeOffset.Now,
            };
        }

        // the upload response's `delete` field is a full delete URL.
        public override bool CanDelete(UploadDeleteInfo info)
            => !String.IsNullOrEmpty(info.DeleteKey) && info.DeleteKey.StartsWith("http", StringComparison.OrdinalIgnoreCase);

        public override async Task DeleteAsync(UploadDeleteInfo info, CancellationToken cancelToken)
        {
            using var http = GetHttpClient(TimeSpan.FromSeconds(30));
            using var resp = await http.GetAsync(info.DeleteKey, cancelToken);
            if (!resp.IsSuccessStatusCode)
                throw new Exception($"Failed to delete upload (error {resp.StatusCode}).");
        }
    }

    internal class VgyResponse
    {
        public bool error { get; set; }
        public int filesize { get; set; }
        public string filename { get; set; }
        public string ext { get; set; }
        public string url { get; set; }
        public string image { get; set; }
        public string delete { get; set; }
        public Dictionary<string, string> messages { get; set; }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Clowd.Upload
{
    public class PicsurUploadProvider : UploadProviderBase
    {
        public override SupportedUploadType SupportedUpload => SupportedUploadType.Image;

        public override string Name => "Picsur";

        public override string Description => "Open source / self-hosted image sharing software";

        public override Stream Icon => new Resource().PicsurIcon;

        public string ApiKey
        {
            get => _apiKey;
            set => Set(ref _apiKey, value);
        }

        public string BaseUrl
        {
            get => _baseUrl;
            set => Set(ref _baseUrl, value);
        }

        public bool CopyDirectLink
        {
            get => _copyDirectLink;
            set => Set(ref _copyDirectLink, value);
        }

        private string _apiKey;
        private string _baseUrl;
        private bool _copyDirectLink;

        public override async Task<UploadResult> UploadAsync(Stream fileStream, UploadProgressHandler progress, string uploadName, CancellationToken cancelToken)
        {
            AuthenticationHeaderValue auth = null;
            if (!String.IsNullOrEmpty(ApiKey))
            {
                auth = new AuthenticationHeaderValue("Api-Key", ApiKey);
            }

            if (String.IsNullOrEmpty(BaseUrl))
            {
                throw new Exception("Must configure BaseUrl in Picsur settings.");
            }

            var bu = BaseUrl.TrimEnd('/', '\\');
            var ulu = bu + "/api/image/upload";
            var json = await SendFileAsFormData(ulu, fileStream, "image", progress, uploadName, auth: auth);
            var parsed = JsonConvert.DeserializeObject<PicsurResponse>(json);
            if (!parsed.success || parsed.data == null || parsed.data.id == null)
                throw new Exception("Failed to upload file. " + (parsed?.data?.message ?? parsed.statusCode.ToString()));

            var ext = Path.GetExtension(uploadName);
            var publicUrl = CopyDirectLink
                ? $"{bu}/i/{parsed.data.id}{ext}"
                : $"{bu}/view/{parsed.data.id}";

            return new UploadResult()
            {
                Provider = this,
                PublicUrl = publicUrl,
                DeleteKey = parsed.data.delete_key,
                FileName = uploadName,
                UploadTime = DateTime.UtcNow,
            };
        }

        private class PicsurData
        {
            public string id { get; set; }
            public string delete_key { get; set; }
            public string message { get; set; }
        }

        private class PicsurResponse
        {
            public bool success { get; set; }
            public int statusCode { get; set; }
            public PicsurData data { get; set; }
        }
    }
}

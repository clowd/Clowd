using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Clowd.Upload
{
    public class HastebinUploadProvider : UploadProviderBase
    {
        public override string Name => "HasteBin";

        public override string Description => "A clean and anonymous (or self-hosted) code sharing site";

        public override SupportedUploadType SupportedUpload => SupportedUploadType.Text;

        public override Stream Icon => new Resource().HastebinIcon;

        public string HasteBinUrl
        {
            get => _hasteBinUrl;
            set => Set(ref _hasteBinUrl, value);
        }

        private string _hasteBinUrl = "https://pastie.io";

        public override async Task<UploadResult> UploadAsync(Stream fileStream, UploadProgressHandler progress, string uploadName, CancellationToken cancelToken)
        {
            var url = HasteBinUrl.TrimEnd('/');
            var result = await SendFileAsContent(url + "/documents", fileStream, progress);
            var resp = JsonConvert.DeserializeObject<HasebinResponse>(result);

            if (resp?.key == null)
                throw new Exception("Empty response");

            var fileUrl = url + "/" + resp.key;

            var ext = Path.GetExtension(uploadName);
            if (!String.IsNullOrWhiteSpace(ext))
                fileUrl += ext;

            return new UploadResult()
            {
                Provider = this,
                PublicUrl = fileUrl,
                FileName = uploadName,
                DeleteKey = resp.secret,
            };
        }

        private class HasebinResponse
        {
            public string key { get; set; }
            
            // pastie.io only, native hastebin does not support 
            public string secret { get; set; } 
        }
    }
}

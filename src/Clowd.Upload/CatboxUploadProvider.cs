using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Handlers;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Clowd.Upload
{
    public class CatboxUploadProvider : UploadProviderBase
    {
        public override string Name => "catbox.moe";
        public override string Description => "A simple, anonymous file host with expiry options (max 200mb)";
        public override SupportedUploadType SupportedUpload => SupportedUploadType.All;
        public override Stream Icon => new Resource().CatboxIcon;

        public CatBoxExpiry ExpireUploads
        {
            get => _expireUploads;
            set => _expireUploads = value;
        }

        private CatBoxExpiry _expireUploads;

        public override async Task<UploadResult> UploadAsync(Stream fileStream, UploadProgressHandler progress, string uploadName, CancellationToken cancelToken)
        {
            // for expiry = never, we use the 'catbox' api (which never expires).
            // for expiry < never, we use the 'litterbox' api, which always expires.
            
            var url = "https://litterbox.catbox.moe/resources/internals/api.php";
            
            Dictionary<string, string> args = new()
            {
                { "reqtype", "fileupload" }
            };

            switch (ExpireUploads)
            {
                case CatBoxExpiry.Never:
                    url = "https://catbox.moe/user/api.php";
                    break;
                case CatBoxExpiry._1h:
                    args.Add("time", "1h");
                    break;
                case CatBoxExpiry._12h:
                    args.Add("time", "12h");
                    break;
                case CatBoxExpiry._24h:
                    args.Add("time", "24h");
                    break;
                case CatBoxExpiry._72h:
                    args.Add("time", "72h");
                    break;
            }

            var resp = await SendFileAsFormData(url, fileStream, "fileToUpload", progress, uploadName, args);
            return new UploadResult()
            {
                Provider = this,
                PublicUrl = resp,
            };
        }

        public enum CatBoxExpiry
        {
            Never = 0,
            _1h = 1,
            _12h = 2,
            _24h = 3,
            _72h = 4,
        }
    }
}

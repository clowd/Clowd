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
            var obj = JsonConvert.DeserializeObject<VgyResponse>(resp);

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

        class VgyResponse
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
}

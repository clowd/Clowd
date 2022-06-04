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
        public override string Description => "A free and easy to use image hosting service";
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
            
            using var http = GetVgyHttpClient(TimeSpan.FromSeconds(30), progress);
            var uri = new Uri("https://vgy.me/upload");

            using var content = new MultipartFormDataContent(RandomEx.GetString(12));
            content.Add(new StreamContent(fileStream), "file", uploadName);
            content.Add(new StringContent(UserKey), "userkey");

            using var resp = await http.PostAsync(uri, content);
            var str = await resp.Content.ReadAsStringAsync();
            var obj = JsonConvert.DeserializeObject<VgyResponse>(str);
            
            if (!resp.IsSuccessStatusCode)
                throw new Exception($"Error uploading to vgy.me ({resp.StatusCode}). {obj?.messages?.FirstOrDefault()}");
            
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
        
        private HttpClient GetVgyHttpClient(TimeSpan timeout, UploadProgressHandler progress = null)
        {
            var handler = new HttpClientHandler() { AllowAutoRedirect = true };
            var ph = new ProgressMessageHandler(handler);

            if (progress != null)
            {
                ph.HttpSendProgress += (sender, args) =>
                {
                    // progress(args.BytesTransferred, args.TotalBytes);
                    progress(args.BytesTransferred);
                };
            }

            var client = new HttpClient(ph);
            client.Timeout = timeout;
            client.DefaultRequestHeaders.Accept.Clear();
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            return client;
        }
    }
}

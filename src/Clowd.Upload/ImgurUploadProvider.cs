﻿using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Clowd.Upload
{
    public class ImgurUploadProvider : UploadProviderBase
    {
        public override string Name => "Imgur.com";
        public override string Description => "Media sharing where all uploads are shown on the front page";
        public override SupportedUploadType SupportedUpload => SupportedUploadType.Image | SupportedUploadType.Video;
        public override Stream Icon => new Resource().ImgurIcon;

        public string ClientId
        {
            get => _clientId;
            set => Set(ref _clientId, value);
        }

        private string _clientId;

        public override async Task<UploadResult> UploadAsync(Stream fileStream, UploadProgressHandler progress, string uploadName,
            CancellationToken cancelToken)
        {
            if (ClientId == null)
                throw new ArgumentNullException("Client-ID must not be empty.");

            var auth = new System.Net.Http.Headers.AuthenticationHeaderValue("Client-ID", "c3bda1f4e978e28");

            var args = new Dictionary<string, string>()
            {
                { "type", "file" }
            };

            var json = await SendFileAsFormData("https://api.imgur.com/3/upload", fileStream, "image", progress, uploadName, args, auth: auth);
            var parsed = JsonConvert.DeserializeObject<ImgurApiResponse>(json);
            if (!parsed.success)
                throw new Exception("Failed to upload file: " + parsed.status);

            return new UploadResult()
            {
                ContentType = parsed.data.type,
                FileName = uploadName,
                Provider = this,
                PublicUrl = parsed.data.link,
                UploadKey = parsed.data.id,
                DeleteKey = parsed.data.deletehash,
                UploadTime = parsed.data.datetime.HasValue ? FromUnixTime(parsed.data.datetime.Value) : DateTime.Now,
            };
        }

        private static readonly DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static DateTime FromUnixTime(long unixTime)
        {
            return epoch.AddSeconds(unixTime);
        }

        private class ImgurObjectData
        {
            public string id { get; set; }
            public string title { get; set; }
            public string description { get; set; }
            public int? datetime { get; set; }
            public string type { get; set; }
            public bool? animated { get; set; }
            public int? width { get; set; }
            public int? height { get; set; }
            public int? size { get; set; }
            public int? views { get; set; }
            public int? bandwidth { get; set; }
            public object vote { get; set; }
            public bool? favorite { get; set; }
            public object nsfw { get; set; }
            public object section { get; set; }
            public string account_url { get; set; }
            public int? account_id { get; set; }
            public bool? is_ad { get; set; }
            public bool? in_most_viral { get; set; }
            public List<string> tags { get; set; }
            public int? ad_type { get; set; }
            public string ad_url { get; set; }
            public bool? in_gallery { get; set; }
            public string deletehash { get; set; }
            public string name { get; set; }
            public string link { get; set; }
        }

        private class ImgurApiResponse
        {
            public ImgurObjectData data { get; set; }
            public bool success { get; set; }
            public int status { get; set; }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using B2Net;
using B2Net.Http;
using B2Net.Http.RequestGenerators;
using B2Net.Models;
using Newtonsoft.Json;
using RT.Serialization;

namespace Clowd.Upload
{
    public class BackBlazeUploadProvider : UploadProviderBase
    {
        public override string Name => "BackBlaze B2";

        public override string Description => "Uploads any file to a public B2 bucket";

        public override SupportedUploadType SupportedUpload => SupportedUploadType.All;

        public override Stream Icon => new Resource().BackBlazeIcon;

        public string BucketId
        {
            get => _bucketId;
            set => Set(ref _bucketId, value);
        }
        
        public string KeyId
        {
            get => _keyId;
            set => Set(ref _keyId, value);
        }
        
        public string ApplicationKey
        {
            get => _applicationKey;
            set => Set(ref _applicationKey, value);
        }
        
        private string _bucketId;
        private string _keyId;
        private string _applicationKey;
        
        [ClassifyIgnore]
        private readonly IMimeProvider _mimeDb = new MimeProvider();

        public override async Task<UploadResult> UploadAsync(Stream fileStream, UploadProgressHandler progress, string uploadName, CancellationToken cancelToken)
        {
            var mimeType = _mimeDb.GetMimeFromExtension(Path.GetExtension(uploadName)).ContentType;

            var options = new B2Options() {
                KeyId = _keyId,
                ApplicationKey = _applicationKey,
                BucketId = _bucketId,
                PersistBucket = true
            };

            using HttpClient http = HttpClientFactory.CreateHttpClient(options.RequestTimeout);
            
            var operationalBucketId = Utilities.DetermineBucketId(options, _bucketId);
            
            var uploadUrlRequest = FileUploadRequestGenerators.GetUploadUrl(options, operationalBucketId);
            var uploadUrlResponse = await http.SendAsync(uploadUrlRequest, cancelToken);
            var uploadUrlData = await uploadUrlResponse.Content.ReadAsStringAsync();
            var uploadUrlObject = JsonConvert.DeserializeObject<B2UploadUrl>(uploadUrlData);
            options.UploadAuthorizationToken = uploadUrlObject.AuthorizationToken;
            
            // https://www.backblaze.com/b2/docs/b2_upload_file.html
            var requestMessage = GetUploadReq(options, uploadUrlObject.UploadUrl, fileStream, uploadName);
            var response = await http.SendAsync(requestMessage, cancelToken);

            var b2file = await ResponseParser.ParseResponse<B2File>(response, "Files");

            var url = $"{options.DownloadUrl}/file/{_bucketId}/{uploadName}";

            return new UploadResult()
            {
                ContentType = mimeType,
                Provider = this,
                FileName = uploadName,
                PublicUrl = url,
                UploadTime = DateTimeOffset.Now,
            };


            // var operationalBucketId = Utilities.DetermineBucketId(options, _bucketId);
            //
            // // Get the upload url for this file
            // var uploadUrlRequest = FileUploadRequestGenerators.GetUploadUrl(options, operationalBucketId);
            //
            //
            // using var req = Upload(options, )
            //
            //
            // string hash = GetSHA1Hash(fileStream);
            // fileStream.Position = 0;
            //
            // var client = new B2Client(options, authorizeOnInitialize: true);
            // client.Files.Upload()
            // FileUploadRequestGenerators.

            // await client.Files.Upload(fileStream, 

        }
        
        private static HttpRequestMessage GetUploadReq(B2Options options, string uploadUrl, Stream fileData, string fileName, Dictionary<string, string> fileInfo = null, string contentType = "") {
            
            string hash = GetSHA1Hash(fileData);
            fileData.Position = 0;
            
            var uri = new Uri(uploadUrl);
            var request = new HttpRequestMessage() {
                Method = HttpMethod.Post,
                RequestUri = uri,
                Content = new StreamContent(fileData)
            };

            request.Headers.TryAddWithoutValidation("Authorization", options.UploadAuthorizationToken);
            request.Headers.Add("X-Bz-File-Name", fileName.b2UrlEncode());
            request.Headers.Add("X-Bz-Content-Sha1", hash);
            if (fileInfo != null && fileInfo.Count > 0) {
                foreach (var info in fileInfo.Take(10)) {
                    request.Headers.Add($"X-Bz-Info-{info.Key}", info.Value);
                }
            }

            request.Content.Headers.ContentType = new MediaTypeHeaderValue(string.IsNullOrWhiteSpace(contentType) ? "b2/x-auto" : contentType);
            request.Content.Headers.ContentLength = fileData.Length;

            return request;
        }
        
        private static string GetSHA1Hash(Stream fileData) {
            using (var sha1 = SHA1.Create()) {
                return HexStringFromBytes(sha1.ComputeHash(fileData));
            }
        }

        private static string HexStringFromBytes(byte[] bytes) {
            var sb = new StringBuilder();
            foreach (byte b in bytes) {
                var hex = b.ToString("x2");
                sb.Append(hex);
            }
            return sb.ToString();
        }
    }
}

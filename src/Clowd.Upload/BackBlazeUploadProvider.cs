using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Handlers;
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

        public string BucketName
        {
            get => _bucketName;
            set => Set(ref _bucketName, value);
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

        private string _bucketName;
        private string _keyId;
        private string _applicationKey;

        [ClassifyIgnore] private readonly IMimeProvider _mimeDb = new MimeProvider();

        public override async Task<UploadResult> UploadAsync(Stream fileStream, UploadProgressHandler progress, string uploadName,
            CancellationToken cancelToken)
        {
            var mimeType = _mimeDb.GetMimeFromExtension(Path.GetExtension(uploadName)).ContentType;

            var options = new B2Options()
            {
                KeyId = _keyId,
                ApplicationKey = _applicationKey,
            };

            options = await B2Client.AuthorizeAsync(options);
            
            var bucketId = await GetBucketId(options, _bucketName, cancelToken);
            var uploadUrlObject = await GetUploadUrl(options, bucketId, cancelToken);
            options.UploadAuthorizationToken = uploadUrlObject.AuthorizationToken;

            using var http = GetB2HttpClient(TimeSpan.FromMinutes(60), progress);
            
            // https://www.backblaze.com/b2/docs/b2_upload_file.html
            var requestMessage = GetUploadReq(options, uploadUrlObject.UploadUrl, fileStream, uploadName);
            var response = await http.SendAsync(requestMessage, cancelToken);

            // check for errors
            await ResponseParser.ParseResponse<B2File>(response, "Files");

            var url = $"{options.DownloadUrl}/file/{_bucketName}/{uploadName}";

            return new UploadResult()
            {
                ContentType = mimeType,
                Provider = this,
                FileName = uploadName,
                PublicUrl = url,
                UploadTime = DateTimeOffset.Now,
            };
        }

        private static HttpRequestMessage GetUploadReq(B2Options options, string uploadUrl, Stream fileData, string fileName,
            Dictionary<string, string> fileInfo = null, string contentType = "")
        {
            string hash = GetSHA1Hash(fileData);
            fileData.Position = 0;

            var uri = new Uri(uploadUrl);
            var request = new HttpRequestMessage() { Method = HttpMethod.Post, RequestUri = uri, Content = new StreamContent(fileData) };

            request.Headers.TryAddWithoutValidation("Authorization", options.UploadAuthorizationToken);
            request.Headers.Add("X-Bz-File-Name", fileName.b2UrlEncode());
            request.Headers.Add("X-Bz-Content-Sha1", hash);
            if (fileInfo != null && fileInfo.Count > 0)
            {
                foreach (var info in fileInfo.Take(10))
                {
                    request.Headers.Add($"X-Bz-Info-{info.Key}", info.Value);
                }
            }

            request.Content.Headers.ContentType = new MediaTypeHeaderValue(string.IsNullOrWhiteSpace(contentType) ? "b2/x-auto" : contentType);
            request.Content.Headers.ContentLength = fileData.Length;

            return request;
        }
        
        private async Task<string> GetBucketId(B2Options options, string bucketName, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(bucketName))
                throw new ArgumentNullException(nameof(bucketName));
            
            using var http = GetB2HttpClient(TimeSpan.FromSeconds(10));
            
            var json = JsonConvert.SerializeObject(new { accountId = options.AccountId, bucketName });
            var req = BaseRequestGenerator.PostRequest("b2_list_buckets", json, options);
            var response = await http.SendAsync(req, token);

            if (!response.IsSuccessStatusCode)
                throw new Exception($"Error retrieving bucket id ({response.StatusCode}).");

            var bucketList = await ResponseParser.ParseResponse<B2BucketListDeserializeModel>(response);

            if (bucketList.Buckets.Count == 0)
                throw new Exception($"Bucket '{bucketName}' was not found.");

            return bucketList.Buckets.First().BucketId;
        }

        private async Task<B2UploadUrl> GetUploadUrl(B2Options options, string bucketId, CancellationToken token)
        {
            using var http = GetB2HttpClient(TimeSpan.FromSeconds(10));
            var uploadUrlRequest = FileUploadRequestGenerators.GetUploadUrl(options, bucketId);
            var uploadUrlResponse = await http.SendAsync(uploadUrlRequest, token);
            return await ResponseParser.ParseResponse<B2UploadUrl>(uploadUrlResponse);
        }

        private HttpClient GetB2HttpClient(TimeSpan timeout, UploadProgressHandler progress = null)
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
        
        private static string GetSHA1Hash(Stream fileData)
        {
            using (var sha1 = SHA1.Create())
            {
                return HexStringFromBytes(sha1.ComputeHash(fileData));
            }
        }

        private static string HexStringFromBytes(byte[] bytes)
        {
            var sb = new StringBuilder();
            foreach (byte b in bytes)
            {
                var hex = b.ToString("x2");
                sb.Append(hex);
            }

            return sb.ToString();
        }
    }
}

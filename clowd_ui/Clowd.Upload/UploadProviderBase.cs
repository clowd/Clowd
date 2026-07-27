using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Net.Http.Handlers;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using Amazon.Runtime;
using System.Threading.Tasks;

namespace Clowd
{
    // formerly part of Clowd.Shared/Upload.cs in the WPF repo; the interfaces stayed in
    // Clowd.Shared and the http helper base class moved here with the providers.
    public abstract class UploadProviderBase : SimpleNotifyObject, IUploadProvider
    {
        private static readonly byte[] TestPng =
        {
            0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a,
            0x00, 0x00, 0x00, 0x0d, 0x49, 0x48, 0x44, 0x52,
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
            0x08, 0x04, 0x00, 0x00, 0x00, 0xb5, 0x1c, 0x0c,
            0x02, 0x00, 0x00, 0x00, 0x0b, 0x49, 0x44, 0x41,
            0x54, 0x78, 0xda, 0x63, 0xfc, 0xff, 0x1f, 0x00,
            0x03, 0x03, 0x02, 0x00, 0xef, 0xa2, 0xa7, 0x5b,
            0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4e, 0x44,
            0xae, 0x42, 0x60, 0x82,
        };

        private static readonly string _userAgent =
            "Clowd/" + (typeof(UploadProviderBase).Assembly.GetName().Version?.ToString() ?? "1.0");

        [Browsable(false)] public abstract SupportedUploadType SupportedUpload { get; }

        [Browsable(false)] public abstract string Name { get; }

        [Browsable(false)] public abstract string Description { get; }

        [Browsable(false)] public abstract Stream Icon { get; }

        protected UploadProviderBase()
        { }

        public virtual async Task<UploadResult> UploadAsync(string filePath, UploadProgressHandler progress, string uploadName, CancellationToken cancelToken)
        {
            using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            {
                return await UploadAsync(fs, progress, uploadName, cancelToken);
            }
        }

        public abstract Task<UploadResult> UploadAsync(Stream fileStream, UploadProgressHandler progress, string uploadName, CancellationToken cancelToken);

        public virtual async Task TestAsync(CancellationToken cancelToken)
        {
            bool supportsText = SupportedUpload.HasFlag(SupportedUploadType.Text);
            byte[] payload = supportsText ? Encoding.UTF8.GetBytes("Clowd upload test\n") : TestPng;
            string fileName = supportsText ? "clowd-test.txt" : "clowd-test.png";

            using var stream = new MemoryStream(payload, false);
            var result = await UploadAsync(stream, _ => { }, fileName, cancelToken);
            if (String.IsNullOrWhiteSpace(result?.PublicUrl))
                throw new InvalidOperationException($"{Name} upload test failed because no public URL was returned.");

            var deleteInfo = new UploadDeleteInfo
            {
                UploadKey = result.UploadKey,
                DeleteKey = result.DeleteKey,
                FileName = result.FileName,
                PublicUrl = result.PublicUrl,
            };

            try
            {
                if (CanDelete(deleteInfo))
                    await DeleteAsync(deleteInfo, cancelToken);
            }
            catch
            { }
        }

        /// <summary>Flattens an upload failure into a diagnostic string: the exception chain's
        /// messages plus protocol details (HTTP status, service error code, request id) that the
        /// SDKs bury in exception properties rather than the message.</summary>
        public static string DescribeError(Exception ex)
        {
            var sb = new StringBuilder();
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (sb.Length > 0)
                    sb.AppendLine().Append("Caused by ");
                sb.Append(e.GetType().Name).Append(": ").Append(e.Message);
                if (e is AmazonServiceException aws)
                {
                    sb.Append($" (HTTP {(int)aws.StatusCode}");
                    if (!String.IsNullOrEmpty(aws.ErrorCode))
                        sb.Append($", error code {aws.ErrorCode}");
                    if (!String.IsNullOrEmpty(aws.RequestId))
                        sb.Append($", request id {aws.RequestId}");
                    sb.Append(')');
                }
                else if (e is HttpRequestException { StatusCode: not null } http)
                {
                    sb.Append($" (HTTP {(int)http.StatusCode.Value})");
                }
            }

            return sb.ToString();
        }

        /// <summary>Upload overload that can surface a shareable URL early (before the transfer
        /// finishes) via <paramref name="urlAvailable"/>. The base implementation ignores it and
        /// delegates to the standard upload; only providers that support the clwd.app accelerated
        /// flow (Azure, S3) override it.</summary>
        public virtual Task<UploadResult> UploadAsync(
            Stream fileStream, UploadProgressHandler progress, UploadUrlHandler urlAvailable, string uploadName, CancellationToken cancelToken)
            => UploadAsync(fileStream, progress, uploadName, cancelToken);

        public virtual async Task<UploadResult> UploadAsync(
            string filePath, UploadProgressHandler progress, UploadUrlHandler urlAvailable, string uploadName, CancellationToken cancelToken)
        {
            using FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            return await UploadAsync(fs, progress, urlAvailable, uploadName, cancelToken);
        }

        public virtual bool CanDelete(UploadDeleteInfo info) => false;

        public virtual Task DeleteAsync(UploadDeleteInfo info, CancellationToken cancelToken)
            => throw new NotSupportedException($"{Name} does not support deleting uploads.");

        protected HttpClient GetHttpClient(
            TimeSpan timeout, UploadProgressHandler progress = null, string accept = "application/json",
            AuthenticationHeaderValue auth = null)
        {
            var handler = new HttpClientHandler() { AllowAutoRedirect = true };
            var ph = new ProgressMessageHandler(handler);

            if (progress != null)
            {
                ph.HttpSendProgress += (_, args) =>
                {
                    progress(args.BytesTransferred);
                };
            }

            var client = new HttpClient(ph);
            client.Timeout = timeout;
            // catbox.moe drops the connection mid-response when there is no user-agent
            client.DefaultRequestHeaders.UserAgent.ParseAdd(_userAgent);
            client.DefaultRequestHeaders.Accept.Clear();
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
            client.DefaultRequestHeaders.Authorization = auth;

            return client;
        }

        protected async Task<string> SendFileAsFormData(
            string url, Stream fileStream, string formName, UploadProgressHandler progress, string fileName = null,
            Dictionary<string, string> otherFields = null, HttpMethod method = null, string accept = "application/json",
            TimeSpan? timeout = null, AuthenticationHeaderValue auth = null)
        {
            method ??= HttpMethod.Post;
            otherFields ??= new Dictionary<string, string>();
            timeout ??= TimeSpan.FromSeconds(100);

            using var content = new MultipartFormDataContent(Guid.NewGuid().ToString("N").Substring(0, 12));

            if (fileName != null)
            {
                content.Add(new StreamContent(fileStream), formName, fileName);
            }
            else
            {
                content.Add(new StreamContent(fileStream), formName);
            }

            foreach (var f in otherFields)
            {
                content.Add(new StringContent(f.Value), f.Key);
            }

            using var req = new HttpRequestMessage(method, url)
            {
                Content = content,
            };

            using var http = GetHttpClient(timeout.Value, progress, accept, auth);
            using var resp = await http.SendAsync(req);
            var str = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
                throw new Exception($"Send form data failed (error {resp.StatusCode}){Environment.NewLine}{str}");

            return str;
        }

        protected async Task<string> SendFileAsContent(
            string url, Stream fileStream, UploadProgressHandler progress, HttpMethod method = null,
            string accept = "application/json", TimeSpan? timeout = null, AuthenticationHeaderValue auth = null)
        {
            method ??= HttpMethod.Post;
            timeout ??= TimeSpan.FromSeconds(100);

            using var req = new HttpRequestMessage(method, url)
            {
                Content = new StreamContent(fileStream),
            };

            using var http = GetHttpClient(timeout.Value, progress, accept, auth);
            using var resp = await http.SendAsync(req);
            var str = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
                throw new Exception($"Send form data failed (error {resp.StatusCode}){Environment.NewLine}{str}");

            return str;
        }
    }
}

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Net.Http.Handlers;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Clowd
{
    // formerly part of Clowd.Shared/Upload.cs in the WPF repo; the interfaces stayed in
    // Clowd.Shared and the http helper base class moved here with the providers.
    public abstract class UploadProviderBase : SimpleNotifyObject, IUploadProvider
    {
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

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
    public delegate void UploadProgressHandler(long bytesUploaded);

    public class UploadResult
    {
        public UploadProviderBase Provider { get; set; }
        public string UploadKey { get; set; }
        public string DeleteKey { get; set; }
        public DateTimeOffset UploadTime { get; set; }
        public string ContentType { get; set; }
        public string FileName { get; set; }
        public string PublicUrl { get; set; }
    }

    [Flags]
    public enum SupportedUploadType
    {
        None = 1 << 0,
        Image = 1 << 1,
        Video = 1 << 2,
        Binary = 1 << 3,
        Text = 1 << 4,
        All = Image | Video | Binary | Text,
    }

    public interface IUploadProvider : INotifyPropertyChanged
    {
        string Name { get; }

        string Description { get; }

        Stream Icon { get; }

        SupportedUploadType SupportedUpload { get; }

        Task<UploadResult> UploadAsync(string filePath, UploadProgressHandler progress, string uploadName, CancellationToken cancelToken);

        Task<UploadResult> UploadAsync(Stream fileStream, UploadProgressHandler progress, string uploadName, CancellationToken cancelToken);
    }

    public abstract class UploadProviderBase : SimpleNotifyObject, IUploadProvider
    {
        [Browsable(false)] public abstract SupportedUploadType SupportedUpload { get; }

        [Browsable(false)] public abstract string Name { get; }

        [Browsable(false)] public abstract string Description { get; }

        [Browsable(false)] public virtual Stream Icon => EmbeddedResource.GetStream("Clowd", "default-provider-icon.png");

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

        protected async Task<string> SendFormDataFile(
            string url, Stream fileStream, string formName, UploadProgressHandler progress, string fileName = null,
            Dictionary<string, string> otherFields = null, HttpMethod method = null, string accept = "application/json", 
            TimeSpan? timeout = null, AuthenticationHeaderValue auth = null)
        {
            method ??= HttpMethod.Post;
            otherFields ??= new Dictionary<string, string>();
            timeout ??= TimeSpan.FromSeconds(100);

            using var content = new MultipartFormDataContent(RandomEx.GetString(12));

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
                throw new Exception($"Send failed (error {resp.StatusCode}){Environment.NewLine}{str}");

            return str;
        }
    }
}

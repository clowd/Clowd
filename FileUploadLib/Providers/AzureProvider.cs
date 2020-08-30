using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;

namespace FileUploadLib.Providers
{
    public class AzureProvider : IUploadProvider
    {
        private readonly IAzureOptions _options;
        private readonly IMimeProvider _mimeDb;

        const string AZURE_SERVICE_VERSION = "2019-12-12";

        public AzureProvider(IAzureOptions options)
        {
            this._options = options;
            _mimeDb = new MimeDbMimeProvider();
        }

        public async Task<Stream> BeginLargeUpload(string fileName, bool isContentGzipped)
        {
            var key = GetNewBlobKey();
            var blob = await CreateBlobAsync(key);
            return new AzureBlobStream(blob, key, fileName, isContentGzipped);
        }

        public async Task<UploadResult> EndLargeUpload(Stream genStream)
        {
            var stream = genStream as AzureBlobStream;
            if (stream == null)
                throw new ArgumentException(nameof(genStream));

            await stream.CommitBlocks();

            return await SetPropertiesAndGetResult(stream.Blob, stream.FileName, stream.Gzip);
        }

        public async Task<UploadResult> Upload(Stream fileStream, string fileName, ProgressHandler progress)
        {
            var key = GetNewBlobKey();
            var blob = await CreateBlobAsync(key);

            progress(fileStream.Length / 3);
            await blob.UploadFromStreamAsync(fileStream);

            return await SetPropertiesAndGetResult(blob, fileName, false);
        }

        private string GetNewBlobKey() => Guid.NewGuid().ToString().Replace("-", "");

        private async Task<CloudBlockBlob> CreateBlobAsync(string key)
        {
            var container = await Task.Run(() =>
            {
                var account = CloudStorageAccount.Parse(_options.AzureConnectionString);
                var storage = account.CreateCloudBlobClient();
                var serviceProperties = storage.GetServiceProperties();
                serviceProperties.DefaultServiceVersion = AZURE_SERVICE_VERSION;
                storage.SetServiceProperties(serviceProperties);
                return storage.GetContainerReference(_options.AzureContainerName);
            });

            var containerExists = await container.ExistsAsync();
            if (!containerExists)
                throw new InvalidOperationException("Configured Azure storage container does not exist");

            var blob = container.GetBlockBlobReference(key);
            if (await blob.ExistsAsync())
                throw new InvalidOperationException("A blob with this key already exists");

            return blob;
        }

        private async Task<UploadResult> SetPropertiesAndGetResult(CloudBlockBlob blob, string fileName, bool gzip)
        {
            var mimeType = _mimeDb.GetMimeFromExtension(Path.GetExtension(fileName)).ContentType;

            // we want to give the browser a filename hint, and also tell it to render it inline (if it can).
            // if it can't be rendered inline, the browser will just download it anyway.
            blob.Properties.ContentDisposition = $"inline; filename=\"{fileName}\"";
            blob.Properties.ContentType = mimeType;
            if (gzip)
                blob.Properties.ContentEncoding = "gzip";

            await blob.SetPropertiesAsync();

            var url = String.IsNullOrWhiteSpace(_options.CustomUrlPattern)
                ? blob.Uri.ToString()
                : UploadUtil.SubstituteUploadUrl(_options.CustomUrlPattern, mimeType, blob.Name);

            return new UploadResult()
            {
                Provider = typeof(AzureProvider).AssemblyQualifiedName,
                PublicUrl = url,
                FileName = fileName,
                ContentType = mimeType,
                UploadKey = blob.Name,
                UploadTime = DateTimeOffset.UtcNow,
            };
        }
    }

    public interface IAzureOptions : IUploadOptions
    {
        string AzureConnectionString { get; }
        string AzureContainerName { get; }
    }

    public class AzureOptions : UploadOptions, IAzureOptions
    {
        public string AzureConnectionString { get; set; }
        public string AzureContainerName { get; set; }
    }
}

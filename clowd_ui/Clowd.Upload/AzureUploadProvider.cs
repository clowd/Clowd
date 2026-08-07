using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Microsoft.WindowsAzure.Storage.Core.Util;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Clowd.Upload.Accelerate;

namespace Clowd.Upload
{
    public class AzureProgressHandler : IProgress<StorageProgress>
    {
        private readonly Action<StorageProgress> func;

        public AzureProgressHandler(Action<StorageProgress> func)
        {
            this.func = func;
        }

        public void Report(StorageProgress value)
        {
            this.func(value);
        }
    }

    public class AzureUploadProvider : UploadProviderBase, IAccelerateProvider
    {
        public override string Name => "Azure Storage";
        public override string Description => "Uploads any file as a BlockBlob to a public container";
        public override SupportedUploadType SupportedUpload => SupportedUploadType.All;
        public override Stream Icon => new Resource().AzureIcon;
        public override bool SupportsUnseekableUpload => true;

        public string ConnectionString
        {
            get => _connectionString;
            set
            {
                if (value != _connectionString)
                {
                    _connectionString = value;
                    OnPropertyChanged(nameof(ConnectionString));
                }
            }
        }

        public string ContainerName
        {
            get => _containerName;
            set
            {
                if (value != _containerName)
                {
                    _containerName = value;
                    OnPropertyChanged(nameof(ContainerName));
                }
            }
        }

        public string CustomDomain
        {
            get => _customDomain;
            set
            {
                if (value != _customDomain)
                {
                    _customDomain = value;
                    OnPropertyChanged(nameof(CustomDomain));
                }
            }
        }

        [Description("Route uploads through clwd.app so a shareable link is ready immediately, "
                    + "while the file relays to this container in the background.")]
        public bool AccelerateUploads
        {
            get => _accelerateUploads;
            set => Set(ref _accelerateUploads, value);
        }

        [Description("The accelerate server to route uploads through when the toggle above is on.")]
        public string AccelerateServerUrl
        {
            get => _accelerateServerUrl;
            set => Set(ref _accelerateServerUrl, value);
        }

        private readonly IMimeProvider _mimeDb;

        const string AZURE_SERVICE_VERSION = "2019-12-12";
        private string _connectionString;
        private string _containerName;
        private string _customDomain;
        private bool _accelerateUploads = true;
        private string _accelerateServerUrl = "https://clwd.app";

        public AzureUploadProvider() : base()
        {
            _mimeDb = new MimeProvider();
        }

        public override async Task<UploadResult> UploadAsync(Stream fileStream, UploadProgressHandler progress, string uploadName,
            CancellationToken cancelToken)
        {
            if (!fileStream.CanSeek)
                return await UploadUnseekableAsync(fileStream, progress, uploadName, cancelToken);

            var key = GetNewBlobKey();
            var blob = await CreateBlobAsync(key);

            var prg = new AzureProgressHandler((p) => progress(p.BytesTransferred));
            await blob.UploadFromStreamAsync(fileStream,
                null,
                new BlobRequestOptions { },
                new OperationContext { },
                prg,
                cancelToken
            );

            return await SetPropertiesAndGetResult(blob, uploadName, false);
        }

        public override Task<UploadResult> UploadAsync(Stream fileStream, UploadProgressHandler progress, UploadUrlHandler urlAvailable,
            string uploadName, CancellationToken cancelToken)
            => AccelerateUploads
                ? UploadAcceleratedAsync(fileStream, progress, urlAvailable, uploadName, cancelToken)
                : UploadAsync(fileStream, progress, uploadName, cancelToken);

        /// <summary>Streams a payload of unknown length as a sequence of 8 MiB blocks committed
        /// with a final block list, so nothing is ever spooled to disk.</summary>
        private async Task<UploadResult> UploadUnseekableAsync(Stream fileStream, UploadProgressHandler progress, string uploadName,
            CancellationToken cancelToken)
        {
            const int blockBufferSize = 8 * 1024 * 1024;

            var key = GetNewBlobKey();
            var blob = await CreateBlobAsync(key);

            var blockIds = new List<string>();
            var buffer = new byte[blockBufferSize];
            long uploaded = 0;

            while (true)
            {
                var filled = await FillBufferAsync(fileStream, buffer, cancelToken);
                if (filled == 0)
                    break;

                // block ids must be base64 strings of equal pre-encoding length
                var blockId = Convert.ToBase64String(Encoding.ASCII.GetBytes(blockIds.Count.ToString("d6")));
                await blob.PutBlockAsync(blockId, new MemoryStream(buffer, 0, filled, false), null,
                    null, new BlobRequestOptions { }, new OperationContext { }, cancelToken);

                blockIds.Add(blockId);
                uploaded += filled;
                progress?.Invoke(uploaded);

                if (filled < buffer.Length)
                    break;
            }

            await blob.PutBlockListAsync(blockIds, null, new BlobRequestOptions { }, new OperationContext { }, cancelToken);

            return await SetPropertiesAndGetResult(blob, uploadName, false);
        }

        private static async Task<int> FillBufferAsync(Stream stream, byte[] buffer, CancellationToken cancelToken)
        {
            int filled = 0;
            while (filled < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(filled, buffer.Length - filled), cancelToken);
                if (read == 0)
                    break;
                filled += read;
            }

            return filled;
        }

        private async Task<UploadResult> UploadAcceleratedAsync(Stream fileStream, UploadProgressHandler progress,
            UploadUrlHandler urlAvailable, string uploadName, CancellationToken cancelToken)
        {
            var mimeType = _mimeDb.GetMimeFromExtension(Path.GetExtension(uploadName)).ContentType;

            // null for a non-seekable source: the SAS destination is count-free, so unknown-length
            // sessions differ only in the create request and the ?final=1 marking of the last chunk.
            long? contentLength = fileStream.CanSeek ? fileStream.Length : null;

            var key = GetNewBlobKey();
            var account = CloudStorageAccount.Parse(ConnectionString);
            var container = account.CreateCloudBlobClient().GetContainerReference(ContainerName);
            var blob = container.GetBlockBlobReference(key);

            // a blob-level SAS with create+write only — the server never sees the account key.
            var sas = blob.GetSharedAccessSignature(new SharedAccessBlobPolicy
            {
                Permissions = SharedAccessBlobPermissions.Create | SharedAccessBlobPermissions.Write,
                SharedAccessExpiryTime = DateTimeOffset.UtcNow.AddHours(48),
            });

            var finalUrl = String.IsNullOrWhiteSpace(CustomDomain)
                ? blob.Uri.ToString()
                : $"https://{CustomDomain}/{ContainerName}/{blob.Name}";

            var descriptor = new DestinationDescriptor
            {
                Type = "azure-blob",
                BlobSasUrl = blob.Uri + sas,
                FinalUrl = finalUrl,
            };

            return await AcceleratedUploadRunner.RunAsync(
                AccelerateServerUrl, descriptor, fileStream, mimeType, contentLength, uploadName, blob.Name,
                AcceleratedUploadClient.DefaultChunkSize, this, null, progress, urlAvailable, cancelToken);
        }

        public override bool CanDelete(UploadDeleteInfo info)
            => !String.IsNullOrEmpty(info.UploadKey)
               && !String.IsNullOrEmpty(ConnectionString)
               && !String.IsNullOrEmpty(ContainerName);

        public override async Task DeleteAsync(UploadDeleteInfo info, CancellationToken cancelToken)
        {
            // delete the destination blob with the account's own credentials (as always).
            var account = CloudStorageAccount.Parse(ConnectionString);
            var container = account.CreateCloudBlobClient().GetContainerReference(ContainerName);
            var blob = container.GetBlockBlobReference(info.UploadKey);
            await blob.DeleteIfExistsAsync();

            // accelerated records also carry a clwd.app short link — remove it too.
            if (AcceleratedDeleteToken.TryParse(info.DeleteKey, out var id, out var token))
                await AcceleratedUploadClient.DeleteAsync(AccelerateServerUrl, id, token, cancelToken);
        }

        private string GetNewBlobKey() => Guid.NewGuid().ToString().Replace("-", "");

        private async Task<CloudBlockBlob> CreateBlobAsync(string key)
        {
            //var container = await Task.Run(async () =>
            //{
            var account = CloudStorageAccount.Parse(ConnectionString);
            var storage = account.CreateCloudBlobClient();
            var serviceProperties = await storage.GetServicePropertiesAsync();
            serviceProperties.DefaultServiceVersion = AZURE_SERVICE_VERSION;
            await storage.SetServicePropertiesAsync(serviceProperties);
            var container = storage.GetContainerReference(ContainerName);
            //return storage.GetContainerReference(ContainerName);
            //});

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

            var url = blob.Uri.ToString();

            if (!String.IsNullOrWhiteSpace(CustomDomain))
                url = $"https://{CustomDomain}/{ContainerName}/{blob.Name}";

            //var url = String.IsNullOrWhiteSpace(_options.CustomUrlPattern)
            //    ? blob.Uri.ToString()
            //    : UploadUtil.SubstituteUploadUrl(_options.CustomUrlPattern, mimeType, blob.Name);

            return new UploadResult()
            {
                Provider = this,
                PublicUrl = url,
                FileName = fileName,
                ContentType = mimeType,
                UploadKey = blob.Name,
                UploadTime = DateTimeOffset.UtcNow,
            };
        }
    }
}

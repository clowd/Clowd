﻿using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Microsoft.WindowsAzure.Storage.Core.Util;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

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

    public class AzureUploadProvider : UploadProviderBase
    {
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

        public override Stream Icon => new Resource().AzureIcon;

        const string AZURE_SERVICE_VERSION = "2019-12-12";
        private readonly IMimeProvider _mimeDb;
        private string _connectionString;
        private string _containerName;
        private string _customDomain;

        public AzureUploadProvider() : base("Azure Storage", "Uploads any file as a block blob for viewing online", SupportedUploadType.All)
        {
            _mimeDb = new MimeDbMimeProvider();
        }

        public override async Task<UploadResult> UploadAsync(Stream fileStream, UploadProgressHandler progress, string uploadName, CancellationToken cancelToken)
        {
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

        private string GetNewBlobKey() => Guid.NewGuid().ToString().Replace("-", "");

        private async Task<CloudBlockBlob> CreateBlobAsync(string key)
        {
            var container = await Task.Run(() =>
            {
                var account = CloudStorageAccount.Parse(ConnectionString);
                var storage = account.CreateCloudBlobClient();
                var serviceProperties = storage.GetServiceProperties();
                serviceProperties.DefaultServiceVersion = AZURE_SERVICE_VERSION;
                storage.SetServiceProperties(serviceProperties);
                return storage.GetContainerReference(ContainerName);
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

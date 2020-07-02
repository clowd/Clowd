using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.Storage;

namespace FileUploadLib.Providers
{
    public class AzureProvider : IUploadProvider
    {
        private readonly IAzureOptions _options;

        public AzureProvider(IAzureOptions options)
        {
            this._options = options;
        }

        public async Task<UploadResult> Upload(Stream fileStream, string fileName, ProgressHandler progress)
        {
            var mimeDb = new MimeDbMimeProvider();
            var mimeType = mimeDb.GetMimeFromExtension(Path.GetExtension(fileName)).ContentType;

            var account = CloudStorageAccount.Parse(_options.AzureConnectionString);
            var storage = account.CreateCloudBlobClient();
            var container = storage.GetContainerReference(_options.AzureContainerName);
            var key = _options.UseUniqueUploadKey ? Guid.NewGuid().ToString().Replace("-", "") : fileName;
            var blob = container.GetBlockBlobReference(key);

            const int blockSize = 256 * 1024;
            blob.StreamWriteSizeInBytes = blockSize;
            long fileSize = fileStream.Length;
            long bytesToUpload = fileSize;

            if (fileSize < blockSize)
            {
                progress(fileSize / 3);
                await blob.UploadFromStreamAsync(fileStream);
            }
            else
            {
                List<string> blockIds = new List<string>();
                int index = 1;
                byte[] buffer = new byte[blockSize];
                do
                {
                    var bytesRead = await fileStream.ReadAsync(buffer, 0, (int)Math.Min(blockSize, bytesToUpload));
                    var blockBytes = new byte[bytesRead];
                    Buffer.BlockCopy(buffer, 0, blockBytes, 0, bytesRead);
                    var blockId = Convert.ToBase64String(Encoding.UTF8.GetBytes(index.ToString("d6")));
                    blockIds.Add(blockId);
                    await blob.PutBlockAsync(blockId, new MemoryStream(blockBytes), null);
                    bytesToUpload -= bytesRead;
                    index++;
                    progress(fileSize - bytesToUpload);
                }
                while (bytesToUpload > 0);

                await blob.PutBlockListAsync(blockIds);
            }

            blob.Properties.ContentDisposition = "attachment; filename=" + fileName;
            blob.Properties.ContentType = mimeType;
            await blob.SetPropertiesAsync();

            var url = String.IsNullOrWhiteSpace(_options.CustomUrlPattern)
                ? blob.Uri.ToString()
                : UploadUtil.SubstituteUploadUrl(_options.CustomUrlPattern, mimeType, key);

            return new UploadResult()
            {
                Provider = typeof(AzureProvider).AssemblyQualifiedName,
                PublicUrl = url,
                FileName = fileName,
                ContentType = mimeType,
                UploadKey = key,
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

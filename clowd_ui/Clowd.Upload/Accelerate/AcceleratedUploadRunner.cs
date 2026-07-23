using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Clowd.Upload.Accelerate
{
    /// <summary>
    /// Drives one accelerated upload end to end — create session, surface the download URL early,
    /// PUT the chunks, then commit — aborting the server session on any failure or cancellation.
    /// Shared by the Azure and S3 providers; the only per-provider part is the destination
    /// descriptor and object key, which are computed by the caller.
    /// </summary>
    internal static class AcceleratedUploadRunner
    {
        public static async Task<UploadResult> RunAsync(
            string serverUrl,
            DestinationDescriptor descriptor,
            Stream fileStream,
            string contentType,
            long contentLength,
            string uploadName,
            string uploadKey,
            long chunkSize,
            IUploadProvider provider,
            UploadProgressHandler progress,
            UploadUrlHandler urlAvailable,
            CancellationToken cancelToken)
        {
            if (contentLength > AcceleratedUploadClient.MaxUploadBytes)
                throw new InvalidOperationException(
                    $"The file is larger than the {AcceleratedUploadClient.MaxUploadBytes / (1024 * 1024 * 1024)} GiB accelerated-upload limit.");

            chunkSize = AcceleratedUploadClient.ClampChunkSize(chunkSize);
            var chunkCount = AcceleratedUploadClient.ComputeChunkCount(contentLength, chunkSize);

            var client = new AcceleratedUploadClient(serverUrl);

            var create = await client.CreateAsync(new CreateUploadRequest
            {
                FileName = uploadName,
                ContentType = contentType,
                ContentLength = contentLength,
                ChunkSize = chunkSize,
                Destination = descriptor,
            }, cancelToken);

            // the whole point of the feature: the link is shareable now, before any bytes transfer.
            urlAvailable?.Invoke(create.DownloadUrl);

            try
            {
                await client.UploadChunksAsync(create.Id, create.UploadToken, fileStream, chunkSize, chunkCount, progress, cancelToken);
            }
            catch
            {
                // a chunk-phase failure means the bytes never fully staged — nothing is committable,
                // so abort promptly to release staging (the shared early link is rolled back in the UI).
                await client.AbortAsync(create.Id, create.UploadToken);
                throw;
            }

            // Every chunk is staged; /complete is idempotent and retried internally (REFACTOR §4.3).
            // Deliberately NOT wrapped in the abort handler: after full staging the server may have
            // already committed (a lost response looks like a failure), and the download URL is already
            // shared — aborting here would sever a possibly-working link. Let the failure propagate so
            // the UI rolls back the early URL, and leave the server session to its own idle cleanup.
            await client.CompleteAsync(create.Id, create.UploadToken, cancelToken);

            return new UploadResult
            {
                Provider = provider,
                // downloadUrl is the permanent short link (301s to the destination once committed),
                // so it is both the early URL and the final PublicUrl.
                PublicUrl = create.DownloadUrl,
                FileName = uploadName,
                ContentType = contentType,
                UploadKey = uploadKey,
                DeleteKey = AcceleratedDeleteToken.Encode(create.Id, create.DeleteToken),
                UploadTime = DateTimeOffset.UtcNow,
            };
        }
    }
}

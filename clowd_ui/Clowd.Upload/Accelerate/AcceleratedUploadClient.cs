using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Handlers;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Clowd.Upload.Accelerate
{
    /// <summary>
    /// Speaks the clwd.app "accelerated upload" wire protocol (REFACTOR.md §4): create a session,
    /// PUT the file in sequential chunks through the worker, then commit. The download URL is
    /// shareable the moment <see cref="CreateAsync"/> returns — long before the bytes finish
    /// relaying to their final destination — which is the entire point of the feature.
    ///
    /// JSON is camelCase on the wire; auth on every mutation is <c>Authorization: Bearer
    /// {uploadToken}</c>.
    /// </summary>
    internal sealed class AcceleratedUploadClient
    {
        // 16 MiB default; the server clamps to [5 MiB, 32 MiB]. Kept inside that range so the clamp
        // is a no-op and the client's chunk plan (and any S3 partUrls minted for it) always agrees
        // with what the server relays.
        public const long DefaultChunkSize = 16L * 1024 * 1024;
        public const long MinChunkSize = 5L * 1024 * 1024;
        public const long MaxChunkSize = 32L * 1024 * 1024;
        public const long MaxUploadBytes = 10L * 1024 * 1024 * 1024;

        // chunk PUTs can take minutes each; must not use HttpClient's 100s default.
        private static readonly TimeSpan ChunkTimeout = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan ControlTimeout = TimeSpan.FromMinutes(5);

        // /complete is idempotent and safe to retry (REFACTOR §4.3) — the server returns the cached
        // result if the commit already happened. Retry transient failures rather than giving up:
        // by this point every chunk is staged and the download URL is already shared, so surrendering
        // (and aborting) would sever a link the server may have already committed.
        private const int CompleteAttempts = 4;
        private static readonly TimeSpan CompleteRetryDelay = TimeSpan.FromSeconds(2);

        private readonly string _baseUrl;

        public AcceleratedUploadClient(string serverUrl)
        {
            if (string.IsNullOrWhiteSpace(serverUrl))
                throw new InvalidOperationException("An accelerate server URL must be configured.");
            _baseUrl = serverUrl.Trim().TrimEnd('/');
        }

        public static long ClampChunkSize(long requested)
        {
            if (requested <= 0)
                requested = DefaultChunkSize;
            return Math.Min(MaxChunkSize, Math.Max(MinChunkSize, requested));
        }

        public static int ComputeChunkCount(long contentLength, long chunkSize)
        {
            if (chunkSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(chunkSize));
            if (contentLength <= 0)
                return 1; // a zero-byte upload still commits a single (empty) chunk/part
            return (int)((contentLength + chunkSize - 1) / chunkSize);
        }

        public async Task<CreateUploadResponse> CreateAsync(CreateUploadRequest request, CancellationToken ct)
        {
            var json = JsonSerializer.Serialize(request, AccelerateJsonContext.Default.CreateUploadRequest);

            using var http = NewClient(ControlTimeout);
            using var msg = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/v1/uploads")
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            };

            using var resp = await http.SendAsync(msg, ct);
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                throw new Exception($"Failed to create upload (error {resp.StatusCode}).{Environment.NewLine}{body}");

            return JsonSerializer.Deserialize(body, AccelerateJsonContext.Default.CreateUploadResponse);
        }

        /// <summary>
        /// Streams <paramref name="source"/> to the server in sequential chunks of
        /// <paramref name="chunkSize"/> bytes (last chunk short), reporting absolute bytes sent
        /// through <paramref name="progress"/>. Each chunk is buffered so a single retry can resend
        /// the exact same bytes; the PUT is idempotent server-side.
        /// </summary>
        public async Task UploadChunksAsync(
            string id, string uploadToken, Stream source, long chunkSize, int chunkCount,
            UploadProgressHandler progress, CancellationToken ct)
        {
            long baseOffset = 0;

            using var http = NewClient(ChunkTimeout, args => progress?.Invoke(baseOffset + args));

            var buffer = new byte[chunkSize];
            for (int n = 0; n < chunkCount; n++)
            {
                ct.ThrowIfCancellationRequested();

                int read = await ReadFullyAsync(source, buffer, chunkSize, ct);
                if (read == 0 && n < chunkCount - 1)
                    throw new IOException("The upload stream ended before all chunks were read.");

                baseOffset = n * chunkSize;
                await PutChunkWithRetryAsync(http, id, uploadToken, n, buffer, read, false, null, ct);

                // ensure progress lands on the exact boundary even if the handler under-reports.
                progress?.Invoke(baseOffset + read);
            }
        }

        /// <summary>
        /// Streams a source of unknown length to the server in sequential chunks, marking the last
        /// one with <c>?final=1</c> so the server learns the total only when EOF is reached. For
        /// s3-multipart destinations the create request carried no part URLs, so each chunk's
        /// presigned UploadPart URL is minted lazily via <paramref name="partUrl"/> and travels in
        /// the <c>x-clowd-part-url</c> header (null for count-free destinations such as azure-blob).
        /// Returns the total number of bytes sent.
        /// </summary>
        public async Task<long> UploadUnknownLengthChunksAsync(
            string id, string uploadToken, Stream source, long chunkSize, Func<int, string> partUrl,
            UploadProgressHandler progress, CancellationToken ct)
        {
            long baseOffset = 0;

            using var http = NewClient(ChunkTimeout, args => progress?.Invoke(baseOffset + args));

            var chunker = new UnknownLengthChunker(source, chunkSize);
            for (int n = 0; ; n++)
            {
                ct.ThrowIfCancellationRequested();

                var (length, isFinal) = await chunker.ReadNextAsync(ct);

                // the server rejects zero-byte chunks, so an empty source has no valid final
                // chunk to mark; the caller never produces one (a streamed zip is never empty).
                if (length == 0)
                    throw new IOException("The upload stream ended before producing any data.");

                baseOffset = n * chunkSize;
                await PutChunkWithRetryAsync(http, id, uploadToken, n, chunker.Buffer, length, isFinal, partUrl?.Invoke(n), ct);

                // ensure progress lands on the exact boundary even if the handler under-reports.
                progress?.Invoke(baseOffset + length);

                if (isFinal)
                    return baseOffset + length;
            }
        }

        private async Task PutChunkWithRetryAsync(
            HttpClient http, string id, string uploadToken, int n, byte[] buffer, int length, bool final, string partUrl,
            CancellationToken ct)
        {
            var url = $"{_baseUrl}/api/v1/uploads/{id}/chunks/{n}";
            if (final)
                url += "?final=1";

            Exception last = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var content = new ByteArrayContent(buffer, 0, length);
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

                    using var msg = new HttpRequestMessage(HttpMethod.Put, url)
                    {
                        Content = content,
                    };
                    msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", uploadToken);
                    if (partUrl != null)
                        msg.Headers.TryAddWithoutValidation("x-clowd-part-url", partUrl);

                    using var resp = await http.SendAsync(msg, ct);
                    if (resp.IsSuccessStatusCode)
                        return;

                    var body = await resp.Content.ReadAsStringAsync();
                    last = new Exception($"Chunk {n} upload failed (error {resp.StatusCode}).{Environment.NewLine}{body}");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    last = ex;
                }
            }

            throw last ?? new Exception($"Chunk {n} upload failed.");
        }

        public async Task<CompleteUploadResponse> CompleteAsync(string id, string uploadToken, CancellationToken ct)
        {
            using var http = NewClient(ControlTimeout);

            Exception last = null;
            for (int attempt = 0; attempt < CompleteAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var msg = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/v1/uploads/{id}/complete");
                    msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", uploadToken);

                    using var resp = await http.SendAsync(msg, ct);
                    var body = await resp.Content.ReadAsStringAsync();
                    if (resp.IsSuccessStatusCode)
                        return JsonSerializer.Deserialize(body, AccelerateJsonContext.Default.CompleteUploadResponse);

                    last = new Exception($"Failed to complete upload (error {resp.StatusCode}).{Environment.NewLine}{body}");

                    // a 4xx (other than 429) is a permanent rejection — retrying the same idempotent
                    // request won't change the answer, so fail fast instead of burning the retry budget.
                    int code = (int)resp.StatusCode;
                    if (code >= 400 && code < 500 && resp.StatusCode != System.Net.HttpStatusCode.TooManyRequests)
                        break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    last = ex;
                }

                if (attempt < CompleteAttempts - 1)
                    await Task.Delay(CompleteRetryDelay, ct);
            }

            throw last ?? new Exception("Failed to complete upload.");
        }

        /// <summary>Best-effort abort on cancellation/failure; never throws.</summary>
        public async Task AbortAsync(string id, string uploadToken)
        {
            try
            {
                using var http = NewClient(TimeSpan.FromSeconds(30));
                using var msg = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/v1/uploads/{id}/abort");
                msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", uploadToken);
                using var resp = await http.SendAsync(msg, CancellationToken.None);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("failed to abort accelerated upload: " + ex);
            }
        }

        /// <summary>Removes the clwd.app short link for a previously-completed upload
        /// (<c>DELETE /api/v1/uploads/{id}</c> with the delete token). The destination object is
        /// deleted separately by the provider with its own credentials.</summary>
        public static async Task DeleteAsync(string serverUrl, string id, string deleteToken, CancellationToken ct)
        {
            var baseUrl = serverUrl.Trim().TrimEnd('/');
            using var http = NewClient(TimeSpan.FromSeconds(30));
            using var msg = new HttpRequestMessage(HttpMethod.Delete, $"{baseUrl}/api/v1/uploads/{id}");
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deleteToken);

            using var resp = await http.SendAsync(msg, ct);
            if (!resp.IsSuccessStatusCode && resp.StatusCode != System.Net.HttpStatusCode.NotFound)
            {
                var body = await resp.Content.ReadAsStringAsync();
                throw new Exception($"Failed to delete upload link (error {resp.StatusCode}).{Environment.NewLine}{body}");
            }
        }

        private static async Task<int> ReadFullyAsync(Stream source, byte[] buffer, long chunkSize, CancellationToken ct)
        {
            int total = 0;
            while (total < chunkSize)
            {
                int read = await source.ReadAsync(buffer.AsMemory(total, (int)(chunkSize - total)), ct);
                if (read == 0)
                    break;
                total += read;
            }
            return total;
        }

        private static HttpClient NewClient(TimeSpan timeout, Action<long> onProgress = null)
        {
            var handler = new ProgressMessageHandler(new HttpClientHandler { AllowAutoRedirect = true });
            if (onProgress != null)
                handler.HttpSendProgress += (_, args) => onProgress(args.BytesTransferred);

            var client = new HttpClient(handler) { Timeout = timeout };
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            return client;
        }
    }

    internal sealed class CreateUploadRequest
    {
        public string FileName { get; set; }
        public string ContentType { get; set; }

        // null for an unknown-length (non-seekable) source; WhenWritingNull omits it on the wire,
        // which the server treats identically to an explicit null. Always sent when known so
        // seekable uploads stay byte-identical to the fixed-plan protocol.
        public long? ContentLength { get; set; }
        public long ChunkSize { get; set; }
        public DestinationDescriptor Destination { get; set; }
    }

    /// <summary>A capability-URL-only destination descriptor (REFACTOR.md §6). Only the fields
    /// relevant to <see cref="Type"/> are populated; the rest are omitted on the wire.</summary>
    internal sealed class DestinationDescriptor
    {
        public string Type { get; set; }

        // azure-blob — the worker's Destination::AzureBlob DTO requires the wire field "sasUrl"
        // (model.rs, #[serde(rename_all="camelCase")], no alias/default); the camelCase policy would
        // otherwise emit "blobSasUrl" and every azure create would 400 with "missing field sasUrl".
        [JsonPropertyName("sasUrl")]
        public string BlobSasUrl { get; set; }

        // s3-multipart
        public string[] PartUrls { get; set; }
        public string CompleteUrl { get; set; }
        public string AbortUrl { get; set; }

        // both
        public string FinalUrl { get; set; }
    }

    internal sealed class CreateUploadResponse
    {
        public string Id { get; set; }
        public string DownloadUrl { get; set; }
        public string UploadToken { get; set; }
        public string DeleteToken { get; set; }
        public long ChunkSize { get; set; }
        public int ChunkCount { get; set; }
        public string FinalUrl { get; set; }
    }

    internal sealed class CompleteUploadResponse
    {
        public string FinalUrl { get; set; }
        public long Length { get; set; }
    }

    // A dedicated camelCase context for the accelerate DTOs — deliberately NOT a naming policy on
    // the shared UploadJsonContext, which would change the existing B2 request payloads.
    [JsonSourceGenerationOptions(
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(CreateUploadRequest))]
    [JsonSerializable(typeof(CreateUploadResponse))]
    [JsonSerializable(typeof(CompleteUploadResponse))]
    [JsonSerializable(typeof(DestinationDescriptor))]
    internal partial class AccelerateJsonContext : JsonSerializerContext { }
}

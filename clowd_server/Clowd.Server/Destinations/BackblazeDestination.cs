using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Clowd.Server.Api;

namespace Clowd.Server.Destinations;

/// <summary>
/// BackBlaze B2. The b2_upload_file api demands Content-Length and a sha1 up front, which
/// would normally force buffering the whole file. We stream anyway by requiring the client
/// to declare contentLength and using B2's "hex_digits_at_end" mode: the sha1 is computed
/// while bytes flow through and appended as a 40-char trailer.
/// </summary>
public sealed class BackblazeDestinationProvider(IHttpClientFactory httpFactory) : IDestinationProvider
{
    private const string AuthorizeUrl = "https://api.backblazeb2.com/b2api/v2/b2_authorize_account";
    private const int Sha1TrailerLength = 40;

    public string Name => "backblaze";

    public async Task<IDestinationUpload> BeginAsync(StartUploadRequest request, CancellationToken ct)
    {
        var keyId = Creds.Require(request, "keyId", Name);
        var applicationKey = Creds.Require(request, "applicationKey", Name);
        var bucketName = Creds.Require(request, "bucketName", Name);
        if (request.ContentLength is not { } contentLength)
            throw new DestinationConfigException("backblaze requires contentLength (B2 uploads cannot be chunked)");

        using var http = httpFactory.CreateClient(HttpStreamingUpload.HttpClientName);

        // 1. authorize
        using var authReq = new HttpRequestMessage(HttpMethod.Get, AuthorizeUrl);
        authReq.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{keyId}:{applicationKey}")));
        var auth = await SendJson<AuthorizeResponse>(http, authReq, "authorization", ct);

        // 2. resolve the bucket id (a bucket-restricted key already carries it)
        string bucketId;
        if (auth.allowed?.bucketId is { } allowedId && (auth.allowed.bucketName is null || auth.allowed.bucketName == bucketName))
        {
            bucketId = allowedId;
        }
        else
        {
            using var listReq = JsonPost($"{auth.apiUrl}/b2api/v2/b2_list_buckets",
                                         new { accountId = auth.accountId, bucketName }, auth.authorizationToken!);
            var buckets = await SendJson<ListBucketsResponse>(http, listReq, "bucket lookup", ct);
            bucketId = buckets.buckets is [{ bucketId: { } id }, ..]
                ? id
                : throw new DestinationConfigException($"backblaze bucket '{bucketName}' was not found");
        }

        // 3. get a single-use upload url
        using var uploadUrlReq = JsonPost($"{auth.apiUrl}/b2api/v2/b2_get_upload_url", new { bucketId }, auth.authorizationToken!);
        var uploadUrl = await SendJson<UploadUrlResponse>(http, uploadUrlReq, "upload url", ct);

        // 4. start the streaming upload
        var fileName = Creds.SanitizeFileName(request.FileName);
        var encodedName = Uri.EscapeDataString(fileName);
        var finalUrl = $"{auth.downloadUrl}/file/{bucketName}/{encodedName}";

        var upload = new HttpStreamingUpload(
            httpFactory.CreateClient(HttpStreamingUpload.HttpClientName),
            fileStream =>
            {
                var content = new StreamContent(new Sha1TrailerStream(fileStream));
                content.Headers.ContentType = new MediaTypeHeaderValue(Creds.ContentTypeOrDefault(request));
                content.Headers.ContentLength = contentLength + Sha1TrailerLength;

                var message = new HttpRequestMessage(HttpMethod.Post, uploadUrl.uploadUrl) { Content = content };
                message.Headers.TryAddWithoutValidation("Authorization", uploadUrl.authorizationToken);
                message.Headers.Add("X-Bz-File-Name", encodedName);
                message.Headers.Add("X-Bz-Content-Sha1", "hex_digits_at_end");
                return message;
            },
            async (response, token) =>
            {
                var json = await response.Content.ReadAsStringAsync(token);
                var parsed = JsonSerializer.Deserialize<UploadFileResponse>(json);
                if (string.IsNullOrEmpty(parsed?.fileId))
                    throw new IOException($"backblaze upload failed: {json}");

                return new DestinationResult
                {
                    FinalUrl = finalUrl,
                    Delete = new UploadDeleteInfo
                    {
                        Provider = Name,
                        // b2_delete_file_version wants both the name and the file id
                        UploadKey = parsed.fileName ?? fileName,
                        DeleteKey = parsed.fileId,
                    },
                };
            },
            finalUrl: finalUrl);

        return upload;
    }

    private static HttpRequestMessage JsonPost(string url, object body, string authToken)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        message.Headers.TryAddWithoutValidation("Authorization", authToken);
        return message;
    }

    private static async Task<T> SendJson<T>(HttpClient http, HttpRequestMessage request, string what, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new DestinationConfigException($"backblaze {what} failed: {ex.Message}");
        }

        using (response)
        {
            var json = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new DestinationConfigException($"backblaze {what} failed ({(int)response.StatusCode}): {json}");
            return JsonSerializer.Deserialize<T>(json)
                   ?? throw new DestinationConfigException($"backblaze {what} returned an empty response");
        }
    }

#pragma warning disable IDE1006 // names match the b2 wire format
    private sealed class AuthorizeResponse
    {
        public string? accountId { get; set; }
        public string? authorizationToken { get; set; }
        public string? apiUrl { get; set; }
        public string? downloadUrl { get; set; }
        public AllowedInfo? allowed { get; set; }
    }

    private sealed class AllowedInfo
    {
        public string? bucketId { get; set; }
        public string? bucketName { get; set; }
    }

    private sealed class ListBucketsResponse
    {
        public List<BucketInfo>? buckets { get; set; }
    }

    private sealed class BucketInfo
    {
        public string? bucketId { get; set; }
    }

    private sealed class UploadUrlResponse
    {
        public string? uploadUrl { get; set; }
        public string? authorizationToken { get; set; }
    }

    private sealed class UploadFileResponse
    {
        public string? fileId { get; set; }
        public string? fileName { get; set; }
    }
#pragma warning restore IDE1006
}

/// <summary>
/// Passes the inner stream through while hashing it, then yields the lowercase sha1 hex as
/// a 40-byte trailer — B2's "hex_digits_at_end" wire format.
/// </summary>
internal sealed class Sha1TrailerStream(Stream inner) : Stream
{
    private readonly IncrementalHash _sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
    private byte[]? _trailer;
    private int _trailerPos;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_trailer is null)
        {
            int read = await inner.ReadAsync(buffer, ct);
            if (read > 0)
            {
                _sha1.AppendData(buffer.Span[..read]);
                return read;
            }

            _trailer = Encoding.ASCII.GetBytes(Convert.ToHexString(_sha1.GetHashAndReset()).ToLowerInvariant());
        }

        int take = Math.Min(_trailer.Length - _trailerPos, buffer.Length);
        _trailer.AsSpan(_trailerPos, take).CopyTo(buffer.Span);
        _trailerPos += take;
        return take;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _sha1.Dispose();
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

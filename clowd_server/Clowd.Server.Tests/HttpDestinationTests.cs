using System.Net;
using System.Security.Cryptography;
using System.Text;
using Clowd.Server.Api;
using Clowd.Server.Destinations;
using Xunit;

namespace Clowd.Server.Tests;

/// <summary>
/// Exercises the http destination providers against a stub handler — verifies the wire
/// format each service expects and that file bytes stream through the pipe correctly.
/// </summary>
public sealed class HttpDestinationTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, byte[], HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, byte[] Body, long? DeclaredContentLength)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            // Capture the declared length *before* draining: this is what the real
            // transport reads to decide Content-Length vs chunked. Reading the body first
            // would buffer the content and retroactively populate ContentLength.
            var declaredLength = request.Content?.Headers.ContentLength;

            // fully drain the (streaming) content like a real server would
            var body = request.Content is null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(ct);
            lock (Requests)
                Requests.Add((request, body, declaredLength));
            return responder(request, body);
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static StartUploadRequest Request(string provider, Dictionary<string, string> creds, long? length = null) => new()
    {
        Provider = provider,
        FileName = "shot.png",
        ContentType = "image/png",
        ContentLength = length,
        Credentials = creds,
    };

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static async Task<DestinationResult> RunUpload(IDestinationUpload upload, byte[] payload)
    {
        await upload.WriteStream.WriteAsync(payload);
        return await upload.CommitAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Imgur_StreamsFormDataAndParsesResult()
    {
        var stub = new StubHandler((_, _) => Json("""{"success":true,"status":200,"data":{"id":"abc","deletehash":"dh123","link":"https://i.imgur.com/abc.png"}}"""));
        var provider = new ImgurDestinationProvider(new StubHttpClientFactory(stub));

        var payload = "fake png bytes"u8.ToArray();
        await using var upload = await provider.BeginAsync(Request("imgur", new() { ["clientId"] = "cid" }), default);
        var result = await RunUpload(upload, payload);

        Assert.Equal("https://i.imgur.com/abc.png", result.FinalUrl);
        Assert.Equal("dh123", result.Delete?.DeleteKey);
        Assert.Equal("https://api.imgur.com/3/image/dh123", result.Delete?.DeleteUrl);

        var (request, body, _) = stub.Requests.Single();
        Assert.Equal("https://api.imgur.com/3/upload", request.RequestUri!.ToString());
        Assert.Equal("Client-ID", request.Headers.Authorization!.Scheme);
        var text = Encoding.UTF8.GetString(body);
        Assert.Contains("name=image", text);
        Assert.Contains("filename=shot.png", text);
        Assert.Contains("fake png bytes", text);
    }

    [Fact]
    public async Task Catbox_ParsesPlainTextUrl()
    {
        var stub = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("https://files.catbox.moe/xyz123.png"),
        });
        var provider = new CatboxDestinationProvider(new StubHttpClientFactory(stub));

        await using var upload = await provider.BeginAsync(Request("catbox", new() { ["userHash"] = "uh" }), default);
        var result = await RunUpload(upload, [1, 2, 3]);

        Assert.Equal("https://files.catbox.moe/xyz123.png", result.FinalUrl);
        Assert.Equal("xyz123.png", result.Delete?.DeleteKey);
        Assert.Equal("https://catbox.moe/user/api.php", stub.Requests.Single().Request.RequestUri!.ToString());
    }

    [Fact]
    public async Task Catbox_ExpiryUsesLitterbox()
    {
        var stub = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("https://litter.catbox.moe/abc.png"),
        });
        var provider = new CatboxDestinationProvider(new StubHttpClientFactory(stub));

        await using var upload = await provider.BeginAsync(Request("catbox", new() { ["expiry"] = "24h" }), default);
        await RunUpload(upload, [1]);

        var (request, body, _) = stub.Requests.Single();
        Assert.Equal("https://litterbox.catbox.moe/resources/internals/api.php", request.RequestUri!.ToString());
        Assert.Contains("24h", Encoding.UTF8.GetString(body));
    }

    [Fact]
    public async Task Hastebin_PostsRawBodyAndAppendsExtension()
    {
        var stub = new StubHandler((_, _) => Json("""{"key":"k9","secret":"s1"}"""));
        var provider = new HastebinDestinationProvider(new StubHttpClientFactory(stub));

        var request = Request("hastebin", new());
        request.FileName = "notes.txt";
        await using var upload = await provider.BeginAsync(request, default);
        var result = await RunUpload(upload, "some text"u8.ToArray());

        Assert.Equal("https://pastie.io/k9.txt", result.FinalUrl);
        Assert.Equal("s1", result.Delete?.DeleteKey);
        var (req, body, _) = stub.Requests.Single();
        Assert.Equal("https://pastie.io/documents", req.RequestUri!.ToString());
        Assert.Equal("some text", Encoding.UTF8.GetString(body)); // raw, not multipart
    }

    [Fact]
    public async Task VgyMe_ParsesResult()
    {
        var stub = new StubHandler((_, _) => Json("""{"error":false,"filename":"f1","image":"https://i.vgy.me/f1.png","delete":"https://vgy.me/delete/zz"}"""));
        var provider = new VgyMeDestinationProvider(new StubHttpClientFactory(stub));

        await using var upload = await provider.BeginAsync(Request("vgyme", new() { ["userKey"] = "uk" }), default);
        var result = await RunUpload(upload, [1]);

        Assert.Equal("https://i.vgy.me/f1.png", result.FinalUrl);
        Assert.Equal("https://vgy.me/delete/zz", result.Delete?.DeleteUrl);
    }

    [Fact]
    public async Task Picsur_BuildsViewAndDirectUrls()
    {
        var stub = new StubHandler((_, _) => Json("""{"success":true,"data":{"id":"img1","delete_key":"dk"}}"""));
        var provider = new PicsurDestinationProvider(new StubHttpClientFactory(stub));

        var creds = new Dictionary<string, string> { ["baseUrl"] = "https://pics.example.com/", ["apiKey"] = "ak", ["directLink"] = "true" };
        await using var upload = await provider.BeginAsync(Request("picsur", creds), default);
        var result = await RunUpload(upload, [1]);

        Assert.Equal("https://pics.example.com/i/img1.png", result.FinalUrl);
        Assert.Equal("dk", result.Delete?.DeleteKey);
        Assert.Equal("Api-Key", stub.Requests.Single().Request.Headers.Authorization!.Scheme);
    }

    [Fact]
    public async Task Backblaze_StreamsWithSha1Trailer()
    {
        var payload = "hello backblaze streaming"u8.ToArray();
        var expectedSha1 = Convert.ToHexString(SHA1.HashData(payload)).ToLowerInvariant();

        var stub = new StubHandler((request, _) => request.RequestUri!.ToString() switch
        {
            var u when u.Contains("b2_authorize_account") => Json("""
                {"accountId":"acct","authorizationToken":"authtok","apiUrl":"https://api900.backblazeb2.com","downloadUrl":"https://f900.backblazeb2.com"}
                """),
            var u when u.Contains("b2_list_buckets") => Json("""{"buckets":[{"bucketId":"bkt1"}]}"""),
            var u when u.Contains("b2_get_upload_url") => Json("""{"uploadUrl":"https://pod.backblazeb2.com/upload","authorizationToken":"uploadtok"}"""),
            _ => Json("""{"fileId":"fid1","fileName":"shot.png"}"""),
        });
        var provider = new BackblazeDestinationProvider(new StubHttpClientFactory(stub));

        var creds = new Dictionary<string, string> { ["keyId"] = "k", ["applicationKey"] = "a", ["bucketName"] = "mybucket" };
        await using var upload = await provider.BeginAsync(Request("backblaze", creds, length: payload.Length), default);

        Assert.Equal("https://f900.backblazeb2.com/file/mybucket/shot.png", upload.FinalUrl);

        var result = await RunUpload(upload, payload);
        Assert.Equal("fid1", result.Delete?.DeleteKey);

        var (uploadReq, uploadBody, _) = stub.Requests.Single(r => r.Request.RequestUri!.ToString().EndsWith("/upload"));
        Assert.Equal("hex_digits_at_end", uploadReq.Headers.GetValues("X-Bz-Content-Sha1").Single());
        Assert.Equal(payload.Length + 40, uploadReq.Content!.Headers.ContentLength);
        Assert.Equal(payload.Length + 40, uploadBody.Length);
        Assert.Equal(payload, uploadBody[..payload.Length]);
        Assert.Equal(expectedSha1, Encoding.ASCII.GetString(uploadBody[payload.Length..]));
    }

    [Fact]
    public async Task DeclaredContentLength_AvoidsChunkedEncoding_Multipart()
    {
        var stub = new StubHandler((_, _) => Json("""{"success":true,"status":200,"data":{"id":"a","deletehash":"d","link":"https://i.imgur.com/a.png"}}"""));
        var provider = new ImgurDestinationProvider(new StubHttpClientFactory(stub));

        var payload = new byte[1234];
        await using var upload = await provider.BeginAsync(Request("imgur", new() { ["clientId"] = "c" }, length: payload.Length), default);
        await RunUpload(upload, payload);

        // a computable Content-Length on the outgoing content is exactly what makes
        // HttpClient send Content-Length instead of Transfer-Encoding: chunked. The
        // multipart total is boundaries + the file part, so it exceeds the file size.
        var contentLength = stub.Requests.Single().DeclaredContentLength;
        Assert.NotNull(contentLength);
        Assert.True(contentLength > payload.Length);
    }

    [Fact]
    public async Task NoContentLength_FallsBackToChunked_Multipart()
    {
        var stub = new StubHandler((_, _) => Json("""{"success":true,"status":200,"data":{"id":"a","deletehash":"d","link":"https://i.imgur.com/a.png"}}"""));
        var provider = new ImgurDestinationProvider(new StubHttpClientFactory(stub));

        // no contentLength declared: the pipe stream is non-seekable, so the length is
        // not computable and the real transport would use chunked
        await using var upload = await provider.BeginAsync(Request("imgur", new() { ["clientId"] = "c" }), default);
        await RunUpload(upload, new byte[1234]);

        Assert.Null(stub.Requests.Single().DeclaredContentLength);
    }

    [Fact]
    public async Task DeclaredContentLength_AvoidsChunkedEncoding_RawBody()
    {
        var stub = new StubHandler((_, _) => Json("""{"key":"k","secret":"s"}"""));
        var provider = new HastebinDestinationProvider(new StubHttpClientFactory(stub));

        var payload = "hello"u8.ToArray();
        await using var upload = await provider.BeginAsync(Request("hastebin", new(), length: payload.Length), default);
        await RunUpload(upload, payload);

        // raw post: Content-Length is exactly the file length
        Assert.Equal(payload.Length, stub.Requests.Single().DeclaredContentLength);
    }

    [Fact]
    public async Task Backblaze_RequiresContentLength()
    {
        var provider = new BackblazeDestinationProvider(new StubHttpClientFactory(new StubHandler((_, _) => Json("{}"))));
        var creds = new Dictionary<string, string> { ["keyId"] = "k", ["applicationKey"] = "a", ["bucketName"] = "b" };
        await Assert.ThrowsAsync<DestinationConfigException>(() => provider.BeginAsync(Request("backblaze", creds), default));
    }

    [Fact]
    public async Task Azure_RequiresContainerSasUrl()
    {
        var provider = new AzureBlobDestinationProvider();
        await Assert.ThrowsAsync<DestinationConfigException>(() => provider.BeginAsync(Request("azure", new()), default));
    }

    [Fact]
    public async Task DestinationErrorResponse_FailsCommit()
    {
        var stub = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("bad client id") });
        var provider = new ImgurDestinationProvider(new StubHttpClientFactory(stub));

        await using var upload = await provider.BeginAsync(Request("imgur", new() { ["clientId"] = "bad" }), default);
        await upload.WriteStream.WriteAsync(new byte[16]);
        var ex = await Assert.ThrowsAsync<IOException>(() => upload.CommitAsync(CancellationToken.None));
        Assert.Contains("401", ex.Message);
    }
}

using System.Security.Cryptography;
using System.Text;
using Clowd.Server;
using Clowd.Server.Api;
using Clowd.Server.Destinations;
using Clowd.Server.Redirects;
using Clowd.Server.Uploads;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection("Clowd"));
builder.Services.AddSingleton<UploadRegistry>();
builder.Services.AddSingleton<RedirectStore>();
builder.Services.AddSingleton<UploadService>();
builder.Services.AddSingleton<DownloadStreamer>();
builder.Services.AddSingleton<IDestinationProvider, AzureBlobDestinationProvider>();
builder.Services.AddSingleton<IDestinationProvider, BackblazeDestinationProvider>();
builder.Services.AddSingleton<IDestinationProvider, ImgurDestinationProvider>();
builder.Services.AddSingleton<IDestinationProvider, CatboxDestinationProvider>();
builder.Services.AddSingleton<IDestinationProvider, PicsurDestinationProvider>();
builder.Services.AddSingleton<IDestinationProvider, VgyMeDestinationProvider>();
builder.Services.AddSingleton<IDestinationProvider, HastebinDestinationProvider>();
builder.Services.AddHostedService<SessionCleanupService>();

// destination uploads run as long as the client keeps sending; the session idle
// timeout is what reaps stalled transfers, not an http client timeout
builder.Services.AddHttpClient(HttpStreamingUpload.HttpClientName, c => c.Timeout = Timeout.InfiniteTimeSpan);

// uploads are size-checked in the pump against Clowd:MaxUploadBytes, not by kestrel
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = null);

var app = builder.Build();

{
    var opts = app.Services.GetRequiredService<IOptions<ServerOptions>>().Value;
    Directory.CreateDirectory(opts.CachePath);
    Directory.CreateDirectory(opts.RedirectsPath);
}

app.MapGet("/healthz", () => Results.Ok(new { ok = true }));

// ---------------------------------------------------------------------------
// StartUpload: client sends provider credentials + file metadata; gets back an
// upload endpoint and a download url that is shareable immediately.
// ---------------------------------------------------------------------------
app.MapPost("/api/v1/uploads", async (StartUploadRequest request, HttpContext ctx, UploadRegistry registry,
                                      IEnumerable<IDestinationProvider> providers, IOptions<ServerOptions> options,
                                      CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Provider))
        return Results.BadRequest(new { error = "provider is required" });
    if (request.ContentLength is < 0)
        return Results.BadRequest(new { error = "contentLength cannot be negative" });
    if (request.ContentLength > options.Value.MaxUploadBytes)
        return Results.BadRequest(new { error = $"contentLength exceeds the {options.Value.MaxUploadBytes} byte limit" });

    var provider = providers.FirstOrDefault(p => string.Equals(p.Name, request.Provider, StringComparison.OrdinalIgnoreCase));
    if (provider is null)
        return Results.BadRequest(new { error = $"unknown provider '{request.Provider}'" });

    IDestinationUpload destination;
    try
    {
        destination = await provider.BeginAsync(request, ct);
    }
    catch (DestinationConfigException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }

    UploadSession session;
    try
    {
        session = registry.Create(request, destination);
    }
    catch
    {
        await destination.DisposeAsync();
        throw;
    }

    var baseUrl = PublicBaseUrl(options.Value, ctx.Request);
    return Results.Ok(new StartUploadResponse
    {
        Id = session.Id,
        UploadUrl = $"{baseUrl}/api/v1/uploads/{session.Id}?token={session.Token}",
        DownloadUrl = $"{baseUrl}/d/{session.Id}",
        FinalUrl = destination.FinalUrl,
        Delete = destination.DeleteInfo,
    });
});

// ---------------------------------------------------------------------------
// Upload: raw file body. Teed to the cache file (for live downloads) and the
// destination as it arrives; commits the destination at the end.
// ---------------------------------------------------------------------------
app.MapPut("/api/v1/uploads/{id}", async (string id, HttpContext ctx, UploadRegistry registry, UploadService uploads,
                                          IOptions<ServerOptions> options, CancellationToken ct) =>
{
    if (!registry.TryGet(id, out var session))
        return Results.NotFound();

    if (!TokenMatches(ctx.Request, session.Token))
        return Results.Unauthorized();

    if (!session.TryMarkUploadStarted())
        return Results.Conflict(new { error = "upload body was already sent for this id" });

    DestinationResult result;
    try
    {
        result = await uploads.ProcessUploadAsync(session, ctx.Request.Body, ct);
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        return Results.Empty; // client went away; nothing to tell it
    }
    catch (InvalidDataException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (Exception)
    {
        return Results.Problem("upload failed", statusCode: StatusCodes.Status502BadGateway);
    }

    var baseUrl = PublicBaseUrl(options.Value, ctx.Request);
    return Results.Ok(new UploadCompleteResponse
    {
        Id = session.Id,
        DownloadUrl = $"{baseUrl}/d/{session.Id}",
        FinalUrl = result.FinalUrl,
        Length = session.BytesWritten,
        Delete = result.Delete,
    });
});

// ---------------------------------------------------------------------------
// Download: while the upload is in flight, tail the cache file so recipients
// start receiving immediately. Once committed, a persisted 301 to the
// destination is all that remains.
// ---------------------------------------------------------------------------
app.MapMethods("/d/{id}", new[] { "GET", "HEAD" }, async (string id, HttpContext ctx, UploadRegistry registry,
                                                          RedirectStore redirects, DownloadStreamer streamer) =>
{
    if (redirects.TryGet(id, out var record))
    {
        ctx.Response.Redirect(record.Url, permanent: true);
        return;
    }

    if (!registry.TryGet(id, out var session))
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    if (session.State == UploadState.Failed)
    {
        ctx.Response.StatusCode = StatusCodes.Status410Gone;
        return;
    }

    ctx.Response.ContentType = session.ContentType;
    SetContentDisposition(ctx.Response, session.FileName);
    if (session.ExpectedLength is { } length)
        ctx.Response.ContentLength = length;
    // for chains like nginx -> this server, make sure bytes flow as they arrive
    ctx.Response.Headers["X-Accel-Buffering"] = "no";

    if (HttpMethods.IsHead(ctx.Request.Method))
        return;

    try
    {
        // flush headers now — the recipient should see the response begin even if
        // the uploader hasn't sent the first byte yet. An explicit body flush (not
        // just StartAsync) so in-memory test servers release the response too.
        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
        await streamer.StreamAsync(session, ctx.Response.Body, ctx.RequestAborted);
    }
    catch (UploadFailedException)
    {
        // the upload died mid-stream; the byte count won't add up, so kill the
        // connection rather than silently ending with a truncated file
        ctx.Abort();
    }
    catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
    {
        // downloader went away; nothing to do
    }
});

app.Run();

static string PublicBaseUrl(ServerOptions options, HttpRequest request) =>
    string.IsNullOrWhiteSpace(options.PublicBaseUrl)
        ? $"{request.Scheme}://{request.Host}"
        : options.PublicBaseUrl.TrimEnd('/');

static bool TokenMatches(HttpRequest request, string expected)
{
    string? presented = request.Query["token"].FirstOrDefault();
    if (presented is null)
    {
        var auth = request.Headers.Authorization.FirstOrDefault();
        const string prefix = "Bearer ";
        if (auth?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true)
            presented = auth[prefix.Length..];
    }

    if (string.IsNullOrEmpty(presented))
        return false;

    return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(expected));
}

static void SetContentDisposition(HttpResponse response, string fileName)
{
    var disposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("inline");
    disposition.SetHttpFileName(fileName);
    response.Headers.ContentDisposition = disposition.ToString();
}

// exposed for WebApplicationFactory in tests
public partial class Program
{ }

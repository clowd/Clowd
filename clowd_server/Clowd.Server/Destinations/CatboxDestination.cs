using Clowd.Server.Api;

namespace Clowd.Server.Destinations;

/// <summary>
/// catbox.moe (permanent) and litterbox.catbox.moe (timed expiry). Anonymous uploads can't
/// be deleted; with a userhash the file can be removed via the catbox account api.
/// </summary>
public sealed class CatboxDestinationProvider(IHttpClientFactory httpFactory) : IDestinationProvider
{
    private static readonly string[] LitterboxTimes = ["1h", "12h", "24h", "72h"];

    public string Name => "catbox";

    public Task<IDestinationUpload> BeginAsync(StartUploadRequest request, CancellationToken ct)
    {
        var expiry = Creds.Optional(request, "expiry"); // never (default) | 1h | 12h | 24h | 72h
        var userHash = Creds.Optional(request, "userHash");

        string url;
        var fields = new Dictionary<string, string> { ["reqtype"] = "fileupload" };
        if (expiry is null || expiry.Equals("never", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://catbox.moe/user/api.php";
            if (userHash is not null)
                fields["userhash"] = userHash;
        }
        else if (LitterboxTimes.Contains(expiry, StringComparer.OrdinalIgnoreCase))
        {
            url = "https://litterbox.catbox.moe/resources/internals/api.php";
            fields["time"] = expiry.ToLowerInvariant();
        }
        else
        {
            throw new DestinationConfigException($"catbox expiry must be one of: never, {string.Join(", ", LitterboxTimes)}");
        }

        var upload = new HttpStreamingUpload(
            httpFactory.CreateClient(HttpStreamingUpload.HttpClientName),
            fileStream => FormUpload.Multipart(url, fileStream, "fileToUpload", request, fields),
            async (response, token) =>
            {
                // catbox responds with the bare file url as text
                var body = (await response.Content.ReadAsStringAsync(token)).Trim();
                if (!Uri.TryCreate(body, UriKind.Absolute, out _))
                    throw new IOException($"catbox upload failed: {body}");

                return new DestinationResult
                {
                    FinalUrl = body,
                    Delete = userHash is null
                        ? null
                        : new UploadDeleteInfo
                        {
                            Provider = Name,
                            // reqtype=deletefiles&userhash=...&files=<this name>
                            DeleteKey = body[(body.LastIndexOf('/') + 1)..],
                        },
                };
            });

        return Task.FromResult<IDestinationUpload>(upload);
    }
}

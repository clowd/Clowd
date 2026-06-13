using System.Text.Json;
using Clowd.Server.Api;

namespace Clowd.Server.Destinations;

/// <summary>Hastebin-compatible paste services (default pastie.io). The body is posted raw, not as a form.</summary>
public sealed class HastebinDestinationProvider(IHttpClientFactory httpFactory) : IDestinationProvider
{
    public string Name => "hastebin";

    public Task<IDestinationUpload> BeginAsync(StartUploadRequest request, CancellationToken ct)
    {
        var baseUrl = (Creds.Optional(request, "url") ?? "https://pastie.io").TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out _))
            throw new DestinationConfigException($"hastebin url '{baseUrl}' is not a valid absolute url");

        var extension = Path.GetExtension(Creds.SanitizeFileName(request.FileName));

        var upload = new HttpStreamingUpload(
            httpFactory.CreateClient(HttpStreamingUpload.HttpClientName),
            fileStream =>
            {
                var content = new StreamContent(fileStream);
                // raw body: Content-Length is exactly the file length when the client declared it,
                // avoiding chunked transfer encoding (the body still streams)
                if (request.ContentLength is { } length)
                    content.Headers.ContentLength = length;
                return new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/documents") { Content = content };
            },
            async (response, token) =>
            {
                var json = await response.Content.ReadAsStringAsync(token);
                var parsed = JsonSerializer.Deserialize<HastebinResponse>(json);
                if (string.IsNullOrEmpty(parsed?.key))
                    throw new IOException($"hastebin upload failed: {json}");

                return new DestinationResult
                {
                    FinalUrl = $"{baseUrl}/{parsed.key}{extension}",
                    Delete = string.IsNullOrEmpty(parsed.secret)
                        ? null
                        : new UploadDeleteInfo { Provider = Name, UploadKey = parsed.key, DeleteKey = parsed.secret },
                };
            });

        return Task.FromResult<IDestinationUpload>(upload);
    }

#pragma warning disable IDE1006 // names match the hastebin wire format
    private sealed class HastebinResponse
    {
        public string? key { get; set; }
        public string? secret { get; set; } // pastie.io only; native hastebin has no deletion
    }
#pragma warning restore IDE1006
}

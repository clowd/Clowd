using System.Text.Json;
using Clowd.Server.Api;

namespace Clowd.Server.Destinations;

public sealed class VgyMeDestinationProvider(IHttpClientFactory httpFactory) : IDestinationProvider
{
    public string Name => "vgyme";

    public Task<IDestinationUpload> BeginAsync(StartUploadRequest request, CancellationToken ct)
    {
        var userKey = Creds.Require(request, "userKey", Name);

        var upload = new HttpStreamingUpload(
            httpFactory.CreateClient(HttpStreamingUpload.HttpClientName),
            fileStream => FormUpload.Multipart("https://vgy.me/upload", fileStream, "file", request,
                                               new Dictionary<string, string> { ["userkey"] = userKey }),
            async (response, token) =>
            {
                var json = await response.Content.ReadAsStringAsync(token);
                var parsed = JsonSerializer.Deserialize<VgyResponse>(json);
                if (parsed is null || parsed.error || string.IsNullOrEmpty(parsed.image))
                    throw new IOException($"vgy.me upload failed: {json}");

                return new DestinationResult
                {
                    FinalUrl = parsed.image,
                    Delete = new UploadDeleteInfo
                    {
                        Provider = Name,
                        UploadKey = parsed.filename,
                        DeleteUrl = parsed.delete,
                    },
                };
            });

        return Task.FromResult<IDestinationUpload>(upload);
    }

#pragma warning disable IDE1006 // names match the vgy.me wire format
    private sealed class VgyResponse
    {
        public bool error { get; set; }
        public string? filename { get; set; }
        public string? image { get; set; }
        public string? delete { get; set; }
    }
#pragma warning restore IDE1006
}

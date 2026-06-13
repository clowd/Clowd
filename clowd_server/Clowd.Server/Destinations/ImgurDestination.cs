using System.Net.Http.Headers;
using System.Text.Json;
using Clowd.Server.Api;

namespace Clowd.Server.Destinations;

public sealed class ImgurDestinationProvider(IHttpClientFactory httpFactory) : IDestinationProvider
{
    public string Name => "imgur";

    public Task<IDestinationUpload> BeginAsync(StartUploadRequest request, CancellationToken ct)
    {
        var clientId = Creds.Require(request, "clientId", Name);

        var upload = new HttpStreamingUpload(
            httpFactory.CreateClient(HttpStreamingUpload.HttpClientName),
            fileStream => FormUpload.Multipart("https://api.imgur.com/3/upload", fileStream, "image", request,
                                               new Dictionary<string, string> { ["type"] = "file" },
                                               new AuthenticationHeaderValue("Client-ID", clientId)),
            async (response, token) =>
            {
                var json = await response.Content.ReadAsStringAsync(token);
                var parsed = JsonSerializer.Deserialize<ImgurResponse>(json);
                if (parsed?.success != true || string.IsNullOrEmpty(parsed.data?.link))
                    throw new IOException($"imgur upload failed (status {parsed?.status}): {json}");

                return new DestinationResult
                {
                    FinalUrl = parsed.data.link,
                    Delete = new UploadDeleteInfo
                    {
                        Provider = Name,
                        UploadKey = parsed.data.id,
                        DeleteKey = parsed.data.deletehash,
                        // anonymous deletion: DELETE with the same Client-ID auth
                        DeleteUrl = $"https://api.imgur.com/3/image/{parsed.data.deletehash}",
                    },
                };
            });

        return Task.FromResult<IDestinationUpload>(upload);
    }

#pragma warning disable IDE1006 // names match the imgur wire format
    private sealed class ImgurResponse
    {
        public ImgurData? data { get; set; }
        public bool success { get; set; }
        public int status { get; set; }
    }

    private sealed class ImgurData
    {
        public string? id { get; set; }
        public string? deletehash { get; set; }
        public string? link { get; set; }
    }
#pragma warning restore IDE1006
}

using System.Net.Http.Headers;
using System.Text.Json;
using Clowd.Server.Api;

namespace Clowd.Server.Destinations;

/// <summary>Self-hosted picsur instance; baseUrl points at the user's server.</summary>
public sealed class PicsurDestinationProvider(IHttpClientFactory httpFactory) : IDestinationProvider
{
    public string Name => "picsur";

    public Task<IDestinationUpload> BeginAsync(StartUploadRequest request, CancellationToken ct)
    {
        var baseUrl = Creds.Require(request, "baseUrl", Name).TrimEnd('/', '\\');
        var apiKey = Creds.Optional(request, "apiKey");
        var directLink = string.Equals(Creds.Optional(request, "directLink"), "true", StringComparison.OrdinalIgnoreCase);
        var extension = Path.GetExtension(Creds.SanitizeFileName(request.FileName));

        var upload = new HttpStreamingUpload(
            httpFactory.CreateClient(HttpStreamingUpload.HttpClientName),
            fileStream => FormUpload.Multipart($"{baseUrl}/api/image/upload", fileStream, "image", request,
                                               auth: apiKey is null ? null : new AuthenticationHeaderValue("Api-Key", apiKey)),
            async (response, token) =>
            {
                var json = await response.Content.ReadAsStringAsync(token);
                var parsed = JsonSerializer.Deserialize<PicsurResponse>(json);
                if (parsed?.success != true || string.IsNullOrEmpty(parsed.data?.id))
                    throw new IOException($"picsur upload failed: {parsed?.data?.message ?? json}");

                return new DestinationResult
                {
                    FinalUrl = directLink ? $"{baseUrl}/i/{parsed.data.id}{extension}" : $"{baseUrl}/view/{parsed.data.id}",
                    Delete = new UploadDeleteInfo
                    {
                        Provider = Name,
                        UploadKey = parsed.data.id,
                        DeleteKey = parsed.data.delete_key,
                    },
                };
            });

        return Task.FromResult<IDestinationUpload>(upload);
    }

#pragma warning disable IDE1006 // names match the picsur wire format
    private sealed class PicsurResponse
    {
        public bool success { get; set; }
        public PicsurData? data { get; set; }
    }

    private sealed class PicsurData
    {
        public string? id { get; set; }
        public string? delete_key { get; set; }
        public string? message { get; set; }
    }
#pragma warning restore IDE1006
}

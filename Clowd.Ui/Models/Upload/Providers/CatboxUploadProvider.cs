using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Clowd.Ui.Models.Upload.Providers;

/// <summary>
/// Catbox.moe anonymous file upload — no API key, simple multipart POST.
/// </summary>
public sealed class CatboxUploadProvider : IUploadProvider
{
    public string Name => "Catbox";
    public string Description => "Anonymous file hosting at catbox.moe (no account required).";
    public SupportedUploadType SupportedUpload => SupportedUploadType.All;

    private static readonly HttpClient _http = new();

    public async Task<string> UploadAsync(Stream content, string fileName, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("fileupload"), "reqtype");

        var fileContent = new StreamContent(content);
        form.Add(fileContent, "fileToUpload", fileName);

        using var response = await _http.PostAsync("https://catbox.moe/user/api.php", form, ct);
        response.EnsureSuccessStatusCode();

        var url = (await response.Content.ReadAsStringAsync(ct)).Trim();
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Catbox returned an unexpected response: {url}");

        return url;
    }
}

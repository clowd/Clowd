using System.Net.Http.Headers;
using Clowd.Server.Api;

namespace Clowd.Server.Destinations;

internal static class FormUpload
{
    /// <summary>Builds a multipart-form upload request whose file part streams from <paramref name="fileStream"/>.</summary>
    public static HttpRequestMessage Multipart(string url, Stream fileStream, string fileFieldName, StartUploadRequest request,
                                               Dictionary<string, string>? otherFields = null,
                                               AuthenticationHeaderValue? auth = null)
    {
        var content = new MultipartFormDataContent();

        // When the client declared the size, the file part reports its length so the
        // multipart wrapper can compute its whole length (boundaries + fields + file) and
        // the outgoing request carries Content-Length instead of Transfer-Encoding:
        // chunked — the body still streams. Without it the pipe-backed stream is
        // non-seekable, so the length isn't computable and HttpClient falls back to
        // chunked, which some hosts reject. If the actual byte count later disagrees, the
        // upload fails (as it should).
        HttpContent filePart = request.ContentLength is { } length
            ? new KnownLengthStreamContent(fileStream, length)
            : new StreamContent(fileStream);
        filePart.Headers.ContentType = new MediaTypeHeaderValue(Creds.ContentTypeOrDefault(request));

        content.Add(filePart, fileFieldName, Creds.SanitizeFileName(request.FileName));

        if (otherFields is not null)
        {
            foreach (var (key, value) in otherFields)
                content.Add(new StringContent(value), key);
        }

        var message = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        message.Headers.Authorization = auth;
        return message;
    }
}

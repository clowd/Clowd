using Clowd.Server.Api;

namespace Clowd.Server.Destinations;

internal static class Creds
{
    public static string Require(StartUploadRequest request, string key, string provider)
    {
        var value = Optional(request, key);
        if (string.IsNullOrWhiteSpace(value))
            throw new DestinationConfigException($"{provider} requires credentials.{key}");
        return value;
    }

    public static string? Optional(StartUploadRequest request, string key)
    {
        if (request.Credentials is { } creds && creds.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            return value;
        return null;
    }

    public static string SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "file";
        var chars = name.Where(c => !char.IsControl(c) && c != '"' && c != '\\').ToArray();
        return chars.Length == 0 ? "file" : new string(chars);
    }

    public static string ContentTypeOrDefault(StartUploadRequest request) =>
        string.IsNullOrWhiteSpace(request.ContentType) ? "application/octet-stream" : request.ContentType;
}

namespace Clowd.Server;

public sealed class ServerOptions
{
    /// <summary>Directory where in-progress uploads are buffered. Should be a docker volume.</summary>
    public string CachePath { get; set; } = "data/cache";

    /// <summary>Directory where completed-upload redirects are persisted. Should be a docker volume.</summary>
    public string RedirectsPath { get; set; } = "data/redirects";

    /// <summary>
    /// Public origin used when building upload/download urls (e.g. "https://share.example.com").
    /// When unset, urls are derived from the incoming request.
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>Hard cap on a single upload.</summary>
    public long MaxUploadBytes { get; set; } = 10L * 1024 * 1024 * 1024;

    /// <summary>An upload session with no incoming bytes for this long is failed and discarded.</summary>
    public TimeSpan UploadIdleTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>How long a finished session lingers in memory so in-flight downloads can drain from the cache file.</summary>
    public TimeSpan FinishedLinger { get; set; } = TimeSpan.FromMinutes(1);
}

namespace Clowd.Server.Api;

public sealed class StartUploadRequest
{
    /// <summary>Destination provider name, e.g. "azure".</summary>
    public string? Provider { get; set; }

    /// <summary>Original file name, used for Content-Disposition hints.</summary>
    public string? FileName { get; set; }

    public string? ContentType { get; set; }

    /// <summary>
    /// Total upload size if known. When provided, downloads served by the proxy get a
    /// Content-Length header and the upload is rejected if the byte count doesn't match.
    /// </summary>
    public long? ContentLength { get; set; }

    /// <summary>
    /// Provider-specific credentials/settings, e.g. for azure:
    /// connectionString, containerName, customDomain (optional).
    /// Held in memory only for the duration of the upload — never persisted.
    /// </summary>
    public Dictionary<string, string>? Credentials { get; set; }
}

public sealed class StartUploadResponse
{
    public required string Id { get; set; }

    /// <summary>PUT the raw file body here (contains the secret upload token).</summary>
    public required string UploadUrl { get; set; }

    /// <summary>Shareable immediately — streams from the proxy until the upload commits, then 301s to the destination.</summary>
    public required string DownloadUrl { get; set; }

    /// <summary>Final destination url, when the provider knows it before the upload completes.</summary>
    public string? FinalUrl { get; set; }

    /// <summary>How to delete the upload later, when known before the upload completes.</summary>
    public UploadDeleteInfo? Delete { get; set; }
}

public sealed class UploadCompleteResponse
{
    public required string Id { get; set; }
    public required string DownloadUrl { get; set; }
    public required string FinalUrl { get; set; }
    public long Length { get; set; }
    public UploadDeleteInfo? Delete { get; set; }
}

public sealed class UploadDeleteInfo
{
    public string? Provider { get; set; }

    /// <summary>Provider-side object key — for providers where the client deletes with its own credentials (azure).</summary>
    public string? UploadKey { get; set; }

    /// <summary>Deletion secret returned by the destination (imgur deletehash, picsur delete_key, b2 fileId, ...).</summary>
    public string? DeleteKey { get; set; }

    /// <summary>Direct deletion url — for hosts that hand one back (vgy.me, imgur).</summary>
    public string? DeleteUrl { get; set; }
}

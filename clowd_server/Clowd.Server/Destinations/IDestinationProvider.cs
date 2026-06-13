using Clowd.Server.Api;

namespace Clowd.Server.Destinations;

/// <summary>Thrown when a StartUpload request is missing/has invalid provider credentials. Mapped to HTTP 400.</summary>
public sealed class DestinationConfigException(string message) : Exception(message);

public interface IDestinationProvider
{
    /// <summary>Name matched (case-insensitively) against <see cref="StartUploadRequest.Provider"/>.</summary>
    string Name { get; }

    /// <summary>
    /// Open a streaming upload to the destination. Called during StartUpload, before any
    /// file bytes exist — must not block on file content.
    /// </summary>
    Task<IDestinationUpload> BeginAsync(StartUploadRequest request, CancellationToken ct);
}

public interface IDestinationUpload : IAsyncDisposable
{
    /// <summary>Final public url, if the provider knows it before the upload completes (azure does).</summary>
    string? FinalUrl { get; }

    /// <summary>Deletion info, if known before the upload completes.</summary>
    UploadDeleteInfo? DeleteInfo { get; }

    /// <summary>File bytes are written here as they arrive from the client.</summary>
    Stream WriteStream { get; }

    /// <summary>Finalize the destination object (e.g. commit the azure block list) and return the definitive result.</summary>
    Task<DestinationResult> CommitAsync(CancellationToken ct);

    /// <summary>Discard a partial upload. Must not publish partial data.</summary>
    Task AbortAsync();
}

public sealed class DestinationResult
{
    public required string FinalUrl { get; set; }
    public UploadDeleteInfo? Delete { get; set; }
}

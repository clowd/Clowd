using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Clowd.Server.Api;

namespace Clowd.Server.Destinations;

/// <summary>
/// Streams to an azure block blob. Blocks are staged as bytes arrive and the block list is
/// committed at the end — the blob only becomes visible at azure once committed, which is
/// exactly why the proxy serves downloads from its local cache in the meantime.
///
/// Auth is a container SAS url (with create+write permissions) rather than account
/// credentials, so the server is never trusted with more than the ability to add blobs.
/// </summary>
public sealed class AzureBlobDestinationProvider : IDestinationProvider
{
    public string Name => "azure";

    public async Task<IDestinationUpload> BeginAsync(StartUploadRequest request, CancellationToken ct)
    {
        // e.g. https://account.blob.core.windows.net/container?sv=...&sp=cw...&sig=...
        var sasUrl = Creds.Require(request, "containerSasUrl", Name);
        var customDomain = Creds.Optional(request, "customDomain");

        BlockBlobClient blob;
        BlobContainerClient container;
        try
        {
            container = new BlobContainerClient(new Uri(sasUrl));
            if (string.IsNullOrEmpty(container.Name))
                throw new DestinationConfigException("containerSasUrl must point at a container, not the account root");
            blob = container.GetBlockBlobClient(Guid.NewGuid().ToString("N"));
        }
        catch (UriFormatException ex)
        {
            throw new DestinationConfigException($"invalid containerSasUrl: {ex.Message}");
        }

        var headers = new BlobHttpHeaders
        {
            ContentType = string.IsNullOrWhiteSpace(request.ContentType) ? "application/octet-stream" : request.ContentType,
            // filename hint + render inline if the browser can; it downloads otherwise anyway
            ContentDisposition = $"inline; filename=\"{Creds.SanitizeFileName(request.FileName)}\"",
        };

        var stream = await blob.OpenWriteAsync(overwrite: true, new BlockBlobOpenWriteOptions { HttpHeaders = headers }, ct);

        // the blob uri carries the sas query string — strip it for the public url
        var finalUrl = string.IsNullOrWhiteSpace(customDomain)
            ? new UriBuilder(blob.Uri) { Query = null }.Uri.ToString()
            : $"https://{customDomain}/{container.Name}/{blob.Name}";

        return new AzureBlobUpload(stream, finalUrl, blob.Name);
    }
}

internal sealed class AzureBlobUpload(Stream stream, string finalUrl, string blobName) : IDestinationUpload
{
    private bool _committed;

    public string? FinalUrl => finalUrl;

    public UploadDeleteInfo? DeleteInfo => new()
    {
        Provider = "azure",
        // the client deletes with its own credentials, so the blob key is all it needs
        UploadKey = blobName,
    };

    public Stream WriteStream => stream;

    public async Task<DestinationResult> CommitAsync(CancellationToken ct)
    {
        // disposing the block blob write stream commits the staged block list
        await stream.FlushAsync(ct);
        await stream.DisposeAsync();
        _committed = true;
        return new DestinationResult { FinalUrl = finalUrl, Delete = DeleteInfo };
    }

    public Task AbortAsync()
    {
        // intentionally do NOT dispose the stream — that would commit the partial block
        // list. Uncommitted blocks are garbage-collected by azure after 7 days.
        _committed = true;
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_committed)
            await AbortAsync();
    }
}

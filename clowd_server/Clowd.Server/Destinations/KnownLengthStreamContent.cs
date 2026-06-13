using System.Net;

namespace Clowd.Server.Destinations;

/// <summary>
/// Streams from a (possibly non-seekable) source while reporting a known length. Plain
/// StreamContent over a non-seekable stream reports no length, which forces
/// Transfer-Encoding: chunked — and inside a multipart body the explicitly-set
/// Content-Length header is ignored, because MultipartContent sums each part via
/// TryComputeLength() directly. Overriding TryComputeLength is what actually lets the
/// outgoing request carry Content-Length instead of chunked. The body still streams.
/// </summary>
internal sealed class KnownLengthStreamContent(Stream source, long length) : HttpContent
{
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        source.CopyToAsync(stream);

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
        source.CopyToAsync(stream, cancellationToken);

    protected override bool TryComputeLength(out long computedLength)
    {
        computedLength = length;
        return true;
    }
}

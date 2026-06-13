using System.Buffers;
using Clowd.Server.Destinations;
using Clowd.Server.Redirects;
using Microsoft.Extensions.Options;

namespace Clowd.Server.Uploads;

/// <summary>
/// Pumps the incoming upload body into the local cache file and the destination
/// simultaneously, publishing progress so concurrent downloads can tail the cache.
/// </summary>
public sealed class UploadService(RedirectStore redirects, IOptions<ServerOptions> options, ILogger<UploadService> logger)
{
    private const int BufferSize = 128 * 1024;

    public async Task<DestinationResult> ProcessUploadAsync(UploadSession session, Stream source, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long total = 0;
            var cacheStream = new FileStream(session.CachePath, FileMode.Open, FileAccess.Write,
                                             FileShare.ReadWrite | FileShare.Delete, BufferSize,
                                             FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (cacheStream)
            {
                int read;
                while ((read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), ct)) > 0)
                {
                    total += read;
                    if (total > options.Value.MaxUploadBytes)
                        throw new InvalidDataException($"upload exceeds the {options.Value.MaxUploadBytes} byte limit");
                    if (session.ExpectedLength is { } expectedMax && total > expectedMax)
                        throw new InvalidDataException($"upload exceeds the declared contentLength of {expectedMax}");

                    var chunk = buffer.AsMemory(0, read);
                    var cacheWrite = cacheStream.WriteAsync(chunk, ct).AsTask();
                    var destWrite = session.Destination.WriteStream.WriteAsync(chunk, ct).AsTask();
                    await Task.WhenAll(cacheWrite, destWrite);

                    // flush to the OS page cache before publishing, so tailing readers see the bytes
                    await cacheStream.FlushAsync(ct);
                    session.Publish(total);
                }
            }

            if (session.ExpectedLength is { } expected && total != expected)
                throw new InvalidDataException($"upload ended at {total} bytes but contentLength declared {expected}");

            var result = await session.Destination.CommitAsync(ct);

            // persist the redirect *before* marking complete: from the moment downloads
            // stop being served from cache, the 301 must already exist
            redirects.Save(session.Id, new RedirectRecord
            {
                Url = result.FinalUrl,
                FileName = session.FileName,
                ContentType = session.ContentType,
                CompletedUtc = DateTimeOffset.UtcNow,
            });

            session.Complete(result);
            logger.LogInformation("upload {Id} completed: {Bytes} bytes -> {Url}", session.Id, total, result.FinalUrl);
            return result;
        }
        catch (Exception ex)
        {
            session.Fail(ex);
            logger.LogWarning(ex, "upload {Id} failed after {Bytes} bytes", session.Id, session.BytesWritten);
            try
            {
                await session.Destination.AbortAsync();
            }
            catch (Exception abortEx)
            {
                logger.LogWarning(abortEx, "failed to abort destination for upload {Id}", session.Id);
            }

            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

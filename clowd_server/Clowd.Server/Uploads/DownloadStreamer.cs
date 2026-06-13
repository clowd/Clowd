using System.Buffers;

namespace Clowd.Server.Uploads;

/// <summary>Thrown to a downloader when the upload it is tailing fails — the response must be aborted, not ended cleanly.</summary>
public sealed class UploadFailedException(string uploadId, Exception? inner) : Exception($"upload {uploadId} failed mid-stream", inner);

/// <summary>
/// Serves a download by tailing the session's cache file: streams everything published so
/// far, then parks until the uploader publishes more, until the upload completes.
/// </summary>
public sealed class DownloadStreamer
{
    private const int BufferSize = 128 * 1024;

    public async Task StreamAsync(UploadSession session, Stream output, CancellationToken ct)
    {
        using var lease = session.OpenReader();
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long position = 0;
            while (true)
            {
                long available = session.BytesWritten;
                if (position < available)
                {
                    int want = (int)Math.Min(BufferSize, available - position);
                    int read = await lease.Stream.ReadAsync(buffer.AsMemory(0, want), ct);
                    if (read == 0)
                    {
                        // published length is ahead of what this handle can see yet; retry shortly
                        await Task.Delay(10, ct);
                        continue;
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    // push each chunk to the recipient immediately — this stream exists
                    // so bytes flow in real time, buffering defeats the point
                    await output.FlushAsync(ct);
                    position += read;
                }
                else
                {
                    switch (session.State)
                    {
                        case UploadState.Completed when position >= session.BytesWritten:
                            return;
                        case UploadState.Failed:
                            throw new UploadFailedException(session.Id, session.Error);
                        default:
                            await session.WaitForDataAsync(position, ct);
                            break;
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

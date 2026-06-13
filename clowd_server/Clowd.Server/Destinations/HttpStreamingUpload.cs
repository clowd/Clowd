using System.IO.Pipelines;
using Clowd.Server.Api;

namespace Clowd.Server.Destinations;

/// <summary>
/// Generic engine for http-based destinations (imgur, catbox, vgy.me, ...): the outgoing
/// request is started immediately with its body backed by a pipe, so destination bytes go
/// out as the client's bytes come in. The destination's response (public url, delete key)
/// is parsed at commit time.
/// </summary>
public sealed class HttpStreamingUpload : IDestinationUpload
{
    public const string HttpClientName = "destination";

    private readonly HttpClient _http;
    private readonly Pipe _pipe;
    private readonly Stream _pipeWriterStream;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task<HttpResponseMessage> _responseTask;
    private readonly Func<HttpResponseMessage, CancellationToken, Task<DestinationResult>> _parseResponse;

    /// <param name="requestFactory">Builds the outgoing request; receives the stream the file bytes will arrive on.</param>
    /// <param name="parseResponse">Turns the destination's (success-status) response into the final result.</param>
    public HttpStreamingUpload(HttpClient http, Func<Stream, HttpRequestMessage> requestFactory,
                               Func<HttpResponseMessage, CancellationToken, Task<DestinationResult>> parseResponse,
                               string? finalUrl = null, UploadDeleteInfo? deleteInfo = null)
    {
        // generous buffering, but bounded — backpressure from a slow destination
        // propagates to the uploading client instead of ballooning memory
        _pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 4 * 1024 * 1024, resumeWriterThreshold: 1024 * 1024,
                                         useSynchronizationContext: false));
        _http = http;
        _pipeWriterStream = _pipe.Writer.AsStream();
        _parseResponse = parseResponse;
        FinalUrl = finalUrl;
        DeleteInfo = deleteInfo;

        var request = requestFactory(_pipe.Reader.AsStream());
        _responseTask = SendAsync(http, request);
        WriteStream = new GuardedWriteStream(this);
    }

    public string? FinalUrl { get; }
    public UploadDeleteInfo? DeleteInfo { get; }
    public Stream WriteStream { get; }

    private async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpRequestMessage request)
    {
        try
        {
            return await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, _cts.Token);
        }
        finally
        {
            // if the destination ends the exchange before we finish writing (auth error,
            // size limit, ...), unblock a writer parked on pipe backpressure
            _pipe.Writer.CancelPendingFlush();
        }
    }

    public async Task<DestinationResult> CommitAsync(CancellationToken ct)
    {
        await _pipeWriterStream.DisposeAsync(); // signals end-of-body to the outgoing request
        using var response = await _responseTask.WaitAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await SafeReadBody(response, ct);
            throw new IOException($"destination returned {(int)response.StatusCode}: {Truncate(body)}");
        }

        return await _parseResponse(response, ct);
    }

    public Task AbortAsync()
    {
        _cts.Cancel();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            (await _responseTask).Dispose();
        }
        catch
        {
            // request already failed/cancelled — nothing to release
        }

        _http.Dispose();
        _cts.Dispose();
    }

    private static async Task<string> SafeReadBody(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch
        {
            return "";
        }
    }

    private static string Truncate(string s) => s.Length <= 500 ? s : s[..500];

    /// <summary>
    /// Wraps the pipe writer so that a destination which has already finished its response
    /// fails the upload promptly instead of stalling on backpressure forever.
    /// </summary>
    private sealed class GuardedWriteStream(HttpStreamingUpload owner) : Stream
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            await ThrowIfResponseEnded(ct);
            try
            {
                await owner._pipeWriterStream.WriteAsync(buffer, ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && owner._responseTask.IsCompleted)
            {
                await ThrowIfResponseEnded(ct);
                throw;
            }
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        private async Task ThrowIfResponseEnded(CancellationToken ct)
        {
            if (!owner._responseTask.IsCompleted)
                return;

            string detail;
            try
            {
                using var response = await owner._responseTask;
                detail = $"status {(int)response.StatusCode}: {Truncate(await SafeReadBody(response, ct))}";
            }
            catch (Exception ex)
            {
                detail = ex.Message;
            }

            throw new IOException($"destination ended the upload early ({detail})");
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

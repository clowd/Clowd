using System.IO.Pipelines;
using Clowd.Server.Api;
using Clowd.Server.Redirects;
using Clowd.Server.Uploads;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Clowd.Server.Tests;

/// <summary>
/// Exercises the core promise of the proxy: a download started mid-upload receives bytes
/// before the upload has finished, and ends up with the complete, identical file.
/// </summary>
public sealed class StreamingTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly string _tmp;
    private readonly UploadRegistry _registry;
    private readonly UploadService _uploads;
    private readonly RedirectStore _redirects;
    private readonly DownloadStreamer _streamer = new();

    public StreamingTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "clowd-server-tests-" + Guid.NewGuid().ToString("N"));
        var options = Options.Create(new ServerOptions
        {
            CachePath = Path.Combine(_tmp, "cache"),
            RedirectsPath = Path.Combine(_tmp, "redirects"),
        });
        _registry = new UploadRegistry(options);
        _redirects = new RedirectStore(options, NullLogger<RedirectStore>.Instance);
        _uploads = new UploadService(_redirects, options, NullLogger<UploadService>.Instance);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tmp, recursive: true);
        }
        catch (IOException)
        { }
    }

    private UploadSession CreateSession(FakeDestinationUpload destination, long? contentLength = null) =>
        _registry.Create(new StartUploadRequest
        {
            Provider = "fake",
            FileName = "test.bin",
            ContentType = "application/octet-stream",
            ContentLength = contentLength,
        }, destination);

    [Fact]
    public async Task DownloadStreamsWhileUploadIsStillInProgress()
    {
        var destination = new FakeDestinationUpload("k1");
        var session = CreateSession(destination);

        var chunk1 = MakeChunk(0xAA, 256 * 1024);
        var chunk2 = MakeChunk(0xBB, 256 * 1024);

        var pipe = new Pipe();
        var uploadTask = _uploads.ProcessUploadAsync(session, pipe.Reader.AsStream(), CancellationToken.None);

        var sink = new ObservableSink();
        var downloadTask = _streamer.StreamAsync(session, sink, CancellationToken.None);

        // feed the first chunk and hold the upload open
        await pipe.Writer.WriteAsync(chunk1);
        await pipe.Writer.FlushAsync();

        // the downloader must receive those bytes while the upload is demonstrably unfinished
        await sink.WaitForLengthAsync(chunk1.Length, Timeout);
        Assert.False(uploadTask.IsCompleted);
        Assert.False(destination.Committed);

        await pipe.Writer.WriteAsync(chunk2);
        pipe.Writer.Complete();

        await uploadTask.WaitAsync(Timeout);
        await downloadTask.WaitAsync(Timeout);

        var expected = chunk1.Concat(chunk2).ToArray();
        Assert.Equal(expected, sink.ToArray());
        Assert.Equal(expected, destination.CommittedBytes);
        Assert.Equal(UploadState.Completed, session.State);

        // the persisted redirect is all that should remain after completion
        Assert.True(_redirects.TryGet(session.Id, out var record));
        Assert.Equal(destination.FinalUrl, record.Url);
    }

    [Fact]
    public async Task DownloadStartedAfterCompletionStillDrainsFromCache()
    {
        var destination = new FakeDestinationUpload("k2");
        var session = CreateSession(destination);

        var payload = MakeChunk(0x42, 64 * 1024);
        await _uploads.ProcessUploadAsync(session, new MemoryStream(payload), CancellationToken.None);

        var sink = new ObservableSink();
        await _streamer.StreamAsync(session, sink, CancellationToken.None).WaitAsync(Timeout);
        Assert.Equal(payload, sink.ToArray());
    }

    [Fact]
    public async Task DownloadFailsWhenUploadDiesMidStream()
    {
        var destination = new FakeDestinationUpload("k3");
        var session = CreateSession(destination);

        var pipe = new Pipe();
        var uploadTask = _uploads.ProcessUploadAsync(session, pipe.Reader.AsStream(), CancellationToken.None);

        var sink = new ObservableSink();
        var downloadTask = _streamer.StreamAsync(session, sink, CancellationToken.None);

        await pipe.Writer.WriteAsync(MakeChunk(0x11, 1024));
        await pipe.Writer.FlushAsync();
        await sink.WaitForLengthAsync(1024, Timeout);

        pipe.Writer.Complete(new IOException("simulated client disconnect"));

        await Assert.ThrowsAsync<IOException>(() => uploadTask.WaitAsync(Timeout));
        await Assert.ThrowsAsync<UploadFailedException>(() => downloadTask.WaitAsync(Timeout));
        Assert.Equal(UploadState.Failed, session.State);
        Assert.True(destination.Aborted);
        Assert.False(_redirects.TryGet(session.Id, out _));
    }

    [Fact]
    public async Task UploadFailsWhenByteCountDisagreesWithDeclaredLength()
    {
        var destination = new FakeDestinationUpload("k4");
        var session = CreateSession(destination, contentLength: 100);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => _uploads.ProcessUploadAsync(session, new MemoryStream(MakeChunk(0x01, 50)), CancellationToken.None));
        Assert.Equal(UploadState.Failed, session.State);
        Assert.True(destination.Aborted);
    }

    private static byte[] MakeChunk(byte value, int length)
    {
        var chunk = new byte[length];
        Array.Fill(chunk, value);
        return chunk;
    }

    /// <summary>Memory sink whose observed length can be awaited from another thread.</summary>
    private sealed class ObservableSink : Stream
    {
        private readonly MemoryStream _inner = new();
        private readonly object _gate = new();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            lock (_gate)
                _inner.Write(buffer.Span);
            await System.Threading.Tasks.Task.CompletedTask;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (_gate)
                _inner.Write(buffer, offset, count);
        }

        public async System.Threading.Tasks.Task WaitForLengthAsync(long length, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                lock (_gate)
                {
                    if (_inner.Length >= length)
                        return;
                }

                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException($"sink never reached {length} bytes");
                await System.Threading.Tasks.Task.Delay(10);
            }
        }

        public byte[] ToArray()
        {
            lock (_gate)
                return _inner.ToArray();
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length { get { lock (_gate) return _inner.Length; } }
        public override long Position { get => Length; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

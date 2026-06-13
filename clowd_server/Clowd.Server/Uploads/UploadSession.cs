using Clowd.Server.Destinations;

namespace Clowd.Server.Uploads;

public enum UploadState
{
    Uploading = 0,
    Completed = 1,
    Failed = 2,
}

/// <summary>
/// In-memory state for one in-progress upload. The uploader thread appends to the cache
/// file and calls <see cref="Publish"/>; any number of download threads tail the file,
/// parking in <see cref="WaitForDataAsync"/> until more bytes (or a terminal state) arrive.
/// </summary>
public sealed class UploadSession
{
    public required string Id { get; init; }
    public required string Token { get; init; }
    public required string FileName { get; init; }
    public required string ContentType { get; init; }
    public long? ExpectedLength { get; init; }
    public required string CachePath { get; init; }
    public required IDestinationUpload Destination { get; init; }

    private readonly object _gate = new();
    private TaskCompletionSource _signal = NewSignal();
    private long _bytesWritten;
    private int _state;
    private int _uploadStarted;
    private int _activeReaders;
    private long _lastActivityTicks = DateTimeOffset.UtcNow.UtcTicks;

    public long BytesWritten => Volatile.Read(ref _bytesWritten);
    public UploadState State => (UploadState)Volatile.Read(ref _state);
    public int ActiveReaders => Volatile.Read(ref _activeReaders);
    public DateTimeOffset LastActivityUtc => new(Volatile.Read(ref _lastActivityTicks), TimeSpan.Zero);
    public DateTimeOffset? FinishedUtc { get; private set; }
    public DestinationResult? Result { get; private set; }
    public Exception? Error { get; private set; }

    /// <summary>Returns true exactly once — the upload body may only be sent once.</summary>
    public bool TryMarkUploadStarted() => Interlocked.Exchange(ref _uploadStarted, 1) == 0;

    /// <summary>Bytes up to <paramref name="totalBytes"/> are flushed to the cache file and readable.</summary>
    public void Publish(long totalBytes)
    {
        lock (_gate)
        {
            Volatile.Write(ref _bytesWritten, totalBytes);
            Volatile.Write(ref _lastActivityTicks, DateTimeOffset.UtcNow.UtcTicks);
            Pulse();
        }
    }

    public void Complete(DestinationResult result)
    {
        lock (_gate)
        {
            if (_state != (int)UploadState.Uploading)
                return;
            Result = result;
            FinishedUtc = DateTimeOffset.UtcNow;
            Volatile.Write(ref _state, (int)UploadState.Completed);
            Pulse();
        }
    }

    public void Fail(Exception error)
    {
        lock (_gate)
        {
            if (_state != (int)UploadState.Uploading)
                return;
            Error = error;
            FinishedUtc = DateTimeOffset.UtcNow;
            Volatile.Write(ref _state, (int)UploadState.Failed);
            Pulse();
        }
    }

    /// <summary>
    /// Completes when more than <paramref name="currentOffset"/> bytes are available, or
    /// the session reaches a terminal state. Callers re-check state after waking.
    /// </summary>
    public Task WaitForDataAsync(long currentOffset, CancellationToken ct)
    {
        Task wait;
        lock (_gate)
        {
            if (_bytesWritten > currentOffset || _state != (int)UploadState.Uploading)
                return Task.CompletedTask;
            wait = _signal.Task;
        }

        return wait.WaitAsync(ct);
    }

    /// <summary>Open the cache file for tailing. Counts the reader so cleanup won't delete the file underneath it.</summary>
    public ReaderLease OpenReader()
    {
        Interlocked.Increment(ref _activeReaders);
        try
        {
            // share Write (the uploader has it open) and Delete (cleanup may unlink while we drain)
            var fs = new FileStream(CachePath, FileMode.Open, FileAccess.Read,
                                    FileShare.ReadWrite | FileShare.Delete, 64 * 1024,
                                    FileOptions.Asynchronous | FileOptions.SequentialScan);
            return new ReaderLease(this, fs);
        }
        catch
        {
            Interlocked.Decrement(ref _activeReaders);
            throw;
        }
    }

    internal void ReleaseReader() => Interlocked.Decrement(ref _activeReaders);

    private void Pulse()
    {
        var old = _signal;
        _signal = NewSignal();
        old.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed class ReaderLease(UploadSession session, FileStream stream) : IDisposable
{
    public FileStream Stream => stream;

    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        stream.Dispose();
        session.ReleaseReader();
    }
}

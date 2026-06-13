using Clowd.Server.Api;
using Clowd.Server.Destinations;

namespace Clowd.Server.Tests;

/// <summary>In-memory destination that mimics azure semantics: data is invisible until committed.</summary>
public sealed class FakeDestinationProvider : IDestinationProvider
{
    public string Name => "fake";

    public List<FakeDestinationUpload> Uploads { get; } = new();

    public Task<IDestinationUpload> BeginAsync(StartUploadRequest request, CancellationToken ct)
    {
        if (request.Credentials is null || !request.Credentials.ContainsKey("apiKey"))
            throw new DestinationConfigException("fake requires credentials.apiKey");

        var upload = new FakeDestinationUpload(Guid.NewGuid().ToString("N"));
        lock (Uploads)
            Uploads.Add(upload);
        return Task.FromResult<IDestinationUpload>(upload);
    }
}

public sealed class FakeDestinationUpload(string key) : IDestinationUpload
{
    private readonly MemoryStream _buffer = new();

    public bool Committed { get; private set; }
    public bool Aborted { get; private set; }
    public byte[] CommittedBytes { get; private set; } = Array.Empty<byte>();

    public string? FinalUrl => $"https://final.example.com/{key}";
    public UploadDeleteInfo? DeleteInfo => new() { Provider = "fake", DeleteUrl = $"https://final.example.com/delete/{key}" };

    public Stream WriteStream => _buffer;

    public Task<DestinationResult> CommitAsync(CancellationToken ct)
    {
        Committed = true;
        CommittedBytes = _buffer.ToArray();
        return Task.FromResult(new DestinationResult { FinalUrl = FinalUrl!, Delete = DeleteInfo });
    }

    public Task AbortAsync()
    {
        Aborted = true;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

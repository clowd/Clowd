using System.Collections.Concurrent;
using System.Security.Cryptography;
using Clowd.Server.Api;
using Clowd.Server.Destinations;
using Microsoft.Extensions.Options;

namespace Clowd.Server.Uploads;

/// <summary>Tracks in-progress uploads. The server is otherwise stateless — once an upload finishes, only the persisted redirect remains.</summary>
public sealed class UploadRegistry(IOptions<ServerOptions> options)
{
    private readonly ConcurrentDictionary<string, UploadSession> _sessions = new();
    private readonly ServerOptions _options = options.Value;

    public UploadSession Create(StartUploadRequest request, IDestinationUpload destination)
    {
        Directory.CreateDirectory(_options.CachePath);

        var id = RandomToken(12);
        var cachePath = Path.Combine(_options.CachePath, id + ".part");

        // create the cache file up front so downloads that race ahead of the first PUT byte can open it
        using (new FileStream(cachePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete))
        { }

        var session = new UploadSession
        {
            Id = id,
            Token = RandomToken(32),
            FileName = string.IsNullOrWhiteSpace(request.FileName) ? "file" : request.FileName,
            ContentType = string.IsNullOrWhiteSpace(request.ContentType) ? "application/octet-stream" : request.ContentType,
            ExpectedLength = request.ContentLength,
            CachePath = cachePath,
            Destination = destination,
        };

        if (!_sessions.TryAdd(id, session))
            throw new InvalidOperationException("duplicate upload id"); // 12 random bytes; effectively unreachable

        return session;
    }

    public bool TryGet(string id, out UploadSession session) => _sessions.TryGetValue(id, out session!);

    public bool Remove(string id) => _sessions.TryRemove(id, out _);

    public IReadOnlyCollection<UploadSession> Snapshot() => _sessions.Values.ToArray();

    private static string RandomToken(int byteCount)
    {
        Span<byte> bytes = stackalloc byte[byteCount];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}

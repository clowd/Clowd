using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Clowd.Server.Redirects;

public sealed class RedirectRecord
{
    public required string Url { get; set; }
    public string? FileName { get; set; }
    public string? ContentType { get; set; }
    public DateTimeOffset CompletedUtc { get; set; }
}

/// <summary>
/// The only state that outlives an upload: id -> final destination url, persisted as one
/// json file per id in the redirects mount so it survives restarts.
/// </summary>
public sealed partial class RedirectStore(IOptions<ServerOptions> options, ILogger<RedirectStore> logger)
{
    private readonly ConcurrentDictionary<string, RedirectRecord> _cache = new();
    private readonly string _dir = options.Value.RedirectsPath;

    public void Save(string id, RedirectRecord record)
    {
        if (!IsValidId(id))
            throw new ArgumentException($"invalid redirect id '{id}'", nameof(id));

        Directory.CreateDirectory(_dir);
        var path = PathFor(id);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(record));
        File.Move(tmp, path, overwrite: true);
        _cache[id] = record;
    }

    public bool TryGet(string id, out RedirectRecord record)
    {
        record = null!;
        if (!IsValidId(id))
            return false;

        if (_cache.TryGetValue(id, out record!))
            return true;

        var path = PathFor(id);
        try
        {
            if (!File.Exists(path))
                return false;
            var loaded = JsonSerializer.Deserialize<RedirectRecord>(File.ReadAllText(path));
            if (loaded?.Url is null)
                return false;
            record = _cache.GetOrAdd(id, loaded);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "failed to read redirect record {Id}", id);
            return false;
        }
    }

    private string PathFor(string id) => Path.Combine(_dir, id + ".json");

    // ids are url-safe base64 from the registry; the regex doubles as path-traversal protection
    private static bool IsValidId(string id) => !string.IsNullOrEmpty(id) && IdPattern().IsMatch(id);

    [GeneratedRegex("^[A-Za-z0-9_-]{8,64}$")]
    private static partial Regex IdPattern();
}

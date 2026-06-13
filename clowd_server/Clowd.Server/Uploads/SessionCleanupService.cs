using Microsoft.Extensions.Options;

namespace Clowd.Server.Uploads;

/// <summary>
/// Fails uploads that have gone idle, evicts finished sessions once their in-flight
/// downloads drain, and sweeps orphaned cache files left behind by crashes.
/// </summary>
public sealed class SessionCleanupService(UploadRegistry registry, IOptions<ServerOptions> options, ILogger<SessionCleanupService> logger)
    : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan OrphanAge = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await SweepAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "cleanup sweep failed");
            }
        }
    }

    private async Task SweepAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var opts = options.Value;

        foreach (var session in registry.Snapshot())
        {
            if (session.State == UploadState.Uploading && now - session.LastActivityUtc > opts.UploadIdleTimeout)
            {
                logger.LogWarning("upload {Id} idle for {Idle}, abandoning", session.Id, now - session.LastActivityUtc);
                session.Fail(new TimeoutException("upload abandoned: no data received within the idle timeout"));
                try
                {
                    await session.Destination.AbortAsync();
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "failed to abort destination for idle upload {Id}", session.Id);
                }
            }

            if (session.State != UploadState.Uploading
                && session.ActiveReaders == 0
                && session.FinishedUtc is { } finished
                && now - finished > opts.FinishedLinger)
            {
                registry.Remove(session.Id);
                TryDelete(session.CachePath);
            }
        }

        SweepOrphans(opts.CachePath, now);
    }

    private void SweepOrphans(string cacheDir, DateTimeOffset now)
    {
        if (!Directory.Exists(cacheDir))
            return;

        foreach (var file in Directory.EnumerateFiles(cacheDir, "*.part"))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            if (registry.TryGet(id, out _))
                continue;
            try
            {
                if (now - File.GetLastWriteTimeUtc(file) > OrphanAge)
                    TryDelete(file);
            }
            catch (IOException)
            {
                // raced with a writer/deleter; next sweep will get it
            }
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException ex)
        {
            logger.LogDebug(ex, "could not delete cache file {Path} yet", path);
        }
    }
}

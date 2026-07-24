using System;
using System.Reflection;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace Clowd.UI
{
    /// <summary>
    /// Velopack automatic updates from GitHub Releases. Packages are published per-platform
    /// channels (win-x64, osx-arm64, ...) with a "-pre" suffix for the pre-release channels;
    /// the installed channel is baked into the package manifest at pack time, so a "-pre"
    /// install keeps following pre-releases automatically.
    /// </summary>
    public sealed class UpdateService
    {
        private const string RepoUrl = "https://github.com/clowd/Clowd";

        public static UpdateService Default { get; } = new UpdateService();

        private readonly UpdateManager _manager;

        private UpdateService()
        {
            try
            {
                // pre-release channels live on GitHub pre-releases, which GithubSource skips
                // unless asked; stable channels are unaffected by prerelease: true because
                // pre-releases never contain a stable releases.{channel}.json feed.
                var channel = Velopack.Locators.VelopackLocator.Current?.Channel;
                var prerelease = channel?.EndsWith("-pre", StringComparison.OrdinalIgnoreCase) == true;
                _manager = new UpdateManager(new GithubSource(RepoUrl, null, prerelease));
            }
            catch
            {
                // not a velopack install (local dev build); IsSupported stays false.
            }
        }

        /// <summary>False for non-installed (dev / loose) builds, where update checks are impossible.</summary>
        public bool IsSupported => _manager?.IsInstalled == true;

        public string CurrentVersion =>
            _manager?.CurrentVersion?.ToString()
            ?? Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "unknown";

        /// <summary>Non-null when an update has been downloaded and only needs a restart.</summary>
        public VelopackAsset UpdatePendingRestart => _manager?.UpdatePendingRestart;

        public Task<UpdateInfo> CheckForUpdatesAsync() => _manager.CheckForUpdatesAsync();

        public Task DownloadUpdatesAsync(UpdateInfo info, Action<int> progress = null) =>
            _manager.DownloadUpdatesAsync(info, progress);

        public void ApplyUpdatesAndRestart(UpdateInfo info) => _manager.ApplyUpdatesAndRestart(info);

        public void ApplyUpdatesAndRestart(VelopackAsset asset) => _manager.ApplyUpdatesAndRestart(asset);
    }
}

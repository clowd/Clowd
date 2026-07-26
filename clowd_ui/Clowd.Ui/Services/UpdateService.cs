using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Clowd.Config;
using Clowd.Util;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace Clowd.UI
{
    public enum UpdateState
    {
        /// <summary>Nothing has been checked yet, or automatic checks are turned off.</summary>
        Idle,

        /// <summary>Not a Velopack install (dev / loose build) — nothing here can work.</summary>
        Unsupported,

        Checking,
        UpToDate,
        UpdateAvailable,
        Downloading,

        /// <summary>An update is staged on disk and only needs the process to restart.</summary>
        ReadyToRestart,

        Failed,
    }

    /// <summary>
    /// Velopack automatic updates from GitHub Releases. Packages are published per-platform channels
    /// (win-x64, osx-arm64, ...) with a "-pre" suffix for the pre-release channels; the installed
    /// channel is baked into the package manifest at pack time, so an install keeps following the
    /// channel it came from unless the user switches (<see cref="SetPrereleaseChannelAsync"/>), which
    /// is persisted as <see cref="Clowd.Config.SettingsGeneral.UpdateChannel"/> and applied as an
    /// explicit channel override on every subsequent check.
    ///
    /// <see cref="Start"/> installs a one-minute heartbeat that owns the whole background policy:
    /// checking on the configured interval, downloading when the user has opted into automatic
    /// updates, and applying the staged update only once <see cref="IdleMonitor"/> agrees
    /// Clowd is idle. The settings page drives the same methods for the interactive path, and renders
    /// <see cref="State"/> / <see cref="StatusMessage"/> for both.
    /// </summary>
    public sealed class UpdateService
    {
        private const string RepoUrl = "https://github.com/clowd/Clowd";
        private const string PrereleaseSuffix = "-pre";

        /// <summary>How long to wait before retrying after a failed check — the configured interval
        /// can be many hours, which is far too long to sit on a transient network error.</summary>
        private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(15);

        private static readonly TimeSpan Heartbeat = TimeSpan.FromMinutes(1);

        public static UpdateService Default { get; } = new UpdateService();

        /// <summary>Raised (on an arbitrary thread) whenever <see cref="State"/>,
        /// <see cref="StatusMessage"/> or <see cref="DownloadProgress"/> changes.</summary>
        public event EventHandler StateChanged;

        private readonly object _lock = new object();
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        private UpdateManager _manager;
        private bool _managerCreated;
        private IDisposable _heartbeatTimer;
        private UpdateInfo _available;
        private VelopackAsset _staged;
        private DateTime _nextCheckUtc = DateTime.MinValue;
        private DateTime _lastCheckUtc = DateTime.MinValue;
        private bool _restarting;

        private UpdateService()
        {
        }

        public UpdateState State { get; private set; }

        /// <summary>Human-readable one-liner describing <see cref="State"/>, shown under the update
        /// button. Null before anything has happened.</summary>
        public string StatusMessage { get; private set; }

        /// <summary>0-100 while <see cref="State"/> is <see cref="UpdateState.Downloading"/>.</summary>
        public int DownloadProgress { get; private set; }

        /// <summary>False for non-installed (dev / loose) builds, where update checks are impossible.</summary>
        public bool IsSupported => Manager?.IsInstalled == true;

        public string CurrentVersion =>
            Manager?.CurrentVersion?.ToString()
            ?? Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "unknown";

        /// <summary>Non-null when an update has been downloaded and only needs a restart.</summary>
        /// <remarks>Velopack's own <c>UpdatePendingRestart</c> only counts a staged package whose
        /// version is strictly greater than the installed one, so it never sees a channel switch at
        /// the same version (or a downgrade back to stable). Whatever this session staged wins;
        /// Velopack's answer covers a package staged before the last restart.</remarks>
        public VelopackAsset UpdatePendingRestart => _staged ?? Manager?.UpdatePendingRestart;

        /// <summary>The channel this build was installed from; null in a dev build.</summary>
        public string InstalledChannel => TryGetInstalledChannel();

        /// <summary>The channel updates are actually fetched from — the user's override if they have
        /// switched, otherwise the installed channel.</summary>
        public string Channel
        {
            get
            {
                var over = SettingsRoot.Current?.General?.UpdateChannel;
                return String.IsNullOrWhiteSpace(over) ? InstalledChannel : over;
            }
        }

        public bool IsPrereleaseChannel => IsPrerelease(Channel);

        /// <summary>Switching channels is only meaningful when we know which channel we are on, i.e.
        /// on a real install.</summary>
        public bool CanSwitchChannel => IsSupported && !String.IsNullOrWhiteSpace(InstalledChannel);

        private TimeSpan CheckInterval =>
            TimeSpan.FromMinutes((int)(SettingsRoot.Current?.General?.UpdateCheckInterval ?? UpdateInterval.ThreeHourly));

        private UpdateManager Manager
        {
            get
            {
                lock (_lock)
                {
                    if (!_managerCreated)
                    {
                        _managerCreated = true;
                        _manager = TryCreateManager();
                    }

                    return _manager;
                }
            }
        }

        // ---- lifetime ----

        /// <summary>
        /// Starts the background update policy. Called once from App.Startup, after settings are
        /// loaded — the manager reads the channel override out of settings, so it cannot be built
        /// any earlier.
        /// </summary>
        public void Start()
        {
            if (!IsSupported)
            {
                SetState(UpdateState.Unsupported, "Automatic updates are not available in this build.");
                return;
            }

            if (UpdatePendingRestart != null)
                SetState(UpdateState.ReadyToRestart, "An update has been downloaded and will be applied when Clowd restarts.");
            else if (SettingsRoot.Current?.General?.AutoDownloadUpdates != true)
                SetState(UpdateState.Idle, "Automatic update checks are turned off.");

            if (SettingsRoot.Current?.General is { } general)
                general.PropertyChanged += OnSettingsChanged;

            _heartbeatTimer = DisposableTimer.Start(Heartbeat, () => _ = TickAsync());
            _ = TickAsync();
        }

        public void Stop()
        {
            _heartbeatTimer?.Dispose();
            _heartbeatTimer = null;

            if (SettingsRoot.Current?.General is { } general)
                general.PropertyChanged -= OnSettingsChanged;
        }

        private void OnSettingsChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(SettingsGeneral.AutoDownloadUpdates):
                    // turning checks back on should feel immediate rather than waiting out an
                    // interval that elapsed while they were off.
                    _nextCheckUtc = DateTime.MinValue;
                    _ = TickAsync();
                    break;

                case nameof(SettingsGeneral.UpdateCheckInterval):
                    _nextCheckUtc = _lastCheckUtc == DateTime.MinValue ? DateTime.MinValue : _lastCheckUtc + CheckInterval;
                    _ = TickAsync();
                    break;

                case nameof(SettingsGeneral.AutoApplyUpdates):
                    _ = TickAsync();
                    break;
            }
        }

        /// <summary>
        /// The whole background policy, re-evaluated once a minute (and whenever a relevant setting
        /// changes). The two settings are independent: downloading is what happens on the interval,
        /// and restarting to apply is a separate decision — an update downloaded by hand from the
        /// settings page is still applied automatically if the user has asked for that.
        /// Everything here is best-effort and silent; the interactive paths surface errors.
        /// </summary>
        private async Task TickAsync()
        {
            if (_restarting || !IsSupported)
                return;

            var general = SettingsRoot.Current?.General;
            if (general == null)
                return;

            try
            {
                if (general.AutoDownloadUpdates && UpdatePendingRestart == null)
                {
                    if (DateTime.UtcNow >= _nextCheckUtc)
                        await CheckForUpdatesAsync(userInitiated: false);

                    if (UpdatePendingRestart == null && _available != null && State != UpdateState.Downloading)
                        await DownloadUpdatesAsync(_available);
                }

                if (general.AutoApplyUpdates && UpdatePendingRestart is { } pending)
                    ApplyWhenIdle(pending);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("UpdateService: background update tick failed: " + ex);
                SentryConfig.CaptureHandled(ex, "update.tick");
            }
        }

        // ---- checking / downloading / applying ----

        /// <summary>Checks the release feed. Returns the available update, or null when up to date or
        /// the check failed (<paramref name="userInitiated"/> only affects the wording of
        /// <see cref="StatusMessage"/>).</summary>
        public async Task<UpdateInfo> CheckForUpdatesAsync(bool userInitiated = true)
        {
            if (Manager == null)
            {
                SetState(UpdateState.Unsupported, "Automatic updates are not available in this build.");
                return null;
            }

            // a check or download is already in flight; don't queue a second one behind it.
            if (!await _gate.WaitAsync(0))
                return _available;

            try
            {
                SetState(UpdateState.Checking, "Checking for updates…");
                var info = await Manager.CheckForUpdatesAsync();

                _available = info;
                _lastCheckUtc = DateTime.UtcNow;
                _nextCheckUtc = _lastCheckUtc + CheckInterval;

                if (info == null)
                    SetState(UpdateState.UpToDate, "Clowd is up to date.");
                else
                    SetState(UpdateState.UpdateAvailable, $"Version {info.TargetFullRelease.Version} is available.");

                return info;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("UpdateService: update check failed: " + ex);
                SentryConfig.CaptureHandled(ex, "update.check");
                _lastCheckUtc = DateTime.UtcNow;
                _nextCheckUtc = _lastCheckUtc + (RetryInterval < CheckInterval ? RetryInterval : CheckInterval);
                SetState(UpdateState.Failed, userInitiated
                    ? "Update check failed: " + ex.Message
                    : "The last update check failed. Clowd will try again shortly.");
                return null;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>The update found by the last successful check, or null.</summary>
        public UpdateInfo AvailableUpdate => _available;

        /// <summary>Downloads whatever the last check found; a no-op if there is nothing to download.</summary>
        public Task<bool> DownloadAvailableUpdateAsync() => DownloadUpdatesAsync(_available);

        /// <summary>Downloads (and stages) an update. Afterwards <see cref="UpdatePendingRestart"/> is
        /// non-null and only a restart is required.</summary>
        public async Task<bool> DownloadUpdatesAsync(UpdateInfo info)
        {
            if (Manager == null || info == null)
                return false;

            if (!await _gate.WaitAsync(0))
                return false;

            try
            {
                SetState(UpdateState.Downloading, $"Downloading version {info.TargetFullRelease.Version}…");
                await Manager.DownloadUpdatesAsync(info, p => SetProgress(p));
                _staged = info.TargetFullRelease;
                SetState(UpdateState.ReadyToRestart, "Update downloaded. Restart Clowd to finish installing.");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("UpdateService: update download failed: " + ex);
                SentryConfig.CaptureHandled(ex, "update.download");
                SetState(UpdateState.Failed, "Download failed: " + ex.Message);
                return false;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Applies the staged update at the next quiet moment, or explains what is currently
        /// keeping it from being applied.</summary>
        private void ApplyWhenIdle(VelopackAsset pending)
        {
            if (IdleMonitor.IsGoodTimeToRestart(out var reason))
            {
                ApplyUpdatesAndRestart(pending, silent: true);
                return;
            }

            SetState(UpdateState.ReadyToRestart,
                "An update is ready — Clowd will restart to apply it once it is idle (waiting: " + reason + ").");
        }

        /// <summary>
        /// Hands the staged update to the Velopack updater and exits. <c>WaitExitThenApplyUpdates</c>
        /// (rather than <c>ApplyUpdatesAndRestart</c>) so the app can run its normal shutdown first:
        /// editors persist their sessions on close, and a background update that silently discarded
        /// them would be worse than no update at all. The updater waits up to 60s for the exit.
        /// </summary>
        public void ApplyUpdatesAndRestart(VelopackAsset asset = null, bool silent = false)
        {
            var manager = Manager;
            if (manager == null || _restarting)
                return;

            _restarting = true;

            try
            {
                // a silent restart comes back up in the tray: the user never asked for a window, and
                // having settings appear unprompted is the one thing that would make a background
                // update conspicuous. An explicit "Restart to Update" click restarts normally.
                var restartArgs = silent ? new[] { Program.SilentUpdateRestartArg } : null;
                manager.WaitExitThenApplyUpdates(asset ?? UpdatePendingRestart, silent, true, restartArgs);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("UpdateService: failed to launch the updater: " + ex);
                SentryConfig.CaptureHandled(ex, "update.launch");
                _restarting = false;
                SetState(UpdateState.Failed, "Could not start the updater: " + ex.Message);
                return;
            }

            Dispatcher.UIThread.Post(() => App.Current?.ExitApp());
        }

        // ---- channel ----

        /// <summary>
        /// Switches between the stable and pre-release channel for this platform (win-x64 &lt;-&gt;
        /// win-x64-pre) and immediately re-checks. The switch is persisted and applied as an explicit
        /// channel override until an update from the new channel is installed, at which point the
        /// installed channel matches and the override becomes a no-op.
        /// </summary>
        public async Task SetPrereleaseChannelAsync(bool usePrerelease)
        {
            var installed = InstalledChannel;
            if (String.IsNullOrWhiteSpace(installed) || SettingsRoot.Current?.General is not { } general)
                return;

            var stable = IsPrerelease(installed) ? installed.Substring(0, installed.Length - PrereleaseSuffix.Length) : installed;
            var target = usePrerelease ? stable + PrereleaseSuffix : stable;

            if (String.Equals(target, Channel, StringComparison.OrdinalIgnoreCase))
                return;

            general.UpdateChannel = target;

            lock (_lock)
            {
                _managerCreated = false;
                _manager = null;
            }

            _available = null;
            _staged = null;
            _nextCheckUtc = DateTime.MinValue;

            await CheckForUpdatesAsync(userInitiated: true);
        }

        private UpdateManager TryCreateManager()
        {
            try
            {
                var installed = TryGetInstalledChannel();
                var over = SettingsRoot.Current?.General?.UpdateChannel;
                var effective = String.IsNullOrWhiteSpace(over) ? installed : over;

                // "explicit" only when it actually differs — leaving ExplicitChannel null keeps
                // Velopack on the manifest channel, which is what an un-switched install wants.
                var explicitChannel = String.Equals(effective, installed, StringComparison.OrdinalIgnoreCase) ? null : effective;

                var options = new UpdateOptions
                {
                    ExplicitChannel = explicitChannel,

                    // switching pre-release -> stable usually means the newest release on the target
                    // channel is *older* than what is installed; without this Velopack reports no
                    // update and the user is stuck on the pre-release forever.
                    AllowVersionDowngrade = explicitChannel != null,
                };

                // pre-release channels live on GitHub pre-releases, which GithubSource skips unless
                // asked; stable channels are unaffected by prerelease: true because pre-releases
                // never contain a stable releases.{channel}.json feed.
                return new UpdateManager(new GithubSource(RepoUrl, null, IsPrerelease(effective)), options);
            }
            catch (Exception ex)
            {
                // not a velopack install (local dev build); IsSupported stays false.
                Debug.WriteLine("UpdateService: no update manager available: " + ex);
                SentryConfig.CaptureHandled(ex, "update.locate-manager");
                return null;
            }
        }

        private static string TryGetInstalledChannel()
        {
            try
            {
                return VelopackLocator.Current?.Channel;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsPrerelease(string channel) =>
            channel?.EndsWith(PrereleaseSuffix, StringComparison.OrdinalIgnoreCase) == true;

        // ---- state ----

        private void SetState(UpdateState state, string message)
        {
            // the heartbeat re-evaluates the same "waiting for the computer to go idle" state every
            // minute; only tell anyone when something actually changed.
            if (State == state && StatusMessage == message)
                return;

            State = state;
            StatusMessage = message;
            if (state != UpdateState.Downloading)
                DownloadProgress = 0;

            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        private void SetProgress(int progress)
        {
            DownloadProgress = progress;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}

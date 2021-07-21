using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Clowd
{
    public class Package
    {
        public string Version { get; }

        public Package(string version)
        {
            Version = version;
        }
    }
    public class LocalPackage : Package
    {
        public string Path { get; }

        public LocalPackage(string ver, string path) : base(ver)
        {
            Path = path;
        }
    }

    public class RemotePackage : Package
    {
        public bool IsPrerelease { get; }

        public string DownloadUrl { get; }

        public RemotePackage(string ver, bool pre, string downloadUrl) : base(ver)
        {
            IsPrerelease = pre;
            DownloadUrl = downloadUrl;
        }
    }

    //public class ModuleSettings : INotifyPropertyChanged
    //{
    //    private bool isEnabled;

    //    public bool IsEnabled
    //    {
    //        get => isEnabled;
    //        set
    //        {
    //            if (value == isEnabled)
    //            {
    //                return;
    //            }

    //            isEnabled = value;
    //            OnPropertyChanged();
    //        }
    //    }

    //    public event PropertyChangedEventHandler PropertyChanged;

    //    protected virtual void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string propertyName = null)
    //    {
    //        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    //    }
    //}

    public interface IModule : IDisposable
    {
    }

    public interface IModuleInfo<out T> : INotifyPropertyChanged where T : IModule
    {
        string Name { get; }
        string Description { get; }
        Stream Icon { get; }
        T GetNewInstance();
        LocalPackage InstalledVersion { get; }
        RemotePackage UpdateAvailable { get; }
        Task CheckForUpdates(bool includePrereleases);
        Task Install(RemotePackage pkg);
        Task Uninstall();
    }

    public abstract class ModuleIntegratedBase<T> : IModuleInfo<T> where T : IModule
    {
        public abstract string Name { get; }
        public abstract string Description { get; }
        public abstract Stream Icon { get; }
        public LocalPackage InstalledVersion => new LocalPackage("integrated", "");
        public RemotePackage UpdateAvailable => null;

        public event PropertyChangedEventHandler PropertyChanged;

        public abstract T GetNewInstance();

        public virtual Task CheckForUpdates(bool includePrereleases) => Task.CompletedTask;
        public virtual Task Install(RemotePackage pkg) => throw new NotSupportedException();
        public virtual Task Uninstall() => throw new NotSupportedException();

        protected virtual void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public abstract class ModuleBase<T> : IModuleInfo<T> where T : IModule
    {
        public abstract string Name { get; }
        public abstract string Description { get; }
        public virtual Stream Icon => EmbeddedResource.GetStream("Clowd", "default-provider-icon.png");

        private LocalPackage installedVersion;
        private RemotePackage updateAvailable;

        public event PropertyChangedEventHandler PropertyChanged;

        public LocalPackage InstalledVersion
        {
            get => installedVersion;
            protected set
            {
                if (value == installedVersion)
                {
                    return;
                }

                installedVersion = value;
                OnPropertyChanged();
            }
        }

        public RemotePackage UpdateAvailable
        {
            get => updateAvailable;
            protected set
            {
                if (value == updateAvailable)
                {
                    return;
                }

                updateAvailable = value;
                OnPropertyChanged();
            }
        }

        public abstract Task CheckForUpdates(bool includePrereleases);
        public abstract T GetNewInstance();
        public abstract Task Install(RemotePackage pkg);
        public abstract Task Uninstall();

        protected virtual void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public abstract class GithubReleaseModule<T> : ModuleBase<T> where T : IModule
    {
        private static SemaphoreSlim _lock = new SemaphoreSlim(1, 1);

        public string RepoName { get; }
        public string AssetName { get; }
        public IScopedLog Log { get; }

        public GithubReleaseModule(string repoName, string assetName, IScopedLog log)
        {
            RepoName = repoName;
            AssetName = assetName;
            Log = log;
            CheckInstalledVersion();
        }

        public override async Task CheckForUpdates(bool includePrereleases)
        {
            await _lock.WaitAsync();
            try
            {
                CheckInstalledVersion();
                var releases = await GithubApi.GetReleasesAsync(RepoName);

                var query = releases
                    .OrderByDescending(r => r.TagName)
                    .SelectMany(r => r.Assets, (r, a) => new { Version = r.TagName, r.Prerelease, Asset = a })
                    .Where(u => u.Asset.Name.StartsWith(AssetName) && u.Asset.Name.EndsWith(".zip"))
                    .Select(r => new RemotePackage(r.Version, r.Prerelease, r.Asset.BrowserDownloadUrl))
                    .ToArray();

                RemotePackage release = null;
                if (!includePrereleases) release = query.FirstOrDefault(r => r.IsPrerelease == false);
                if (includePrereleases || release == null) release = query.FirstOrDefault();
                UpdateAvailable = InstalledVersion?.Version != release.Version ? release : null;
            }
            finally
            {
                _lock.Release();
            }
        }

        public virtual void CheckInstalledVersion()
        {
            InstalledVersion = GetInstalledVersions().OrderByDescending(d => d.Version).FirstOrDefault();
        }

        public IEnumerable<LocalPackage> GetInstalledVersions()
        {
            var pluginsDir = PathConstants.Plugins;
            foreach (var dir in Directory.EnumerateDirectories(pluginsDir))
            {
                var cut = dir.Substring(pluginsDir.Length).Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (cut.StartsWith(AssetName))
                {
                    var cut2 = cut.Substring(AssetName.Length).Trim('-');
                    yield return new LocalPackage(cut2, dir);
                }
            }
        }

        public virtual async Task DeleteOldVersions()
        {
            foreach (var s in GetInstalledVersions().OrderByDescending(d => d.Version).Skip(1))
            {
                await Task.Factory.StartNew(path => Directory.Delete((string)path, true), s.Path);
            }
        }

        public override async Task Install(RemotePackage pkg)
        {
            await _lock.WaitAsync();
            try
            {
                var pluginsDir = PathConstants.Plugins;
                var dlpath = PathConstants.GetDatedFilePath(AssetName, "zip", pluginsDir);
                var extractPath = Path.Combine(pluginsDir, $"{AssetName}-{pkg.Version}");

                try
                {
                    await GithubApi.DownloadBrowserAsset(pkg.DownloadUrl, dlpath);
                    await Task.Run(() => ZipFile.ExtractToDirectory(dlpath, extractPath));
                    InstalledVersion = new LocalPackage(pkg.Version, extractPath);
                    await DeleteOldVersions();
                }
                finally
                {
                    if (File.Exists(dlpath))
                        File.Delete(dlpath);
                }
            }
            finally
            {
                _lock.Release();
            }
        }

        public override async Task Uninstall()
        {
            await _lock.WaitAsync();
            try
            {
                foreach (var s in GetInstalledVersions().OrderByDescending(d => d.Version))
                {
                    await Task.Factory.StartNew(path => Directory.Delete((string)path, true), s.Path);
                }
                CheckInstalledVersion();
            }
            finally
            {
                _lock.Release();
            }
        }
    }
}

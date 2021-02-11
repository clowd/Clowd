﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Clowd
{
    public class Version
    {
        public string Ver { get; set; }
        public bool IsPrerelease { get; set; }
    }

    public interface IModule
    {
    }

    public interface IModuleInfo : INotifyPropertyChanged
    {
        string Name { get; }
        string Description { get; }
        Stream Icon { get; }
        string InstalledVersion { get; }
        string UpdateAvailable { get; }
        bool Prerelease { get; }
    }

    public interface IModuleInfo<T> : IModuleInfo where T : IModule
    {
        Task CheckForUpdates(bool includePrereleases);
        Task Install(string version);
        Task Uninstall();
        T GetNewInstance();
    }

    public abstract class GithubReleaseModule<T> : IModuleInfo<T> where T : IModule
    {
        private string installedVersion;
        private string updateAvailable;
        private bool prerelease;

        public string InstalledVersion
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

        public string UpdateAvailable
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

        public bool Prerelease
        {
            get => prerelease;
            protected set
            {
                if (value == prerelease)
                {
                    return;
                }

                prerelease = value;
                OnPropertyChanged();
            }
        }

        public abstract string Name { get; }
        public abstract string Description { get; }
        public abstract Stream Icon { get; }
        public string RepoName { get; }

        public GithubReleaseModule(string repoName)
        {
            RepoName = repoName;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        public abstract T GetNewInstance();

        public virtual async Task CheckForUpdates(bool includePrereleases)
        {
            var releases = await GithubApi.GetReleasesAsync(RepoName);


            throw new NotImplementedException();
        }

        public virtual Task Install(string version)
        {
            throw new NotImplementedException();
        }

        public virtual Task Uninstall()
        {
            throw new NotImplementedException();
        }

        protected virtual void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

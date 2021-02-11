using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Clowd.Video
{
    public abstract class GithubReleaseModule<T> : IModuleInfo<T>
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

        public event PropertyChangedEventHandler PropertyChanged;
        public abstract T GetNewInstance();

        public virtual Task CheckForUpdates(bool includePrereleases)
        {
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

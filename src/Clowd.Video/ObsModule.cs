using System;
using System.IO;

namespace Clowd.Video
{
    public class ObsModule : GithubReleaseModule<ObsCapturer>
    {
        public ObsModule(IScopedLog log) : base("clowd/obs-express", "obs-express", log)
        {
        }

        public override string Name => "obs-express";

        public override string Description => "Capture your screen using libobs. This is the fastest and most reliable method.";

        public override ObsCapturer GetNewInstance()
        {
            if (InstalledVersion == null)
                throw new InvalidOperationException("Can not get instance if module is not installed.");

            if (!Directory.Exists(InstalledVersion.Path))
                throw new InvalidOperationException("Module installation has been corrupted, recommend re-installing.");

            return new ObsCapturer(Log, InstalledVersion.Path);
        }
    }
}

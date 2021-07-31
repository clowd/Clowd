namespace Clowd.Setup.Features
{
    public class AutoStart : IFeature
    {
        public bool CheckInstalled(string assetPath)
        {
            bool found = false;
            foreach (var root in RegistryEx.OpenKeysFromRootPath(Constants.RunRegistryPath, RegistryQuery.CurrentUser))
            {
                var applocation = root.GetValue(Constants.ClowdAppName) as string;
                if (applocation != null && SystemEx.AreFileSystemObjectsEqual(applocation, assetPath))
                    found = true;

                root.Dispose();
            }
            return found;
        }

        public void Install(string assetPath)
        {
            Uninstall(assetPath);
            using (var root = RegistryEx.CreateKeyFromRootPath(Constants.RunRegistryPath, InstallMode.CurrentUser))
            {
                root.SetValue(Constants.ClowdAppName, assetPath);
            }
        }

        public bool NeedsPrivileges()
        {
            return false;
        }

        public void Uninstall(string assetPath)
        {
            foreach (var root in RegistryEx.OpenKeysFromRootPath(Constants.RunRegistryPath, RegistryQuery.CurrentUser))
            {
                try
                {
                    root.DeleteValue(Constants.ClowdAppName);
                }
                catch { } // throws if value does not exist
                root.Dispose();
            }
        }
    }
}

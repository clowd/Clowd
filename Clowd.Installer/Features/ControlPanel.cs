using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Clowd.Installer.Features
{
    public class ControlPanel : IFeature
    {
        public bool CheckInstalled(string assetPath)
        {
            var info = ControlPanelInfo.GetInfo(Constants.AppName, RegistryQuery.CurrentUser);
            return info != null;
        }

        public void Install(string assetPath)
        {
            var info = GetInfo(assetPath);
            ControlPanelInfo.Install(Constants.AppName, info, InstallMode.CurrentUser);
        }

        public bool NeedsPrivileges()
        {
            return false;
        }

        public void Uninstall(string assetPath)
        {
            ControlPanelInfo.Uninstall(Constants.AppName, RegistryQuery.CurrentUser);
        }

        private ControlPanelInfo GetInfo(string assetPath)
        {
            return new ControlPanelInfo()
            {
                DisplayName = Constants.AppName,
                DisplayIconPath = assetPath,
                InstallDirectory = Path.GetDirectoryName(assetPath),
                Publisher = Constants.PublishingCompany,
                UninstallString = assetPath + " /uninstall",
            };
        }
    }
}

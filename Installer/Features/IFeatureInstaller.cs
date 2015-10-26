using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Clowd.Installer.Features
{
    public interface IFeatureInstaller
    {
        bool CheckInstalled(string assetPath, RegistryQuery context);
        void Install(string assetPath, InstallMode context);
        void Uninstall(string assetPath, RegistryQuery context);
    }
}

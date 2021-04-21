using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Clowd.Installer.Features
{
    public interface IFeature
    {
        bool NeedsPrivileges();
        bool CheckInstalled(string assetPath);
        void Install(string assetPath);
        void Uninstall(string assetPath);
    }
}

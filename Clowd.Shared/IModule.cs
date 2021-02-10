using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Clowd
{
    public interface IModule
    {
        string Name { get; }
        string Description { get; }
        Stream Icon { get; }
        ModuleStatus GetStatus();
        string GetInstalledVersion();
        string GetLatestAvailableVersion(bool includePrerelease);
        void Install(string version);
        void Uninstall();
    }

    public enum ModuleStatus
    {
        NotInstalled = 0,
        Installing = 1,
        UpdateAvailable = 2,
        Updating = 3,
        Uninstalling = 4,
        Ready = 5,
    }

    //public class Module : IModule
    //{

    //}
}

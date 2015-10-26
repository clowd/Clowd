using PropertyChanged;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Clowd.Installer
{
    [ImplementPropertyChanged]
    public class InstallOptions
    {
        public static InstallMode InstallLocation { get; set; } = InstallMode.System;
        public static bool DesktopShortcut { get; set; } = true;
        public static bool ContextMenu { get; set; } = true;
        public static bool AutoStart { get; set; } = true;
    }
}

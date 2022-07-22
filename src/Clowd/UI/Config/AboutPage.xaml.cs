using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using Clowd.UI.Helpers;

namespace Clowd.UI.Pages
{
    public class OpenSourceLibrary
    {
        public string LibraryName { get; set; }
        public string LibraryLicenseUrl { get; set; }

        public OpenSourceLibrary()
        {
        }
    }

    public class AboutPageViewModel : SimpleNotifyObject
    {
        public string ClowdVersion { get; set; }
        public List<OpenSourceLibrary> Dependencies { get; set; } = new List<OpenSourceLibrary>();
    }

    public partial class AboutPage : Page
    {
        // update the list below with the following command -
        // and double check there are no missing/deprecated license links
        // Get-Package | Select-Object Id,LicenseUrl
        private const string _nugetPackages = @"
B2Net                               https://licenses.nuget.org/MIT                                        
Clowd.Squirrel                      https://licenses.nuget.org/MIT                                        
DotNetZip                           https://raw.githubusercontent.com/haf/DotNetZip.Semverd/master/LICENSE
Hardcodet.NotifyIcon.Wpf            https://github.com/hardcodet/wpf-notifyicon/blob/develop/LICENSE                                  
Microsoft.Win32.Registry            https://licenses.nuget.org/MIT                                        
NAudio.Wasapi                       https://licenses.nuget.org/MIT                                        
Nerdbank.GitVersioning              https://licenses.nuget.org/MIT                                        
Newtonsoft.Json                     https://licenses.nuget.org/MIT                                        
NLog                                https://licenses.nuget.org/BSD-3-Clause  
RT.Serialization                    https://licenses.nuget.org/MIT                                                                              
RT.Serialization.Binary             https://licenses.nuget.org/MIT                                                                                
RT.Serialization.Xml                https://licenses.nuget.org/MIT                                                                                        
RT.Util.Core                        https://licenses.nuget.org/MIT
Sentry                              https://licenses.nuget.org/MIT                                        
Sentry.NLog                         https://licenses.nuget.org/MIT                                        
ThomasLevesque.WeakEvent            https://licenses.nuget.org/Apache-2.0                                 
Vanara.PInvoke.DwmApi               https://licenses.nuget.org/MIT                                        
Vanara.PInvoke.SHCore               https://licenses.nuget.org/MIT                                        
Vanara.PInvoke.Shell32              https://licenses.nuget.org/MIT                                        
Vanara.PInvoke.User32               https://licenses.nuget.org/MIT                                        
WindowsAzure.Storage                https://github.com/Azure/azure-storage-net/blob/master/LICENSE.txt    
WPF-UI                              https://licenses.nuget.org/MIT                                        
YamlDotNet                          https://github.com/aaubry/YamlDotNet/blob/master/LICENSE.txt                                  
";

        public AboutPage()
        {
            InitializeComponent();

            var model = new AboutPageViewModel();
            model.ClowdVersion = SquirrelUtil.CurrentVersion;

            var deps = new List<OpenSourceLibrary>();

            foreach (var pkg in _nugetPackages.Split("\n"))
            {
                var space = pkg.IndexOf(' ');
                if (space < 1) continue;
                if (String.IsNullOrWhiteSpace(pkg)) continue;
                var name = pkg.Substring(0, space).Trim();
                var url = pkg.Substring(space).Trim();
                deps.Add(new OpenSourceLibrary { LibraryName = name, LibraryLicenseUrl = url });
            }

            // sub-modules, etc
            deps.Add(new OpenSourceLibrary { LibraryName = "obs-studio-node", LibraryLicenseUrl = "https://github.com/stream-labs/obs-studio-node/blob/staging/LICENSE" });
            deps.Add(new OpenSourceLibrary { LibraryName = "obs-studio", LibraryLicenseUrl = "https://github.com/obsproject/obs-studio/blob/master/COPYING" });
            deps.Add(new OpenSourceLibrary { LibraryName = "obs-express", LibraryLicenseUrl = "https://github.com/clowd/obs-express/blob/master/LICENSE" });
            deps.Add(new OpenSourceLibrary { LibraryName = "ffmpeg", LibraryLicenseUrl = "https://www.ffmpeg.org/legal.html" });

            model.Dependencies = deps
                .GroupBy(d => d.LibraryName) // remove duplicates
                .Select(g => g.FirstOrDefault())
                .OrderBy(d => d.LibraryName)
                .ToList();

            DataContext = model;
        }
    }
}

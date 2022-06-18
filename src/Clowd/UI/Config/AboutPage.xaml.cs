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
        public ICommand ClickLicenseCommand { get; set; }

        public OpenSourceLibrary()
        {
            ClickLicenseCommand = new RelayUICommand(OnClick);
        }

        private void OnClick(object obj)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = LibraryLicenseUrl,
                UseShellExecute = true
            });
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
NETStandard.Library               https://github.com/dotnet/standard/blob/master/LICENSE.TXT            
Nerdbank.GitVersioning            https://licenses.nuget.org/MIT                                        
Microsoft.DotNet.ILCompiler       https://licenses.nuget.org/MIT                                        
Nerdbank.GitVersioning            https://licenses.nuget.org/MIT                                        
System.Memory                     https://github.com/dotnet/corefx/blob/master/LICENSE.TXT              
NETStandard.Library               https://github.com/dotnet/standard/blob/master/LICENSE.TXT            
Nerdbank.GitVersioning            https://licenses.nuget.org/MIT                                        
Microsoft.Win32.Registry          https://licenses.nuget.org/MIT                                        
Microsoft.Windows.CsWin32         https://licenses.nuget.org/MIT                                        
RT.Serialization                  https://github.com/RT-Projects/RT.Util/blob/master/LICENSE                                                                      
Newtonsoft.Json                   https://licenses.nuget.org/MIT                                        
NETStandard.Library               https://github.com/dotnet/standard/blob/master/LICENSE.TXT            
Nerdbank.GitVersioning            https://licenses.nuget.org/MIT                                        
RT.Util.Core                      https://github.com/RT-Projects/RT.Util/blob/master/LICENSE                                                                      
RT.Serialization.Xml              https://github.com/RT-Projects/RT.Util/blob/master/LICENSE                                                                      
NETStandard.Library               https://github.com/dotnet/standard/blob/master/LICENSE.TXT            
YamlDotNet                        https://github.com/aaubry/YamlDotNet/blob/master/LICENSE.txt                                   
Nerdbank.GitVersioning            https://licenses.nuget.org/MIT                                        
WindowsAzure.Storage              https://github.com/Azure/azure-storage-net/blob/master/LICENSE.txt    
NAudio                            https://github.com/naudio/NAudio/blob/master/license.txt              
Nerdbank.GitVersioning            https://licenses.nuget.org/MIT                                        
System.Drawing.Common             https://licenses.nuget.org/MIT                                        
Nerdbank.GitVersioning            https://licenses.nuget.org/MIT                                        
Dragablz                          https://raw.githubusercontent.com/ButchersBoy/Dragablz/master/LICENSE 
Microsoft.VisualBasic             https://github.com/dotnet/corefx/blob/master/LICENSE.TXT              
DotNetZip                         https://raw.githubusercontent.com/haf/DotNetZip.Semverd/master/LICENSE
ModernWpfUI                       https://licenses.nuget.org/MIT                                        
SharpRaven                        https://github.com/getsentry/raven-csharp/blob/develop/LICENSE                                                                      
Microsoft.Xaml.Behaviors.Wpf      https://licenses.nuget.org/MIT                                        
PropertyChanged.Fody              https://licenses.nuget.org/MIT                                        
Nerdbank.GitVersioning            https://licenses.nuget.org/MIT                                        
LightInject                       https://licenses.nuget.org/MIT                                        
ThomasLevesque.WeakEvent          https://licenses.nuget.org/Apache-2.0                                 
Fody                              https://github.com/Fody/Fody/blob/master/License.txt                                 
Microsoft.SourceLink.GitHub       https://licenses.nuget.org/Apache-2.0                                 
System.IO.Packaging               https://licenses.nuget.org/MIT                                        
Microsoft.CSharp                  https://licenses.nuget.org/MIT                                        
Microsoft.Web.Xdt                 https://licenses.nuget.org/Apache-2.0                                 
NETStandard.Library               https://github.com/dotnet/standard/blob/master/LICENSE.TXT            
Nerdbank.GitVersioning            https://licenses.nuget.org/MIT                                        
Microsoft.Win32.Registry          https://licenses.nuget.org/MIT                                        
System.ComponentModel.Annotations https://licenses.nuget.org/MIT                                        
Microsoft.SourceLink.GitHub       https://licenses.nuget.org/Apache-2.0                                 
NETStandard.Library               https://github.com/dotnet/standard/blob/master/LICENSE.TXT            
Nerdbank.GitVersioning            https://licenses.nuget.org/MIT                                        
System.Drawing.Common             https://licenses.nuget.org/MIT                                        
Microsoft.SourceLink.GitHub       https://licenses.nuget.org/Apache-2.0                                 
SharpCompress                     https://github.com/adamhathcock/sharpcompress/blob/master/LICENSE.txt                                                                      
NETStandard.Library               https://github.com/dotnet/standard/blob/master/LICENSE.TXT            
Nerdbank.GitVersioning            https://licenses.nuget.org/MIT                                        
Mono.Cecil                        https://licenses.nuget.org/MIT
Vanara.PInvoke.Kernel32           https://licenses.nuget.org/MIT                                        
Vanara.PInvoke.Shell32            https://licenses.nuget.org/MIT                                        
Nerdbank.GitVersioning            https://licenses.nuget.org/MIT                                        
Vanara.PInvoke.User32             https://licenses.nuget.org/MIT                                        
Microsoft.Win32.Registry          https://licenses.nuget.org/MIT                                        
Vanara.PInvoke.Gdi32              https://licenses.nuget.org/MIT                                        
Vanara.PInvoke.SHCore             https://licenses.nuget.org/MIT                                        
Vanara.PInvoke.DwmApi             https://licenses.nuget.org/MIT 
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
            deps.Add(new OpenSourceLibrary { LibraryName = "Cyotek.Windows.Forms.ColorPicker", LibraryLicenseUrl = "https://github.com/cyotek/Cyotek.Windows.Forms.ColorPicker/blob/master/LICENSE.txt" });
            deps.Add(new OpenSourceLibrary { LibraryName = "wpf-notifyicon", LibraryLicenseUrl = "https://github.com/hardcodet/wpf-notifyicon/blob/master/LICENSE" });
            deps.Add(new OpenSourceLibrary { LibraryName = "ookii-dialogs-wpf", LibraryLicenseUrl = "https://github.com/ookii-dialogs/ookii-dialogs-wpf/blob/master/LICENSE" });
            deps.Add(new OpenSourceLibrary { LibraryName = "Squirrel.Windows", LibraryLicenseUrl = "https://github.com/Squirrel/Squirrel.Windows/blob/develop/COPYING" });
            deps.Add(new OpenSourceLibrary { LibraryName = "DeltaCompressionDotNet", LibraryLicenseUrl = "https://github.com/taspeotis/DeltaCompressionDotNet/blob/master/LICENSE" });
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

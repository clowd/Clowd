using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Windows.Input;
using Avalonia.Controls;
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
            ClickLicenseCommand = new RelayCommand() { Executed = OnClick };
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

    public partial class AboutPage : UserControl
    {
        // package list updated for the Avalonia port (the WPF-era dependencies no longer ship).
        private const string _nugetPackages = @"
Avalonia                            https://github.com/AvaloniaUI/Avalonia/blob/master/licence.md
Avalonia.Fonts.Inter                https://github.com/rsms/inter/blob/master/LICENSE.txt
Semi.Avalonia                       https://github.com/irihitech/Semi.Avalonia/blob/main/LICENSE
Irihi.Ursa                          https://github.com/irihitech/Ursa.Avalonia/blob/main/LICENSE
burningmime.curves                  https://github.com/burningmime/curves
";

        public AboutPage()
        {
            InitializeComponent();

            var model = new AboutPageViewModel();
            model.ClowdVersion = "Version " + GetCurrentVersion();

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

            model.Dependencies = deps
                .GroupBy(d => d.LibraryName) // remove duplicates
                .Select(g => g.FirstOrDefault())
                .OrderBy(d => d.LibraryName)
                .ToList();

            DataContext = model;
        }

        private static string GetCurrentVersion()
        {
            var assembly = typeof(AboutPage).Assembly;
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!String.IsNullOrWhiteSpace(informational))
                return informational;

            return assembly.GetName().Version?.ToString() ?? "(dev)";
        }
    }
}

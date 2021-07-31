using Clowd.Setup.Features;
using PowerArgs;
using System;
using System.IO;
using System.Linq;

namespace Clowd.Setup
{
    [ArgExceptionBehavior(ArgExceptionPolicy.DontHandleExceptions)]
    public class InstallerArgs
    {
        [HelpHook]
        [ArgShortcut("-h")]
        [ArgDescription("Shows application help text")]
        public bool Help { get; set; }

        [ArgShortcut("-dir")]
        [ArgDescription("You must provide the path to the main application directory if this cli tool is not in the same directory")]
        public string AppDirectory { get; set; }

        [ArgShortcut("-y")]
        [ArgDescription("Runs in non-interactive mode, and answers YES to any interactive user prompts for confirmation")]
        public bool Yes
        {
            get => Log.AutoResponse == LogAutoResponse.Yes;
            set => Log.AutoResponse = value ? LogAutoResponse.Yes : LogAutoResponse.None;
        }

        [ArgShortcut("-n")]
        [ArgDescription("Runs in non-interactive mode, and answers NO to any interactive user prompts for confirmation")]
        public bool No
        {
            get => Log.AutoResponse == LogAutoResponse.No;
            set => Log.AutoResponse = value ? LogAutoResponse.No : LogAutoResponse.None;
        }

        [ArgDescription("Will pause execution on startup to allow a debugger to be attached")]
        public bool Debug { get; set; }

        [ArgShortcut("-log")]
        [ArgDescription("Will log all console output to the specified file")]
        public string LogFile
        {
            get => Log.LogFile;
            set => Log.LogFile = value;
        }

        [ArgIgnore]
        public string AppExePath => Path.Combine(AppDirectory, Constants.ClowdExeName);

        [ArgIgnore]
        public bool IsInstallerInUse => Constants.CurrentExePath.StartsWith(Path.GetFullPath(AppDirectory), StringComparison.OrdinalIgnoreCase);

        [ArgActionMethod]
        [ArgDescription("Update's the application to the specified (or latest) version")]
        [ArgExample("clowdcli update 1.0.43.56", "Updates to a specific version")]
        [ArgExample("clowdcli update -c beta", "Updates to the latest beta version")]
        [ArgExample("clowdcli update -y", "Updates to the latest version in non-interactive mode")]
        public void Update(UpdateArgs args)
        {
            Startup();

            AvailablePackagesResult result = null;
            UpdatePackage package = null;

            Log.Spinner("Getting list of available packages...", (s) =>
            {
                result = UpdateHelper.GetAvailablePackages();
                package = UpdateHelper.GetLatestChannelRelease(args.Channel);
            });

            if (!String.IsNullOrEmpty(args.Version))
            {
                package = result.Packages.FirstOrDefault(p => p.Channel.Equals(args.Channel, StringComparison.OrdinalIgnoreCase) && p.Version == args.Version);
            }

            if (package == null)
                throw new ArgumentException($"Version '{args.Version ?? "latest"}' in channel '{args.Channel}' was not found and could not be downloaded.");

            var manager = UpdateHelper.GetUpdaterInstance(AppDirectory, Constants.CurrentExePath);

            Log.Spinner("Preparing...", (s) =>
            {
                manager.CleanUp();
            });

            var source = new NAppUpdate.Framework.Sources.SimpleWebSource(package.FeedUrl);

            Log.Spinner("Checking for updates...", (s) =>
            {
                manager.CheckForUpdates(source);
            });

            if (manager.UpdatesAvailable == 0)
            {
                Log.Green($"{Constants.ClowdAppName} is up to date.");
                return;
            }
            else
            {
                Log.Yellow("Updates are available");
            }

            Log.Spinner("Downloading updates...", (s) =>
            {
                NAppUpdate.Framework.Common.ReportProgressDelegate prog = (p) =>
                {
                    s.Text = $"Downloading updates ({p.Percentage}%)...";
                };

                manager.ReportProgress += prog;
                manager.PrepareUpdates(source);
                manager.ReportProgress -= prog;
            });

            Log.White($"Updates have been downloaded.");

            Log.YesOrThrow("Would you like to install updates now? This program will terminate.", "Update cancelled by user");

            manager.ApplyUpdate(args.Launch, Debug, LogFile, false);
        }

        [ArgActionMethod]
        [ArgDescription("Install an application feature from the system")]
        [ArgExample("clowdcli addfeature directshow", "Install DirectShow feature")]
        [ArgExample("clowdcli addfeature autostart", "Install AutoStart feature")]
        [ArgExample("clowdcli addfeature controlpanel", "Install ControlPanel feature")]
        [ArgExample("clowdcli addfeature shortcuts", "Install Shortcuts feature")]
        public void AddFeature([ArgRequired][ArgDescription("The name of the feature")] string FeatureName)
        {
            Startup();
            var feature = GetFeature(FeatureName);
            FeatureName = feature.GetType().Name;

            if (feature.NeedsPrivileges() && !SystemEx.IsProcessElevated)
            {
                Log.YesOrThrow("This operation requires elevation, continue?", "Installation cancelled, elevation is required");
                Program.Elevate(AppDirectory, true, feature.GetType());
            }
            else
            {
                Log.White($"Installing '{FeatureName}'");
                feature.Install(AppExePath);
            }

            Log.Green($"{FeatureName} installed successfully.");
        }

        [ArgActionMethod]
        [ArgDescription("Remove an application feature from the system")]
        [ArgExample("clowdcli removefeature directshow", "Uninstall DirectShow feature")]
        [ArgExample("clowdcli removefeature autostart", "Uninstall AutoStart feature")]
        [ArgExample("clowdcli removefeature controlpanel", "Uninstall ControlPanel feature")]
        [ArgExample("clowdcli removefeature shortcuts", "Uninstall Shortcuts feature")]
        public void RemoveFeature([ArgRequired][ArgDescription("The name of the feature")] string FeatureName)
        {
            Startup();
            var feature = GetFeature(FeatureName);
            FeatureName = feature.GetType().Name;

            if (feature.NeedsPrivileges() && !SystemEx.IsProcessElevated)
            {
                Log.YesOrThrow("This operation requires elevation, continue?", "Uninstallation cancelled, elevation is required");
                Program.Elevate(AppDirectory, false, feature.GetType());
            }
            else
            {
                Log.White($"Uninstalling '{FeatureName}'");
                feature.Uninstall(AppExePath);
            }

            Log.Green($"{FeatureName} uninstalled successfully.");
        }

        [ArgActionMethod]
        [ArgDescription("Removes all application features and deletes application")]
        public void Uninstall()
        {
            Startup();

            Log.YesOrThrow("This will uninstall all Clowd features and delete all traces of Clowd from your system, continue?", "Uninstall was cancelled by user.");

            throw new NotSupportedException();
        }

        [ArgActionMethod]
        [ArgDescription("Connects to a running instance of " + Constants.ClowdAppName + " and completes a task. " +
            "This function is used by " + Constants.ClowdAppName + " itself to run code outside of the main process, and should not be used directly by the user.")]
        public void ProcessIPC()
        {
            // we run startup later after recieving the IPO
            // Startup();

            NAppUpdate.Updater.RunnerAppStart.Run(this);
        }

        internal void Startup()
        {
            if (Debug)
            {
                Console.WriteLine();
                Console.WriteLine("[DEBUG] Pausing to attach debugger. Press any key to continue.");
                Console.WriteLine();
                Console.ReadKey();
            }

            if (!String.IsNullOrEmpty(AppDirectory))
            {
                if (File.Exists(AppDirectory) && AppDirectory.EndsWith(Constants.ClowdExeName))
                {
                    AppDirectory = Path.GetFullPath(Path.GetDirectoryName(AppDirectory));
                    return;
                }
                else if (Directory.Exists(AppDirectory) && File.Exists(AppExePath))
                {
                    AppDirectory = Path.GetFullPath(AppDirectory);
                    return;
                }
            }
            else
            {
                var location = Constants.CurrentExePath;
                var directory = Path.GetDirectoryName(location);

                if (File.Exists(Path.Combine(directory, Constants.ClowdExeName)))
                {
                    AppDirectory = Path.GetFullPath(directory);
                    return;
                }
            }

            throw new ArgumentException($"You must provide a path to the {Constants.ClowdAppName} application directory with the \"-dir\" " +
                $"argument if it is not located in the same directory as this exe.", nameof(AppDirectory));
        }

        private IFeature GetFeature(string featureName)
        {
            var types = new Type[] {
                typeof(AutoStart),
                typeof(ContextMenu),
                typeof(ControlPanel),
                typeof(Shortcuts),
            };

            Type feature = types.SingleOrDefault(s => s.Name.Equals(featureName, StringComparison.OrdinalIgnoreCase));
            if (feature == null)
                throw new ArgumentException($"Feature '{featureName}' not found.{Environment.NewLine}{Environment.NewLine}Available features: {Environment.NewLine}{String.Join(Environment.NewLine, types.Select(t => " - " + t.Name))}");

            var inst = (IFeature)Activator.CreateInstance(feature);
            return inst;
        }
    }

    public class UpdateArgs
    {
        [ArgPosition(1)]
        [ArgShortcut("-v")]
        [ArgDescription("Version to update to, if unspecified defaults to latest version")]
        public string Version { get; set; }

        [ArgShortcut("-c")]
        [ArgDescription("Update channel to retrieve release from.")]
        [ArgDefaultValue("stable")]
        public string Channel { get; set; }

        [ArgDescription("Will launch application after updates are installed successfully")]
        public bool Launch { get; set; }
    }
}

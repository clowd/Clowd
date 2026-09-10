using System;
using System.Linq;
using Avalonia;
using Clowd.Config;
using Clowd.UI;
using Velopack;

namespace Clowd
{
    internal static class Program
    {
        /// <summary>Set by the Velopack first-run hook: this is the first launch after an install,
        /// so the settings window opens instead of going straight to the tray.</summary>
        public static bool IsVelopackFirstRun { get; private set; }

        /// <summary>Marks a launch nobody asked for: the OS login item (AutoStartManager registers
        /// it with this flag) and the updater's restart after a background update both carry it, and
        /// it is the only thing that keeps a window from opening. Every other launch is a user
        /// action — clicking the icon has to look like it did something.</summary>
        public const string AutoStartedArg = "--autostarted";

        /// <summary>Restart argument for an explicit "Restart to Update": the relaunched process
        /// opens the General page, where the freshly installed version is shown.</summary>
        public const string UpdatedRestartArg = "--updated";

        /// <summary>What builds before <see cref="AutoStartedArg"/> passed for a background update
        /// restart. Still consumed so an update applied by the old build comes up in the tray.</summary>
        private const string LegacySilentUpdateRestartArg = "--applied-background-update";

        /// <summary>True when the process was started by the OS at login or relaunched by the
        /// updater in the background — no window should open.</summary>
        public static bool IsAutoStarted { get; private set; }

        /// <summary>True when the process was relaunched by an explicit "Restart to Update" click.</summary>
        public static bool IsUpdateRestart { get; private set; }

        // Initialization code. Don't use any Avalonia, third-party APIs or any
        // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
        // yet and stuff might break.
        [STAThread]
        public static void Main(string[] args)
        {
            // First thing in the process, so a crash anywhere below is reported — including inside
            // the Velopack hooks. Disposing flushes the queue on a normal exit; the hook paths
            // terminate the process instead, which is why the panic-time flush lives in the SDK's
            // own shutdown handling rather than here.
            using var sentry = SentryConfig.Init();

            // Velopack hooks (install/update/uninstall) must run before anything else; Run()
            // exits the process when invoked with a hook argument.
            var velopack = VelopackApp.Build()
                                      .OnFirstRun(_ => IsVelopackFirstRun = true);

            // fast callbacks are Windows-only, which is exactly the platform where auto-start is on
            // by default (SettingsGeneral.DefaultRegisterAutoStart). Registering here means the login
            // item exists before Clowd first runs, and is gone again after an uninstall even though
            // the settings file lingers.
            if (OperatingSystem.IsWindows())
            {
                velopack = velopack
                    .OnAfterInstallFastCallback(_ =>
                    {
                        AutoStartManager.TrySetEnabled(true);
                        ExplorerContextMenuManager.TrySetEnabled(true);
                        SparsePackageManager.TrySetEnabled(true);
                    })
                    // the sparse package embeds the app version, so every update has to re-register
                    // the bumped MSIX (which also retro-installs the Win11 menu on installs that
                    // predate it). Hooks run in a fresh process before App ever loads settings, so
                    // read the file directly — unlike install, an update must respect a user who
                    // turned these off (a corrupt file falls back to the same defaults install uses).
                    .OnAfterUpdateFastCallback(_ =>
                    {
                        SettingsGeneral general;
                        try { general = SettingsService.Load().General; }
                        catch { general = new SettingsGeneral(); }

                        AutoStartManager.Sync(general.RegisterAutoStart);
                        ExplorerContextMenuManager.Sync(general.RegisterExplorerContextMenu);
                        SparsePackageManager.Sync(general.RegisterExplorerContextMenu);
                    })
                    // the settings file outlives an uninstall, so all registrations have to be torn
                    // down explicitly here or they linger pointing at a deleted executable.
                    .OnBeforeUninstallFastCallback(_ =>
                    {
                        AutoStartManager.TrySetEnabled(false);
                        ExplorerContextMenuManager.TrySetEnabled(false);
                        SparsePackageManager.TrySetEnabled(false);
                    });
            }

            velopack.Run();

            args = ConsumeStartupFlags(args);

            // single-instance enforcement (MutexArgsForwarder) and argument forwarding happens
            // in App.OnFrameworkInitializationCompleted (NiceDialog needs the Avalonia platform
            // initialized for the unresponsive-instance error paths).
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        /// <summary>Strips the launch-origin flags off the command line and records them. What is
        /// left is the app's CLI surface, <c>upload "path" ...</c> or legacy bare paths (see
        /// <see cref="CliArgs"/> / MutexArgsForwarder), so a forwarded second instance never carries
        /// them across.</summary>
        private static string[] ConsumeStartupFlags(string[] args)
        {
            if (args == null || args.Length == 0)
                return args;

            bool IsFlag(string arg, string flag) => String.Equals(arg, flag, StringComparison.OrdinalIgnoreCase);

            IsAutoStarted = args.Any(a => IsFlag(a, AutoStartedArg) || IsFlag(a, LegacySilentUpdateRestartArg));
            IsUpdateRestart = args.Any(a => IsFlag(a, UpdatedRestartArg));

            return args.Where(a => !IsFlag(a, AutoStartedArg)
                                   && !IsFlag(a, LegacySilentUpdateRestartArg)
                                   && !IsFlag(a, UpdatedRestartArg))
                       .ToArray();
        }

        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                         .UsePlatformDetect()
                         .WithInterFont()
                         // tray-resident: launch without a dock icon; MacDockIcon flips the
                         // activation policy to Regular whenever a real window opens.
                         .With(new MacOSPlatformOptions { ShowInDock = false })
                         .LogToTrace();
    }
}

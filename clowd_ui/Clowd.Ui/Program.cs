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

        /// <summary>Restart argument UpdateService passes when it applies an update in the background,
        /// so the relaunched process knows to stay in the tray.</summary>
        public const string SilentUpdateRestartArg = "--applied-background-update";

        /// <summary>True when this process was relaunched by the updater after a background update.
        /// The argument is stripped before Avalonia sees it — every other command line argument is
        /// treated as a file to upload (MutexArgsForwarder).</summary>
        public static bool IsSilentUpdateRestart { get; private set; }

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

            args = ConsumeSilentUpdateArg(args);

            // single-instance enforcement (MutexArgsForwarder) and argument forwarding happens
            // in App.OnFrameworkInitializationCompleted (NiceDialog needs the Avalonia platform
            // initialized for the unresponsive-instance error paths).
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        private static string[] ConsumeSilentUpdateArg(string[] args)
        {
            if (args == null || args.Length == 0)
                return args;

            var remaining = args.Where(a => !String.Equals(a, SilentUpdateRestartArg, StringComparison.OrdinalIgnoreCase)).ToArray();
            IsSilentUpdateRestart = remaining.Length != args.Length;
            return remaining;
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

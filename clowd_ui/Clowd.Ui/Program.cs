using System;
using Avalonia;
using Clowd.UI;
using Velopack;

namespace Clowd
{
    internal static class Program
    {
        /// <summary>Set by the Velopack first-run hook: this is the first launch after an install,
        /// so the settings window opens instead of going straight to the tray.</summary>
        public static bool IsVelopackFirstRun { get; private set; }

        // Initialization code. Don't use any Avalonia, third-party APIs or any
        // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
        // yet and stuff might break.
        [STAThread]
        public static void Main(string[] args)
        {
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
                    .OnAfterInstallFastCallback(_ => AutoStartManager.TrySetEnabled(true))
                    .OnBeforeUninstallFastCallback(_ => AutoStartManager.TrySetEnabled(false));
            }

            velopack.Run();

            // single-instance enforcement (MutexArgsForwarder) and argument forwarding happens
            // in App.OnFrameworkInitializationCompleted (NiceDialog needs the Avalonia platform
            // initialized for the unresponsive-instance error paths).
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                         .UsePlatformDetect()
                         .WithInterFont()
                         .LogToTrace();
    }
}

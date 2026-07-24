using System;
using Avalonia;
using Velopack;

namespace Clowd
{
    internal static class Program
    {
        // Initialization code. Don't use any Avalonia, third-party APIs or any
        // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
        // yet and stuff might break.
        [STAThread]
        public static void Main(string[] args)
        {
            // Velopack hooks (install/update/uninstall) must run before anything else; Run()
            // exits the process when invoked with a hook argument.
            VelopackApp.Build().Run();

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

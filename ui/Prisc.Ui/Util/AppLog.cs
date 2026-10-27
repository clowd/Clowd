using System;
using System.Diagnostics;
using Velopack.Locators;
using Velopack.Logging;

namespace Prisc
{
    /// <summary>
    /// A line that has to survive a Release build. <c>Debug.WriteLine</c> compiles away there, so a
    /// one-off decision on a user's machine (a migration skipped, an update ignored) would leave no
    /// trace; this also writes it to Velopack's own log file, the one persistent text log the
    /// shell has (on Windows <c>%LOCALAPPDATA%\velopack\velopack_&lt;AppId&gt;.log</c>), next to
    /// what the installer and updater record. Before Velopack has set up its locator (the first
    /// statements of <see cref="Program.Main"/>) the line only reaches the debugger.
    /// </summary>
    /// <remarks>For rare, decision-level lines only: Sentry stays exceptions-only
    /// (<see cref="SentryConfig"/>), and anything chatty belongs in <c>Debug.WriteLine</c>.</remarks>
    internal static class AppLog
    {
        public static void Info(string message)
        {
            Debug.WriteLine(message);

            try
            {
                if (VelopackLocator.IsCurrentSet)
                    VelopackLocator.Current?.Log?.Info(message);
            }
            catch
            {
                // logging must never take down the caller
            }
        }
    }
}

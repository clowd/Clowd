using System;
using System.IO;
using Velopack.Locators;

namespace Clowd.Util
{
    /// <summary>
    /// The path the OS should be pointed at to launch Clowd — used by the auto-start entry and the
    /// Explorer context menu verb, both of which outlive any single installed version.
    /// </summary>
    internal static class AppLaunchPath
    {
        private static readonly Lazy<string> _current = new(Resolve);

        /// <summary>
        /// Velopack keeps the installed app under <c>&lt;root&gt;\current\</c> and swaps that
        /// directory's contents in place on update, so this stays valid across updates — unlike the
        /// versioned staging paths. Falls back to the running executable for loose / dev builds.
        /// </summary>
        public static string Current => _current.Value;

        private static string Resolve()
        {
            var locator = VelopackLocator.Current;
            if (locator != null && !String.IsNullOrEmpty(locator.AppContentDir) && !String.IsNullOrEmpty(locator.ThisExeRelativePath))
                return Path.Combine(locator.AppContentDir, locator.ThisExeRelativePath);

            return Environment.ProcessPath
                   ?? throw new InvalidOperationException("Could not determine the path of the running executable.");
        }
    }
}

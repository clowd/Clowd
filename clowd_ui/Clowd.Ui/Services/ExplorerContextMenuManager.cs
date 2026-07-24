using System;
using System.Diagnostics;
using System.Runtime.Versioning;
using Clowd.Util;
using Microsoft.Win32;

namespace Clowd.UI
{
    /// <summary>
    /// Backs "Add 'Upload with Clowd' to the Explorer context menu"
    /// (<see cref="Clowd.Config.SettingsGeneral.RegisterExplorerContextMenu"/>).
    ///
    /// This is the legacy (Windows 7 style) shell verb the WPF build used: a pair of per-user keys
    /// under <c>HKCU\Software\Classes</c>, one for all files and one for directories. No elevation,
    /// no COM server, no shell extension DLL — Explorer just runs the command line. Windows 11 still
    /// honours these but files them under "Show more options" (Shift+F10) rather than the compact
    /// menu; appearing in the compact menu additionally requires package identity via a sparse MSIX
    /// package, which is deliberately not implemented here.
    ///
    /// The verb is <c>"&lt;exe&gt;" "%1"</c>, so selecting N files makes Explorer launch N processes.
    /// That is intentional and matches the WPF build: <see cref="MutexArgsForwarder"/> forwards each
    /// one's arguments to the already-running instance over a named pipe and coalesces them on a 1s
    /// debounce, so the N paths arrive as a single batch and upload as one archive.
    /// </summary>
    internal static class ExplorerContextMenuManager
    {
        private const string MenuTitle = "Upload with Clowd";

        private const string FilesShellPath = @"Software\Classes\*\shell";
        private const string DirectoryShellPath = @"Software\Classes\Directory\shell";

        // debug builds register under their own key so running from the IDE cannot overwrite (or
        // delete) the verb belonging to a real install — same split as SettingsService.FilePath.
#if DEBUG
        private const string VerbKeyName = Constants.ClowdAppName + ".DEBUG";
#else
        private const string VerbKeyName = Constants.ClowdAppName;
#endif

        /// <summary>Raised after every <see cref="TrySetEnabled"/>, so the settings page can show
        /// <see cref="LastError"/> when registration failed.</summary>
        public static event EventHandler StateChanged;

        /// <summary>The error from the most recent failed apply, or null if the last one worked.</summary>
        public static string LastError { get; private set; }

        public static bool IsSupported => OperatingSystem.IsWindows();

        /// <summary>Whether the verb is currently registered. Returns false (rather than throwing)
        /// if the state can't be read.</summary>
        public static bool IsEnabled()
        {
            try
            {
                if (OperatingSystem.IsWindows())
                    return IsEnabledWindows();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("ExplorerContextMenuManager: failed to read context menu state: " + ex);
            }

            return false;
        }

        /// <summary>Registers or unregisters the verb. Returns false and sets <see cref="LastError"/>
        /// on failure; the context menu is a convenience, so no call site treats a failure as fatal.</summary>
        public static bool TrySetEnabled(bool enabled)
        {
            try
            {
                // the platform analyzer only flows OperatingSystem.IsWindows() guards written out in
                // full, so this cannot be phrased as a check against IsSupported.
                if (!OperatingSystem.IsWindows())
                    throw new PlatformNotSupportedException("The Explorer context menu is only available on Windows.");

                SetEnabledWindows(enabled);
                LastError = null;
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("ExplorerContextMenuManager: failed to apply context menu: " + ex);
                LastError = ex.Message;
                return false;
            }
            finally
            {
                StateChanged?.Invoke(null, EventArgs.Empty);
            }
        }

        /// <summary>Reconciles the registry with the saved setting at startup. Rewrites rather than
        /// merely checking presence when enabled, so a verb left pointing at a stale executable path
        /// repairs itself.</summary>
        public static void Sync(bool enabled)
        {
            if (!IsSupported)
                return;

            if (enabled || IsEnabled())
                TrySetEnabled(enabled);
        }

        [SupportedOSPlatform("windows")]
        private static bool IsEnabledWindows()
        {
            using var shell = Registry.CurrentUser.OpenSubKey(FilesShellPath, false);
            using var verb = shell?.OpenSubKey(VerbKeyName, false);
            return verb != null;
        }

        [SupportedOSPlatform("windows")]
        private static void SetEnabledWindows(bool enabled)
        {
            foreach (var shellPath in new[] { FilesShellPath, DirectoryShellPath })
            {
                using var shell = Registry.CurrentUser.CreateSubKey(shellPath, true)
                                  ?? throw new InvalidOperationException("Could not open HKCU\\" + shellPath + ".");

                if (!enabled)
                {
                    shell.DeleteSubKeyTree(VerbKeyName, false);
                    continue;
                }

                using var verb = shell.CreateSubKey(VerbKeyName, true);
                verb.SetValue("", MenuTitle, RegistryValueKind.String);

                // Icon is parsed as "path,index", so the path has to be quoted — user profile
                // directories routinely contain spaces. Index 0 is the application icon.
                verb.SetValue("Icon", "\"" + AppLaunchPath.Current + "\"", RegistryValueKind.String);

                using var command = verb.CreateSubKey("command", true);
                command.SetValue("", "\"" + AppLaunchPath.Current + "\" \"%1\"", RegistryValueKind.String);
            }
        }
    }
}

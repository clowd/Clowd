using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Prisc.Legacy;
using Velopack.Locators;

namespace Prisc.UI.Legacy;

/// <summary>
/// Removes the OS registrations a pre-rename install left behind: on Windows the Run value, the
/// classic Explorer verbs, the Task Manager startup state (carried over to the new name), the
/// sparse MSIX package and its extension DLLs; on macOS the LaunchAgent plist. Every name comes
/// from <see cref="LegacyClowd"/>. Release builds touch only the release names and Debug builds
/// only the <c>.DEBUG</c> ones, the same split as <see cref="AutoStartManager"/>.
///
/// Nothing here runs while a separate, not-upgraded install still exists beside this one
/// (<see cref="HasSeparateLegacyInstall"/>): those registrations are still that app's. Every
/// method is safe to call on any OS (a no-op elsewhere), never throws, and is idempotent.
/// </summary>
public static class LegacyClowdShellCleanup
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupApprovedRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private static readonly string[] VerbShellPaths = { @"Software\Classes\*\shell", @"Software\Classes\Directory\shell" };

#if DEBUG
    private const string LegacyRegistryName = LegacyClowd.RegistryNameDebug;
    private const string CurrentRegistryName = Constants.PriscAppName + ".DEBUG";
    private const string LegacyLaunchAgentLabel = LegacyClowd.LaunchAgentLabelDebug;
#else
    private const string LegacyRegistryName = LegacyClowd.RegistryName;
    private const string CurrentRegistryName = Constants.PriscAppName;
    private const string LegacyLaunchAgentLabel = LegacyClowd.LaunchAgentLabel;
#endif

    /// <summary>The Velopack install root of this app (on macOS its <c>.app</c> bundle), or null
    /// for a dev or loose build.</summary>
    public static string OwnInstallRoot => PathConstants.VelopackInstallRoot;

    /// <summary><see cref="LegacyClowd.HasSeparateLegacyInstall"/> for this machine: LocalAppData
    /// on Windows, <c>/Applications</c> and <c>~/Applications</c> on macOS, and
    /// <see cref="OwnInstallRoot"/>. A dev build (no install root) beside an installed copy of the
    /// old app always answers true, so it never migrates or cleans up the real install.</summary>
    public static bool HasSeparateLegacyInstall()
    {
        string localAppData = null;
        string[] applicationsDirs = Array.Empty<string>();

        try
        {
            if (OperatingSystem.IsWindows())
            {
                localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            }
            else if (OperatingSystem.IsMacOS())
            {
                applicationsDirs = new[]
                {
                    "/Applications",
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications"),
                };
            }
        }
        catch (Exception ex)
        {
            Log("could not resolve the install locations: " + ex.Message);
            return true;
        }

        return LegacyClowd.HasSeparateLegacyInstall(localAppData, applicationsDirs, OwnInstallRoot);
    }

    /// <summary>
    /// Windows: deletes the legacy Run value and Explorer verb keys, and moves the legacy Task
    /// Manager startup state (<c>StartupApproved\Run</c>, keyed by the Run value name) to the new
    /// name unless the new name already has one, so a user who disabled the old app in Task
    /// Manager does not find the new one starting at login. Fast; call it synchronously before
    /// <see cref="AutoStartManager.Sync"/>. A no-op on other platforms.
    /// </summary>
    public static void RunRegistryCleanup()
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            if (HasSeparateLegacyInstall())
            {
                Log("a separate legacy install is present; its registrations are left alone");
                return;
            }

            RegistryCleanupWindows();
        }
        catch (Exception ex)
        {
            Log("registry cleanup failed: " + ex);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void RegistryCleanupWindows()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (run?.GetValue(LegacyRegistryName) != null)
            {
                run.DeleteValue(LegacyRegistryName, throwOnMissingValue: false);
                Log("removed the legacy Run value");
            }
        }
        catch (Exception ex)
        {
            Log("could not remove the legacy Run value: " + ex.Message);
        }

        foreach (var shellPath in VerbShellPaths)
        {
            try
            {
                using var shell = Registry.CurrentUser.OpenSubKey(shellPath, writable: true);
                if (shell != null && shell.GetSubKeyNames().Contains(LegacyRegistryName, StringComparer.OrdinalIgnoreCase))
                {
                    shell.DeleteSubKeyTree(LegacyRegistryName, throwOnMissingSubKey: false);
                    Log("removed the legacy verb under " + shellPath);
                }
            }
            catch (Exception ex)
            {
                Log("could not remove the legacy verb under " + shellPath + ": " + ex.Message);
            }
        }

        try
        {
            using var approved = Registry.CurrentUser.OpenSubKey(StartupApprovedRunKeyPath, writable: true);
            var legacyState = approved?.GetValue(LegacyRegistryName);
            if (legacyState != null)
            {
                if (approved.GetValue(CurrentRegistryName) == null && legacyState is byte[] bytes)
                    approved.SetValue(CurrentRegistryName, bytes, RegistryValueKind.Binary);

                approved.DeleteValue(LegacyRegistryName, throwOnMissingValue: false);
                Log("carried the Task Manager startup state over to " + CurrentRegistryName);
            }
        }
        catch (Exception ex)
        {
            Log("could not carry the Task Manager startup state over: " + ex.Message);
        }
    }

    /// <summary>
    /// Windows, installed builds only: unregisters the legacy sparse MSIX package and deletes the
    /// legacy extension DLLs from the install root (a DLL still loaded by Explorer or dllhost
    /// survives until a later run). Gated on such a DLL being present, since the package never
    /// exists without one, so a cleaned-up machine never spawns PowerShell for this. Slow (spawns
    /// PowerShell): call it last in the Velopack hooks and on a background thread at startup.
    /// A no-op on other platforms.
    /// </summary>
    public static void RunPackageCleanup()
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            if (!VelopackLocator.IsCurrentSet || VelopackLocator.Current?.IsPortable != false)
                return;

            var root = OwnInstallRoot;
            if (String.IsNullOrEmpty(root) || !Directory.Exists(root))
                return;

            if (!Directory.EnumerateFiles(root, LegacyClowd.ShellExtensionDllPattern).Any())
                return;

            if (HasSeparateLegacyInstall())
            {
                Log("a separate legacy install is present; its shell extension is left alone");
                return;
            }

            PackageCleanupWindows(root);
        }
        catch (Exception ex)
        {
            Log("package cleanup failed: " + ex);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void PackageCleanupWindows(string root)
    {
        // a no-op when the package is absent. If it throws, the DLLs stay, so the next run
        // (gated on them) retries both steps.
        SparsePackageManager.RunPowerShell("Get-AppxPackage -Name '"
                                           + SparsePackageManager.EscapePowerShellLiteral(LegacyClowd.ShellExtensionPackageName)
                                           + "' | Remove-AppxPackage");
        Log("removed the legacy sparse package");

        foreach (var dll in Directory.EnumerateFiles(root, LegacyClowd.ShellExtensionDllPattern).ToList())
        {
            try
            {
                File.Delete(dll);
                Log("deleted " + dll);
            }
            catch (Exception ex)
            {
                // Explorer/dllhost can hold it until the next sign-in; a later run retries
                Log("could not delete " + dll + ": " + ex.Message);
            }
        }
    }

    /// <summary>macOS: deletes the legacy LaunchAgent plist, if present, and moves the update
    /// cache out of the legacy app's Velopack folder (<see cref="LegacyClowd.MigrateMacUpdatePackages"/>).
    /// Call it before <see cref="AutoStartManager.Sync"/>. A no-op on other platforms.</summary>
    public static void RunMacCleanup()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "LaunchAgents", LegacyLaunchAgentLabel + ".plist");
            var packagesDir = GetLegacyMacPackagesDir();

            if (!File.Exists(path) && packagesDir == null)
                return;

            // the plist and update cache of a still-installed, not-upgraded copy of the old app are
            // still that app's
            if (HasSeparateLegacyInstall())
            {
                Log("a separate legacy install is present; its LaunchAgent and update cache are left alone");
                return;
            }

            if (File.Exists(path))
            {
                File.Delete(path);
                Log("deleted " + path);
            }

            if (packagesDir != null)
                LegacyClowd.MigrateMacUpdatePackages(packagesDir, VelopackLocator.Current.PackagesDir, Constants.PriscAppName);
        }
        catch (Exception ex)
        {
            Log("macOS cleanup failed: " + ex);
        }
    }

    /// <summary>The legacy app's Velopack packages folder beside ours
    /// (<c>~/Library/Caches/velopack/Clowd/packages</c>) when it exists and this is an installed
    /// build; null otherwise.</summary>
    private static string GetLegacyMacPackagesDir()
    {
        if (OwnInstallRoot == null)
            return null;

        // <cache>/velopack/<packId>/packages: only rebase a folder of that shape onto the old id
        var packages = VelopackLocator.Current.PackagesDir;
        var appCache = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(packages ?? ""));
        var velopackCache = Path.GetDirectoryName(appCache ?? "");
        if (String.IsNullOrEmpty(velopackCache) || !String.Equals(Path.GetFileName(appCache), Constants.PriscAppName, StringComparison.OrdinalIgnoreCase))
            return null;

        var legacy = Path.Combine(velopackCache, LegacyClowd.FolderName, Path.GetFileName(packages.TrimEnd('/')));
        return Directory.Exists(legacy) ? legacy : null;
    }

    /// <summary>
    /// macOS: moves an install upgraded across the rename from <c>Clowd.app</c> to a sibling
    /// <c>Prisc.app</c> (see <see cref="LegacyClowd.GetMacBundleRenameTarget"/>). A running app
    /// cannot move its own bundle, as later loads resolve against the old path, so this hands off
    /// to a <c>/bin/sh</c> that waits for this process to exit, renames the bundle and relaunches it
    /// with the same arguments (the old path if the rename failed). Returns true when that handoff
    /// is under way and the caller must exit at once, before taking the single-instance mutex.
    ///
    /// Attempted once per install (a marker in <see cref="PathConstants.AppDataRoot"/>), so a
    /// rename macOS refuses costs one extra relaunch rather than one on every start; and only while
    /// nothing else runs out of the bundle (a second launch next to a running Prisc, the standby
    /// capturer), since every such process would lose its own files. Never throws.
    /// </summary>
    public static bool TryRelaunchFromRenamedBundle(string[] args)
    {
        if (!OperatingSystem.IsMacOS())
            return false;

        try
        {
            var bundle = OwnInstallRoot;
            var target = LegacyClowd.GetMacBundleRenameTarget(bundle, Constants.PriscAppName + ".app");
            if (target == null)
                return false;

            var marker = Path.Combine(PathConstants.AppDataRoot, BundleRenameMarkerFileName);
            if (File.Exists(marker))
                return false;

            if (IsAnyPriscProcessRunning())
            {
                Log("another Prisc process runs out of " + bundle + "; the bundle rename waits for a later start");
                return false;
            }

            Directory.CreateDirectory(PathConstants.AppDataRoot);
            File.WriteAllText(marker, "");

            // $1 pid, $2 bundle, $3 target, the rest are the app's own arguments
            const string script =
                "pid=$1; old=$2; new=$3; shift 3; " +
                "while kill -0 \"$pid\" 2>/dev/null; do sleep 0.1; done; " +
                "app=$old; if [ ! -e \"$new\" ] && /bin/mv \"$old\" \"$new\"; then app=$new; fi; " +
                "exec /usr/bin/open -n \"$app\" --args \"$@\"";

            var psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
            foreach (var a in new[] { "-c", script, "sh", Environment.ProcessId.ToString(), bundle, target })
                psi.ArgumentList.Add(a);
            foreach (var a in args ?? Array.Empty<string>())
                psi.ArgumentList.Add(a);

            using var _ = Process.Start(psi);
            Log("relaunching from " + target);
            return true;
        }
        catch (Exception ex)
        {
            Log("bundle rename failed: " + ex);
            return false;
        }
    }

    private const string BundleRenameMarkerFileName = ".legacy-bundle-rename-attempted";

    /// <summary>The executables Prisc runs out of its bundle, other than this process.</summary>
    private static bool IsAnyPriscProcessRunning()
    {
        string[] names = { "Prisc.Ui", "Prisc.VideoRender", "prisc_capture", "prisc_scroll_driver", "prisc_ai", "prisc_recorder", "prisc_share_region", "prisc_vid2gif" };
        var self = Environment.ProcessId;

        foreach (var name in names)
        {
            var processes = Process.GetProcessesByName(name);
            try
            {
                if (processes.Any(p => p.Id != self))
                    return true;
            }
            finally
            {
                foreach (var p in processes)
                    p.Dispose();
            }
        }

        return false;
    }

    private static void Log(string message) => Debug.WriteLine("LegacyClowdShellCleanup: " + message);
}

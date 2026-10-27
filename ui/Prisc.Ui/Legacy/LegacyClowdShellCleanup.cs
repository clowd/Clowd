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
/// Nothing here runs while another install still lives under the old name beside this one, not
/// yet updated or upgraded in place (<see cref="HasSeparateLegacyInstall"/>): the legacy
/// registrations are that app's to keep or (once upgraded, which it already did) to clean up. Every
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
    /// <see cref="OwnInstallRoot"/>. A dev build (no install root) beside an install under the old
    /// name (a Clowd install, or one upgraded in place to Prisc) always answers true, so it never
    /// migrates or cleans up the real install.</summary>
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

    /// <summary>macOS: deletes the legacy LaunchAgent plist, if present. Call it before
    /// <see cref="AutoStartManager.Sync"/>. A no-op on other platforms.</summary>
    public static void RunMacCleanup()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "LaunchAgents", LegacyLaunchAgentLabel + ".plist");

            if (!File.Exists(path))
                return;

            // the plist of a still-installed, not-upgraded copy of the old app is its autostart
            if (HasSeparateLegacyInstall())
            {
                Log("a separate legacy install is present; its LaunchAgent is left alone");
                return;
            }

            File.Delete(path);
            Log("deleted " + path);
        }
        catch (Exception ex)
        {
            Log("LaunchAgent cleanup failed: " + ex);
        }
    }

    private static void Log(string message) => Debug.WriteLine("LegacyClowdShellCleanup: " + message);
}

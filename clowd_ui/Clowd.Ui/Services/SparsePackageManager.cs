using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Velopack.Locators;

namespace Clowd.UI
{
    /// <summary>
    /// Backs the Windows 11 half of "Add 'Upload with Clowd' to the Explorer context menu"
    /// (<see cref="Clowd.Config.SettingsGeneral.RegisterExplorerContextMenu"/>).
    ///
    /// Windows 11 only shows packaged apps in its compact right-click menu, so alongside the
    /// legacy registry verb (<see cref="ExplorerContextMenuManager"/>, which Win11 files under
    /// "Show more options") this registers a sparse MSIX package: an unpackaged manifest-only
    /// package whose ExternalLocation points at the Velopack install root, declaring the
    /// IExplorerCommand COM server in <c>clowd_shell_ext.dll</c>. Both registrations are driven by
    /// the same setting; on Windows 10 and in dev builds this manager is simply unsupported and
    /// every operation is a quiet no-op.
    ///
    /// Registration goes through <c>powershell.exe</c> (Add-AppxPackage/Get-AppxPackage): the
    /// plain net10.0 TFM has no WinRT projection for the PackageManager API, and shelling out
    /// keeps it that way. The DLL is copied out of <c>current\</c> to the install root first so
    /// Explorer's file lock on the loaded extension can never block Velopack's directory swap on
    /// update.
    /// </summary>
    internal static class SparsePackageManager
    {
        private const string PackageName = "Clowd.ShellExtension";
        private const string MsixFileName = "ClowdShellExt.msix";
        private const string SourceDllFileName = "clowd_shell_ext.dll";
        private const string InstalledDllFileName = "ClowdShellExt.dll";

        // Velopack fast-callback hooks are killed after 15s, but the interactive paths (settings
        // checkbox, startup sync) can afford to wait out a slow deployment service.
        private const int PowerShellTimeoutMs = 90_000;

        /// <summary>Raised after every <see cref="TrySetEnabled"/> (and whenever a read changes
        /// <see cref="LastKnownIsEnabled"/>), so the settings page can show <see cref="LastError"/>
        /// or update its caption.</summary>
        public static event EventHandler StateChanged;

        /// <summary>The error from the most recent failed apply, or null if the last one worked.</summary>
        public static string LastError { get; private set; }

        /// <summary>The registration state as of the last read or apply. UI code renders this —
        /// <see cref="IsEnabled"/> shells out to PowerShell and must stay off the UI thread.</summary>
        public static bool LastKnownIsEnabled { get; private set; }

        /// <summary>Sparse packages need Win11, a real Velopack install to point ExternalLocation
        /// at, and the MSIX shipped next to the executable — dev builds fail all three, so nothing
        /// here ever spawns PowerShell in a debug session.</summary>
        public static bool IsSupported
        {
            get
            {
                if (!OperatingSystem.IsWindows() || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
                    return false;

                var locator = VelopackLocator.Current;
                if (locator == null || locator.CurrentlyInstalledVersion == null || String.IsNullOrEmpty(locator.RootAppDir))
                    return false;

                return File.Exists(Path.Combine(AppContext.BaseDirectory, MsixFileName));
            }
        }

        /// <summary>Whether the sparse package is currently registered. Returns false (rather than
        /// throwing) if the state can't be read. Spawns PowerShell — never call on the UI thread;
        /// UI code should render <see cref="LastKnownIsEnabled"/> instead.</summary>
        public static bool IsEnabled() => GetRegisteredVersion() != null;

        /// <summary>The version of the registered package (e.g. "4.1.5.0"), or null when it is not
        /// registered or the state can't be read. Spawns PowerShell — never call on the UI thread.</summary>
        public static string GetRegisteredVersion()
        {
            try
            {
                // the platform analyzer only flows OperatingSystem.IsWindows() guards written out
                // in full, so this cannot be phrased as a check against IsSupported alone.
                if (OperatingSystem.IsWindows() && IsSupported)
                    return GetRegisteredVersionWindows();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("SparsePackageManager: failed to read sparse package state: " + ex);
                SentryConfig.CaptureHandled(ex, "shellreg.read-sparse-package");
            }

            return null;
        }

        /// <summary>Registers or unregisters the sparse package. Returns false and sets
        /// <see cref="LastError"/> on failure; the modern context menu is a convenience, so no
        /// call site treats a failure as fatal. Spawns PowerShell — never call on the UI thread.</summary>
        public static bool TrySetEnabled(bool enabled)
        {
            try
            {
                if (!OperatingSystem.IsWindows())
                    return false;

                // Win10, dev builds and loose builds quietly do nothing rather than error — the
                // legacy registry verb already covers those machines.
                if (!IsSupported)
                    return false;

                SetEnabledWindows(enabled);
                UpdateLastKnown(enabled);
                LastError = null;
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("SparsePackageManager: failed to apply sparse package: " + ex);
                SentryConfig.CaptureHandled(ex, "shellreg.apply-sparse-package");
                LastError = ex.Message;
                return false;
            }
            finally
            {
                StateChanged?.Invoke(null, EventArgs.Empty);
            }
        }

        /// <summary>Reconciles the deployment state with the saved setting — at startup and after
        /// every update, since the package version tracks the app version and a stale registration
        /// keeps launching whatever the manifest was generated against. Never throws. Spawns
        /// PowerShell — call from a background thread (or a Velopack hook).</summary>
        public static void Sync(bool enabled)
        {
            if (!IsSupported)
                return;

            var registered = GetRegisteredVersion();

            if (enabled)
            {
                if (registered == null || registered != ExpectedPackageVersion())
                    TrySetEnabled(true);
            }
            else if (registered != null)
            {
                TrySetEnabled(false);
            }
        }

        [SupportedOSPlatform("windows")]
        private static void SetEnabledWindows(bool enabled)
        {
            var root = VelopackLocator.Current.RootAppDir;
            var installedDll = Path.Combine(root, InstalledDllFileName);

            if (!enabled)
            {
                RunPowerShell("Get-AppxPackage -Name '" + PackageName + "' | Remove-AppxPackage");

                // best-effort: Explorer/dllhost may still have the extension loaded; a leftover
                // DLL is inert once the package is gone and is overwritten on the next enable.
                try
                {
                    if (File.Exists(installedDll))
                        File.Delete(installedDll);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("SparsePackageManager: could not delete " + installedDll + ": " + ex.Message);
                }

                return;
            }

            CopyExtensionDll(Path.Combine(AppContext.BaseDirectory, SourceDllFileName), installedDll);

            // Add-AppxPackage updates in place when the version went up, but re-adding the same
            // version is an error — and the DLL refresh above is all a same-version apply needs.
            if (GetRegisteredVersionWindows() == ExpectedPackageVersion())
                return;

            RunPowerShell("Add-AppxPackage -Path '" + EscapePowerShellLiteral(Path.Combine(AppContext.BaseDirectory, MsixFileName))
                          + "' -ExternalLocation '" + EscapePowerShellLiteral(root) + "'");
        }

        [SupportedOSPlatform("windows")]
        private static string GetRegisteredVersionWindows()
        {
            var output = RunPowerShell("(Get-AppxPackage -Name '" + PackageName + "').Version");
            var version = String.IsNullOrWhiteSpace(output) ? null : output.Trim();
            UpdateLastKnown(version != null);
            return version;
        }

        /// <summary>The package version the MSIX shipped with this build carries: the app's
        /// three-part Velopack version with the mandatory fourth component pinned to 0 (CI
        /// generates the manifest from the same nbgv version).</summary>
        private static string ExpectedPackageVersion()
        {
            var version = VelopackLocator.Current?.CurrentlyInstalledVersion;
            return version == null ? null : version.Major + "." + version.Minor + "." + version.Patch + ".0";
        }

        /// <summary>Refreshes the install-root copy of the extension DLL from <c>current\</c>.
        /// A locked target is logged and ignored: the old DLL's only job is spawning the exe, so
        /// it keeps working until Explorer lets go and a later apply refreshes it.</summary>
        private static void CopyExtensionDll(string sourceDll, string targetDll)
        {
            try
            {
                if (File.Exists(targetDll) && FilesAreIdentical(sourceDll, targetDll))
                    return;

                File.Copy(sourceDll, targetDll, true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("SparsePackageManager: could not refresh " + targetDll + ": " + ex.Message);
            }
        }

        private static bool FilesAreIdentical(string pathA, string pathB)
        {
            var infoA = new FileInfo(pathA);
            var infoB = new FileInfo(pathB);
            if (infoA.Length != infoB.Length)
                return false;

            using var sha = SHA256.Create();
            using var streamA = File.OpenRead(pathA);
            var hashA = sha.ComputeHash(streamA);

            using var streamB = File.OpenRead(pathB);
            return hashA.AsSpan().SequenceEqual(sha.ComputeHash(streamB));
        }

        private static void UpdateLastKnown(bool registered)
        {
            if (LastKnownIsEnabled == registered)
                return;

            LastKnownIsEnabled = registered;
            StateChanged?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>Escapes a value for embedding in a single-quoted PowerShell string literal
        /// (user profile paths can contain apostrophes).</summary>
        private static string EscapePowerShellLiteral(string value) => value.Replace("'", "''");

        /// <summary>Runs a script via <c>powershell.exe</c> and returns its stdout. Throws on a
        /// non-zero exit or timeout, with the process output in the message. Errors are promoted
        /// to terminating so a failed cmdlet cannot exit 0.</summary>
        [SupportedOSPlatform("windows")]
        private static string RunPowerShell(string script)
        {
            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add("$ErrorActionPreference = 'Stop'; " + script);

            using var process = Process.Start(psi)
                                ?? throw new InvalidOperationException("Failed to start powershell.exe.");

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(PowerShellTimeoutMs))
            {
                try { process.Kill(entireProcessTree: true); }
                catch { }

                throw new TimeoutException("PowerShell did not finish within " + (PowerShellTimeoutMs / 1000) + "s: " + script);
            }

            // the timed overload returns as soon as the process exits; this blocks until the
            // redirected streams have drained so stdout/stderr below are complete.
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                var detail = (stderr.Result + "\n" + stdout.Result).Trim();
                throw new InvalidOperationException("PowerShell exited with code " + process.ExitCode
                                                    + (detail.Length > 0 ? ": " + detail : "."));
            }

            return stdout.Result;
        }
    }
}

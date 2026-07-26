using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Security;
using Clowd.Util;
using Microsoft.Win32;

namespace Clowd.UI
{
    /// <summary>
    /// Backs "Start Clowd when your computer starts up" (<see cref="Clowd.Config.SettingsGeneral.RegisterAutoStart"/>).
    ///
    /// Windows: an <c>HKCU\...\CurrentVersion\Run</c> value. The Velopack install hook (Program.cs)
    /// writes it at install time and the uninstall hook removes it again, which is why the setting
    /// defaults to on there — a fresh install is already registered before Clowd first runs.
    ///
    /// macOS: a per-user LaunchAgent plist. Velopack's fast callbacks are Windows-only and dropping
    /// a file into the user's LaunchAgents folder isn't something the installer does, so the setting
    /// defaults to off and registration happens after the fact when the user ticks the box. Writing
    /// (or deleting) the plist takes effect at the next login; we deliberately don't
    /// <c>launchctl load</c> it, since RunAtLoad would immediately spawn a second Clowd.
    /// </summary>
    internal static class AutoStartManager
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        // debug builds register under their own name so running from the IDE cannot overwrite (or
        // delete) the login item belonging to a real install — same split as SettingsService.FilePath.
#if DEBUG
        private const string RunValueName = Constants.ClowdAppName + ".DEBUG";
        private const string LaunchAgentLabel = "com.clowd.Clowd.DEBUG";
#else
        private const string RunValueName = Constants.ClowdAppName;
        private const string LaunchAgentLabel = "com.clowd.Clowd";
#endif

        /// <summary>Raised after every <see cref="TrySetEnabled"/>, so the settings page can show
        /// <see cref="LastError"/> when registration failed.</summary>
        public static event EventHandler StateChanged;

        /// <summary>The error from the most recent failed apply, or null if the last one worked.</summary>
        public static string LastError { get; private set; }

        public static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

        /// <summary>Whether the OS is currently configured to launch Clowd at login. Returns false
        /// (rather than throwing) if the state can't be read.</summary>
        public static bool IsEnabled()
        {
            try
            {
                if (OperatingSystem.IsWindows())
                    return IsEnabledWindows();

                if (OperatingSystem.IsMacOS())
                    return File.Exists(GetLaunchAgentPath());
            }
            catch (Exception ex)
            {
                Debug.WriteLine("AutoStartManager: failed to read auto-start state: " + ex);
                SentryConfig.CaptureHandled(ex, "shellreg.read-autostart");
            }

            return false;
        }

        /// <summary>Registers or unregisters the login item. Returns false and sets
        /// <see cref="LastError"/> on failure; auto-start is a convenience, so no call site treats a
        /// failure as fatal.</summary>
        public static bool TrySetEnabled(bool enabled)
        {
            try
            {
                if (!IsSupported)
                    throw new PlatformNotSupportedException("Starting Clowd at login is not supported on this platform.");

                if (OperatingSystem.IsWindows())
                    SetEnabledWindows(enabled);
                else
                    SetEnabledMacOS(enabled);

                LastError = null;
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("AutoStartManager: failed to apply auto-start: " + ex);
                SentryConfig.CaptureHandled(ex, "shellreg.apply-autostart");
                LastError = ex.Message;
                return false;
            }
            finally
            {
                StateChanged?.Invoke(null, EventArgs.Empty);
            }
        }

        /// <summary>Reconciles the OS state with the saved setting at startup — the setting is the
        /// source of truth, so this repairs a login item removed behind Clowd's back (or left behind
        /// by an install whose setting was later turned off). Rewrites rather than merely checking
        /// presence when enabled, so an entry pointing at a stale executable path repairs itself.</summary>
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
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            return key?.GetValue(RunValueName) is string s && !String.IsNullOrWhiteSpace(s);
        }

        [SupportedOSPlatform("windows")]
        private static void SetEnabledWindows(bool enabled)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true)
                            ?? throw new InvalidOperationException("Could not open the Windows Run registry key.");

            if (enabled)
                key.SetValue(RunValueName, "\"" + AppLaunchPath.Current + "\"", RegistryValueKind.String);
            else if (key.GetValue(RunValueName) != null)
                key.DeleteValue(RunValueName, false);
        }

        private static void SetEnabledMacOS(bool enabled)
        {
            var path = GetLaunchAgentPath();

            if (!enabled)
            {
                if (File.Exists(path))
                    File.Delete(path);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path));

            // `open -a <bundle>` rather than exec'ing the inner Mach-O directly: launchd would
            // otherwise start it outside the normal app-launch path, which breaks the dock/menu-bar
            // registration Avalonia expects.
            var target = SecurityElement.Escape(GetMacOSLaunchTarget());
            var plist =
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
                "<plist version=\"1.0\">\n" +
                "<dict>\n" +
                "  <key>Label</key>\n" +
                "  <string>" + LaunchAgentLabel + "</string>\n" +
                "  <key>ProgramArguments</key>\n" +
                "  <array>\n" +
                "    <string>/usr/bin/open</string>\n" +
                "    <string>-a</string>\n" +
                "    <string>" + target + "</string>\n" +
                "  </array>\n" +
                "  <key>RunAtLoad</key>\n" +
                "  <true/>\n" +
                "  <key>LimitLoadToSessionType</key>\n" +
                "  <string>Aqua</string>\n" +
                "</dict>\n" +
                "</plist>\n";

            File.WriteAllText(path, plist);
        }

        private static string GetLaunchAgentPath() =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "LaunchAgents", LaunchAgentLabel + ".plist");

        /// <summary>The <c>.app</c> bundle to hand to <c>open -a</c>, found by walking up from the
        /// running executable (<c>Clowd.app/Contents/MacOS/Clowd</c>).</summary>
        private static string GetMacOSLaunchTarget()
        {
            var exePath = Environment.ProcessPath
                          ?? throw new InvalidOperationException("Could not determine the path of the running executable.");

            for (var dir = Path.GetDirectoryName(exePath); !String.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
            {
                if (dir.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                    return dir;
            }

            // not running from a bundle (dev build) — launchd can exec the binary directly.
            return exePath;
        }
    }
}

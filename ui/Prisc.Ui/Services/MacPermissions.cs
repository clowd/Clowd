using System;
using System.Diagnostics;
using System.Threading;
using Avalonia.Threading;
using Prisc.Interop;

namespace Prisc.UI
{
    /// <summary>
    /// The two macOS privacy permissions Prisc cannot work without. Backs the Permissions group on the
    /// System settings page, the gate in front of the capture overlay (<see cref="ScreenCapturePage"/>)
    /// and the eyedropper (<see cref="Prisc.Util.ScreenPixels"/>).
    /// </summary>
    /// <remarks>
    /// Every member is safe to call on any platform: off macOS <see cref="IsRelevant"/> is false and
    /// both permissions read as granted (prisc_interop answers NotApplicable there), so callers need
    /// no <c>OperatingSystem.IsMacOS()</c> dance of their own.
    ///
    /// The checks go through prisc_interop (<see cref="Permissions"/>), which answers with the one
    /// rule the hotkey hook, the scrolling driver and the capturer share: Screen Recording is
    /// <c>CGPreflightScreenCaptureAccess</c>, Accessibility is
    /// <c>AXIsProcessTrusted &amp;&amp; CGPreflightPostEventAccess</c> (an event tap needs both). Both
    /// are per-app decisions recorded in TCC against the bundle, not the individual executable, which
    /// is what makes a check here meaningful for the out-of-process Rust capturer:
    /// <c>prisc_capture</c> ships inside <c>Prisc.app/Contents/MacOS</c> and is launched by us, so TCC
    /// holds Prisc.app responsible for its captures.
    ///
    /// Requesting is one-shot by OS design. The system prompt shows only until the user answers it
    /// once; every later request just returns the stored answer without showing anything. So the
    /// settings button asks first and, once asking has stopped working, sends the user to System
    /// Settings instead: a button that silently does nothing is the worst of the three options.
    ///
    /// Screen Recording cannot be picked up by a running process: macOS hands the app that TCC answer
    /// at launch, so granting it takes effect on the next start, and callers say so rather than
    /// leaving the user to wonder why the toggle they just flipped changed nothing. Accessibility is
    /// different: it answers live, and the hotkey hooks (<see cref="NativeHotkeyHost"/>'s and the
    /// standby capturer's) poll it while they are down and restart once it turns true, so that grant
    /// needs no restart.
    /// </remarks>
    internal static class MacPermissions
    {
        /// <summary>Whether this platform has these permissions at all: false everywhere but macOS,
        /// where the Permissions settings group is hidden entirely.</summary>
        public static bool IsRelevant => OperatingSystem.IsMacOS();

        /// <summary>The nav label of the settings page the Permissions group heads: "System", or (the
        /// MainWindow badge) "Permission Needed" while a permission is missing.</summary>
        public static string NavLabel(bool permissionMissing) => permissionMissing ? NavLabelMissing : NavLabelGranted;

        private const string NavLabelGranted = "System";
        private const string NavLabelMissing = "Permission Needed";

        /// <summary>Where a message sends the user to grant a permission. It names the page both
        /// ways: the message shows while the nav calls it "Permission Needed", except when only a
        /// hook's own check says no and the nav still reads "System".</summary>
        public const string GrantLocation = "Settings → " + NavLabelMissing + " (the " + NavLabelGranted + " page)";

        /// <summary>Raised on macOS after every <see cref="Request"/> (whatever it answered), after
        /// <see cref="OpenSettings"/> and on <see cref="Refresh"/>, so open UI can re-read the
        /// statuses. Raised on the caller's thread; subscribers post their re-read.</summary>
        public static event EventHandler StateChanged;

        /// <summary>Screen Recording: needed by the capture overlay, the screenshot itself, video
        /// recording and the eyedropper. True off macOS. Never prompts; any thread.</summary>
        public static bool HasScreenRecording => IsUsable(PermissionKind.ScreenRecording, "macperm.preflight-screen");

        /// <summary>Accessibility: needed by the global keyboard hook behind the hotkeys
        /// (<see cref="NativeHotkeyHost"/>) and by scrolling capture. True off macOS. Never prompts;
        /// any thread.</summary>
        public static bool HasAccessibility => IsUsable(PermissionKind.Accessibility, "macperm.ax-trusted");

        /// <summary>
        /// Makes sure <paramref name="permission"/> is granted: when it already is, returns true at
        /// once without prompting; otherwise asks the OS, which shows its own prompt if it still has
        /// one to show, and returns whether the permission is usable as of the call returning.
        /// </summary>
        /// <remarks>
        /// A false return does NOT mean the user refused. macOS only ever offers each prompt once (after
        /// any first answer the request returns the stored verdict without showing anything), and for
        /// Screen Recording even a fresh grant reports false, because the process was handed that TCC
        /// answer at launch and cannot be told otherwise until it restarts. So false means "not usable
        /// yet", covering refused, already-answered and (Screen Recording) just-granted-pending-restart
        /// alike; callers should follow it with <see cref="OpenSettings"/> (plus, for Screen Recording,
        /// a restart hint) rather than treating it as a refusal. An Accessibility grant reads true as
        /// soon as it is made, so callers can just check <see cref="HasAccessibility"/> again and need
        /// no restart hint.
        /// </remarks>
        public static bool Request(MacPermission permission)
        {
            var kind = ToKind(permission);
            bool usable;
            try
            {
                usable = Permissions.Ensure(kind).IsUsable();
            }
            catch (Exception ex)
            {
                // fail open like the plain checks: a request that cannot answer falls back to the
                // non-prompting read (which itself answers true when it cannot answer either), so
                // a broken native call never sends a user who has the permission to the grant
                // dialog.
                Debug.WriteLine("[MacPermissions] requesting " + permission + " failed: " + ex);
                SentryConfig.CaptureHandled(ex, "macperm.request");
                usable = IsUsable(kind, "macperm.request-fallback");
            }

            // the request may find the permission granted since open UI last read it (granted in
            // System Settings, then a capture started without re-activating a Prisc window), so
            // every request nudges subscribers to re-read, not only the ones that left it missing.
            if (IsRelevant)
                StateChanged?.Invoke(null, EventArgs.Empty);
            return usable;
        }

        /// <summary>Opens the System Settings pane for <paramref name="permission"/>, where the user
        /// can grant it by hand.</summary>
        public static void OpenSettings(MacPermission permission)
        {
            if (!IsRelevant)
                return;

            var pane = permission switch
            {
                MacPermission.ScreenRecording => "Privacy_ScreenCapture",
                MacPermission.Accessibility => "Privacy_Accessibility",
                _ => null,
            };

            if (pane == null)
                return;

            try
            {
                Process.Start("open", new[] { "x-apple.systempreferences:com.apple.preference.security?" + pane });
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[MacPermissions] failed to open System Settings: " + ex);
                SentryConfig.CaptureHandled(ex, "macperm.open-settings");
            }

            // the user is expected to change the setting over in System Settings while our UI stays
            // open, so nudge it to re-read once they come back.
            StateChanged?.Invoke(null, EventArgs.Empty);
        }

        public static bool IsGranted(MacPermission permission) => permission switch
        {
            MacPermission.ScreenRecording => HasScreenRecording,
            MacPermission.Accessibility => HasAccessibility,
            _ => true,
        };

        /// <summary>Re-reads the permissions and notifies subscribers: used when a window is
        /// activated again after the user has been away in System Settings.</summary>
        public static void Refresh()
        {
            if (IsRelevant)
                StateChanged?.Invoke(null, EventArgs.Empty);
        }

        private static PermissionKind ToKind(MacPermission permission) => permission switch
        {
            MacPermission.ScreenRecording => PermissionKind.ScreenRecording,
            MacPermission.Accessibility => PermissionKind.Accessibility,
            _ => throw new ArgumentOutOfRangeException(nameof(permission), permission, null),
        };

        private static bool IsUsable(PermissionKind kind, string sentryOperation)
        {
            try
            {
                return Permissions.State(kind).IsUsable();
            }
            catch (Exception ex)
            {
                // a check that cannot answer must not lock the user out of the feature: the capturer
                // and the hook still meet the OS's own refusal and report it.
                Debug.WriteLine("[MacPermissions] reading " + kind + " failed: " + ex);
                SentryConfig.CaptureHandled(ex, sentryOperation);
                return true;
            }
        }
    }

    /// <summary>
    /// The system Accessibility prompt, shown at most once per app run by whichever path first
    /// finds the permission missing: the shell's own hook (<see cref="NativeHotkeyHost"/>) or the
    /// standby capturer's hotkey status (<see cref="CaptureStandbySupervisor"/>; in warm-capture
    /// mode the shell may own no gesture at all). Neither the crate nor the capturer prompts
    /// (their checks never do), yet the prompt is what puts Prisc in the Accessibility list,
    /// sparing the user its '+' button. Once, because the alert comes back on every
    /// <see cref="MacPermissions.Request"/> while the permission is missing.
    /// </summary>
    internal sealed class AccessibilityPrompt
    {
        public static AccessibilityPrompt Shared { get; } = new(OperatingSystem.IsMacOS(),
            () => MacPermissions.Request(MacPermission.Accessibility), action => Dispatcher.UIThread.Post(action));

        private readonly bool _isMacOS;
        private readonly Action _request;
        private readonly Action<Action> _post;
        private int _requested;

        /// <param name="request">Shows the prompt (UI thread).</param>
        /// <param name="post">Runs an action on the UI thread.</param>
        public AccessibilityPrompt(bool isMacOS, Action request, Action<Action> post)
        {
            _isMacOS = isMacOS;
            _request = request ?? throw new ArgumentNullException(nameof(request));
            _post = post ?? throw new ArgumentNullException(nameof(post));
        }

        /// <summary>Asks for Accessibility unless this run already has. Any thread, any lock held:
        /// the prompt itself is posted, since <see cref="MacPermissions.Request"/> raises
        /// <see cref="MacPermissions.StateChanged"/> into UI subscribers synchronously. A no-op
        /// off macOS.</summary>
        public void RequestOnce()
        {
            if (!_isMacOS || Interlocked.Exchange(ref _requested, 1) != 0)
                return;

            _post(_request);
        }
    }

    /// <summary>The macOS privacy permissions Prisc needs. See <see cref="MacPermissions"/>.</summary>
    internal enum MacPermission
    {
        /// <summary>Privacy &amp; Security → Screen &amp; System Audio Recording.</summary>
        ScreenRecording,

        /// <summary>Privacy &amp; Security → Accessibility.</summary>
        Accessibility,
    }
}

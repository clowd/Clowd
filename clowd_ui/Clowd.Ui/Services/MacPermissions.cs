using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Avalonia.Threading;

namespace Clowd.UI
{
    /// <summary>
    /// The two macOS privacy permissions Clowd cannot work without, and the only place that talks
    /// to TCC. Backs the Permissions group on the System settings page, the gate in front of the
    /// capture overlay (<see cref="ScreenCapturePage"/>) and the eyedropper
    /// (<see cref="Clowd.Util.ScreenPixels"/>).
    /// </summary>
    /// <remarks>
    /// Every member is safe to call on any platform: off macOS <see cref="IsRelevant"/> is false
    /// and both permissions read as granted, so callers need no <c>OperatingSystem.IsMacOS()</c>
    /// dance of their own.
    ///
    /// Screen Recording is checked with <c>CGPreflightScreenCaptureAccess</c> and Accessibility
    /// with <c>AXIsProcessTrusted</c>. Both are per-app decisions recorded in TCC against the
    /// bundle, not the individual executable, which is what makes a check here meaningful for the
    /// out-of-process Rust capturer: <c>clowd_capture</c> ships inside
    /// <c>Clowd.app/Contents/MacOS</c> and is launched by us, so TCC holds Clowd.app responsible
    /// for its captures.
    ///
    /// Requesting is one-shot by OS design. <c>CGRequestScreenCaptureAccess</c> shows the system
    /// prompt only until the user answers it once; every later call just returns the stored answer
    /// without showing anything. So the settings button asks first and, once asking has stopped
    /// working, sends the user to System Settings instead — a button that silently does nothing is
    /// the worst of the three options.
    ///
    /// Screen Recording cannot be picked up by a running process: macOS hands the app that TCC
    /// answer at launch, so granting it takes effect on the next start, and callers say so rather
    /// than leaving the user to wonder why the toggle they just flipped changed nothing.
    /// Accessibility is different: <c>AXIsProcessTrusted</c> answers live, and the hotkey hooks
    /// (<see cref="NativeHotkeyHost"/>'s and the standby capturer's) poll it while they are down
    /// and restart once it turns true, so that grant needs no restart.
    /// </remarks>
    internal static class MacPermissions
    {
        /// <summary>Whether this platform has these permissions at all — false everywhere but macOS,
        /// where the Permissions settings group is hidden entirely.</summary>
        public static bool IsRelevant => OperatingSystem.IsMacOS();

        /// <summary>The nav label of the settings page the Permissions group heads: "System", or —
        /// MainWindow's badge — "Permission Needed" while a permission is missing.</summary>
        public static string NavLabel(bool permissionMissing) => permissionMissing ? NavLabelMissing : NavLabelGranted;

        private const string NavLabelGranted = "System";
        private const string NavLabelMissing = "Permission Needed";

        /// <summary>Where a message sends the user to grant a permission. It names the page both
        /// ways: the message shows while the nav calls it "Permission Needed", except when only a
        /// hook's own check says no and the nav still reads "System".</summary>
        public const string GrantLocation = "Settings → " + NavLabelMissing + " (the " + NavLabelGranted + " page)";

        /// <summary>Raised after a successful <see cref="Request"/> or <see cref="OpenSettings"/> so
        /// open UI can re-read the statuses.</summary>
        public static event EventHandler StateChanged;

        // the platform checks below are spelled out as OperatingSystem.IsMacOS() rather than going
        // through IsRelevant so CA1416 can see the guard and the P/Invokes need no suppression.

        /// <summary>Screen Recording: needed by the capture overlay, the screenshot itself, video
        /// recording and the eyedropper. True off macOS.</summary>
        public static bool HasScreenRecording => !OperatingSystem.IsMacOS() || MacOS.PreflightScreenCapture();

        /// <summary>Accessibility: needed by the global keyboard hook behind the hotkeys
        /// (<see cref="NativeHotkeyHost"/>). True off macOS.</summary>
        public static bool HasAccessibility => !OperatingSystem.IsMacOS() || MacOS.IsProcessTrusted();

        /// <summary>
        /// Asks the OS for <paramref name="permission"/>, showing its own prompt if it still has one
        /// to show, and returns whether the permission is granted as of the call returning.
        /// </summary>
        /// <remarks>
        /// A false return does NOT mean the user refused. macOS only ever offers each prompt once —
        /// after any first answer these calls return the stored verdict without showing anything —
        /// and for Screen Recording even a fresh grant usually reports false, because the process
        /// was handed that TCC answer at launch and cannot be told otherwise until it restarts. So
        /// false means "not usable yet", covering refused, already-answered and (Screen Recording)
        /// just-granted-pending-restart alike; callers should follow it with
        /// <see cref="OpenSettings"/> (plus, for Screen Recording, a restart hint) rather than
        /// treating it as a refusal. An Accessibility grant reads true as soon as it is made, so
        /// callers can just check <see cref="HasAccessibility"/> again and need no restart hint.
        /// </remarks>
        public static bool Request(MacPermission permission)
        {
            if (!OperatingSystem.IsMacOS() || IsGranted(permission))
                return IsGranted(permission);

            var granted = permission switch
            {
                MacPermission.ScreenRecording => MacOS.RequestScreenCapture(),
                MacPermission.Accessibility => MacOS.RequestProcessTrust(),
                _ => true,
            };

            StateChanged?.Invoke(null, EventArgs.Empty);
            return granted;
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

        /// <summary>Re-reads the permissions and notifies subscribers — used when a window is
        /// activated again after the user has been away in System Settings.</summary>
        public static void Refresh()
        {
            if (IsRelevant)
                StateChanged?.Invoke(null, EventArgs.Empty);
        }

        [SupportedOSPlatform("macos")]
        private static class MacOS
        {
            public static bool PreflightScreenCapture()
            {
                try
                {
                    return CGPreflightScreenCaptureAccess();
                }
                catch (Exception ex)
                {
                    // an older macOS without the symbol (it landed in 10.15) would throw here; the
                    // permission does not exist there, so nothing is being withheld.
                    Debug.WriteLine("[MacPermissions] CGPreflightScreenCaptureAccess unavailable: " + ex);
                    SentryConfig.CaptureHandled(ex, "macperm.preflight-screen");
                    return true;
                }
            }

            public static bool RequestScreenCapture()
            {
                try
                {
                    return CGRequestScreenCaptureAccess();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[MacPermissions] CGRequestScreenCaptureAccess unavailable: " + ex);
                    SentryConfig.CaptureHandled(ex, "macperm.request-screen");
                    return false;
                }
            }

            public static bool IsProcessTrusted()
            {
                try
                {
                    return AXIsProcessTrusted();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[MacPermissions] AXIsProcessTrusted unavailable: " + ex);
                    SentryConfig.CaptureHandled(ex, "macperm.ax-trusted");
                    return true;
                }
            }

            /// <summary>AXIsProcessTrustedWithOptions with kAXTrustedCheckOptionPrompt, which is the
            /// only way to make macOS offer the Accessibility prompt. Returns the current trust
            /// state, so a false here is the "already answered" case.</summary>
            public static bool RequestProcessTrust()
            {
                IntPtr key = IntPtr.Zero, options = IntPtr.Zero;
                try
                {
                    key = CFStringCreateWithCString(IntPtr.Zero, "AXTrustedCheckOptionPrompt", kCFStringEncodingUTF8);
                    if (key == IntPtr.Zero)
                        return false;

                    options = CFDictionaryCreate(IntPtr.Zero, new[] { key }, new[] { kCFBooleanTrue }, 1,
                                                 IntPtr.Zero, IntPtr.Zero);
                    if (options == IntPtr.Zero)
                        return false;

                    return AXIsProcessTrustedWithOptions(options);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[MacPermissions] AXIsProcessTrustedWithOptions failed: " + ex);
                    SentryConfig.CaptureHandled(ex, "macperm.ax-trusted-opts");
                    return false;
                }
                finally
                {
                    if (options != IntPtr.Zero)
                        CFRelease(options);
                    if (key != IntPtr.Zero)
                        CFRelease(key);
                }
            }

            private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
            private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
            private const string ApplicationServices =
                "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";

            private const uint kCFStringEncodingUTF8 = 0x08000100;

            [DllImport(CoreGraphics)]
            [return: MarshalAs(UnmanagedType.I1)]
            private static extern bool CGPreflightScreenCaptureAccess();

            [DllImport(CoreGraphics)]
            [return: MarshalAs(UnmanagedType.I1)]
            private static extern bool CGRequestScreenCaptureAccess();

            [DllImport(ApplicationServices)]
            [return: MarshalAs(UnmanagedType.I1)]
            private static extern bool AXIsProcessTrusted();

            [DllImport(ApplicationServices)]
            [return: MarshalAs(UnmanagedType.I1)]
            private static extern bool AXIsProcessTrustedWithOptions(IntPtr options);

            [DllImport(CoreFoundation)]
            private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, string cStr, uint encoding);

            [DllImport(CoreFoundation)]
            private static extern IntPtr CFDictionaryCreate(IntPtr allocator, IntPtr[] keys, IntPtr[] values, nint count,
                                                            IntPtr keyCallBacks, IntPtr valueCallBacks);

            [DllImport(CoreFoundation)]
            private static extern void CFRelease(IntPtr cf);

            // kCFBooleanTrue is exported data, not a function, so it has to be resolved by hand.
            private static readonly IntPtr kCFBooleanTrue = ResolveCFBooleanTrue();

            private static IntPtr ResolveCFBooleanTrue()
            {
                try
                {
                    var handle = NativeLibrary.Load(CoreFoundation);
                    return Marshal.ReadIntPtr(NativeLibrary.GetExport(handle, "kCFBooleanTrue"));
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[MacPermissions] could not resolve kCFBooleanTrue: " + ex);
                    SentryConfig.CaptureHandled(ex, "macperm.cfbool");
                    return IntPtr.Zero;
                }
            }
        }
    }

    /// <summary>
    /// The system Accessibility prompt, shown at most once per app run by whichever path first
    /// finds the permission missing: the shell's own hook (<see cref="NativeHotkeyHost"/>) or the
    /// standby capturer's hotkey status (<see cref="CaptureStandbySupervisor"/> — in warm-capture
    /// mode the shell may own no gesture at all). Neither the crate nor the capturer prompts
    /// (<c>AXIsProcessTrusted</c> never does), yet the prompt is what puts Clowd in the
    /// Accessibility list, sparing the user its '+' button. Once, because the alert comes back on
    /// every <see cref="MacPermissions.Request"/> while the permission is missing.
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

    /// <summary>The macOS privacy permissions Clowd needs. See <see cref="MacPermissions"/>.</summary>
    internal enum MacPermission
    {
        /// <summary>Privacy &amp; Security → Screen &amp; System Audio Recording.</summary>
        ScreenRecording,

        /// <summary>Privacy &amp; Security → Accessibility.</summary>
        Accessibility,
    }
}

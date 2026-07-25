using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Clowd.UI
{
    /// <summary>
    /// The two macOS privacy permissions Clowd cannot work without, and the only place that talks
    /// to TCC. Backs the Permissions group on the General settings page, the gate in front of the
    /// capture overlay (<see cref="ScreenCapturePage"/>) and the eyedropper
    /// (<see cref="Clowd.Util.ScreenColorReader"/>).
    /// </summary>
    /// <remarks>
    /// Every member is safe to call on any platform: off macOS <see cref="IsRelevant"/> is false
    /// and both permissions read as granted, so callers need no <c>OperatingSystem.IsMacOS()</c>
    /// dance of their own.
    ///
    /// Screen Recording is checked with <c>CGPreflightScreenCaptureAccess</c> and Accessibility
    /// with <c>AXIsProcessTrusted</c>. Both are per-app decisions recorded in TCC against the
    /// bundle, not the individual executable, which is what makes a check here meaningful for the
    /// out-of-process Rust capturer: <c>clowd_capture_wgpu</c> ships inside
    /// <c>Clowd.app/Contents/MacOS</c> and is launched by us, so TCC holds Clowd.app responsible
    /// for its captures.
    ///
    /// Requesting is one-shot by OS design. <c>CGRequestScreenCaptureAccess</c> shows the system
    /// prompt only until the user answers it once; every later call just returns the stored answer
    /// without showing anything. So the settings button asks first and, once asking has stopped
    /// working, sends the user to System Settings instead — a button that silently does nothing is
    /// the worst of the three options.
    ///
    /// Neither permission can be picked up by a running process: macOS hands the app its TCC
    /// answer at launch, so granting takes effect on the next start. Callers say so rather than
    /// leaving the user to wonder why the toggle they just flipped changed nothing.
    /// </remarks>
    internal static class MacPermissions
    {
        /// <summary>Whether this platform has these permissions at all — false everywhere but macOS,
        /// where the Permissions settings group is hidden entirely.</summary>
        public static bool IsRelevant => OperatingSystem.IsMacOS();

        /// <summary>Raised after a successful <see cref="Request"/> or <see cref="OpenSettings"/> so
        /// open UI can re-read the statuses.</summary>
        public static event EventHandler StateChanged;

        // the platform checks below are spelled out as OperatingSystem.IsMacOS() rather than going
        // through IsRelevant so CA1416 can see the guard and the P/Invokes need no suppression.

        /// <summary>Screen Recording: needed by the capture overlay, the screenshot itself, video
        /// recording and the eyedropper. True off macOS.</summary>
        public static bool HasScreenRecording => !OperatingSystem.IsMacOS() || MacOS.PreflightScreenCapture();

        /// <summary>Accessibility: needed by the global keyboard hook behind the hotkeys
        /// (<see cref="GlobalHotkeyHost"/>). True off macOS.</summary>
        public static bool HasAccessibility => !OperatingSystem.IsMacOS() || MacOS.IsProcessTrusted();

        /// <summary>
        /// Asks the OS for <paramref name="permission"/>, showing its own prompt if it still has one
        /// to show, and returns whether the permission is granted as of the call returning.
        /// </summary>
        /// <remarks>
        /// A false return does NOT mean the user refused. macOS only ever offers each prompt once —
        /// after any first answer these calls return the stored verdict without showing anything —
        /// and even a fresh grant usually reports false, because the process was handed its TCC
        /// answer at launch and cannot be told otherwise until it restarts. So false means "not
        /// usable yet", covering refused, already-answered and just-granted-pending-restart alike;
        /// callers should follow it with <see cref="OpenSettings"/> or a restart hint rather than
        /// treating it as a refusal.
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
                    return IntPtr.Zero;
                }
            }
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

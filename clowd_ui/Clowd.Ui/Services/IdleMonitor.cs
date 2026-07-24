using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Clowd.Config;

namespace Clowd.UI
{
    /// <summary>
    /// Decides whether now is a quiet enough moment to restart Clowd underneath the user in order to
    /// apply a downloaded update (<see cref="Clowd.Config.SettingsGeneral.AutoApplyUpdates"/>).
    ///
    /// The primary signal is how long the *machine* has been without keyboard or mouse input — an
    /// update should land while the user is away from the computer, not merely while they happen not
    /// to be looking at Clowd. Windows and macOS both expose that directly
    /// (<see cref="GetSystemIdleTime"/>); anywhere else it is unavailable and the fallback is the
    /// last interaction with a Clowd window, which the windows themselves report in here.
    ///
    /// On top of the idle test are a few hard guards that hold regardless of how long the machine has
    /// been untouched: an unattended recording or upload is still running work that a restart would
    /// destroy, and open editors may only be restarted through if they are set to reopen afterwards.
    /// </summary>
    internal static class IdleMonitor
    {
        /// <summary>How long the machine has to have been idle before a background restart is
        /// considered acceptable.</summary>
        public static readonly TimeSpan IdleThreshold = TimeSpan.FromMinutes(10);

        // seeded with "now" rather than DateTime.MinValue: a Clowd that has only just started is not
        // idle, and shouldn't restart itself moments after the user launched it.
        private static long _lastInteractionTicks = DateTime.UtcNow.Ticks;
        private static long _lastCaptureTicks = DateTime.UtcNow.Ticks;

        /// <summary>Called when the user does something in a Clowd window (editors, settings). Only
        /// used where the OS cannot report system-wide idle time.</summary>
        public static void NotifyInteraction() =>
            Interlocked.Exchange(ref _lastInteractionTicks, DateTime.UtcNow.Ticks);

        /// <summary>Called when a capture or recording overlay opens and again when it closes, so the
        /// quiet period is measured from the point the overlay went away, not from when it appeared.</summary>
        public static void NotifyCaptureActivity()
        {
            Interlocked.Exchange(ref _lastCaptureTicks, DateTime.UtcNow.Ticks);
            NotifyInteraction();
        }

        public static TimeSpan TimeSinceInteraction => Elapsed(ref _lastInteractionTicks);

        public static TimeSpan TimeSinceCapture => Elapsed(ref _lastCaptureTicks);

        /// <summary>Time since the last keyboard or mouse input anywhere on the machine, or null on
        /// platforms that cannot report it.</summary>
        public static TimeSpan? GetSystemIdleTime()
        {
            try
            {
                if (OperatingSystem.IsWindows())
                    return GetSystemIdleTimeWindows();

                if (OperatingSystem.IsMacOS())
                    return GetSystemIdleTimeMacOS();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("IdleMonitor: failed to read system idle time: " + ex);
            }

            return null;
        }

        /// <summary>
        /// True when Clowd can be restarted without the user noticing or losing anything.
        /// <paramref name="reason"/> describes the blocker when it returns false, so the settings
        /// page can explain why a downloaded update is still waiting.
        /// </summary>
        public static bool IsGoodTimeToRestart(out string reason)
        {
            if (VideoCapturePage.ActiveInstance != null)
            {
                reason = "a recording is in progress";
                return false;
            }

            if (ScreenCapturePage.IsCaptureActive)
            {
                reason = "a capture is in progress";
                return false;
            }

            if (SessionManager.Current.Sessions.Any(s => s.ActiveUpload != null))
            {
                reason = "an upload is still running";
                return false;
            }

            if (EditorWindow.GetOpenEditors().Any()
                && SettingsRoot.Current?.Editor?.RestoreSessionsOnClowdStart != true)
            {
                // reopening the editors after the restart is what makes this non-destructive; without
                // session restore the update would silently throw away the user's open annotations.
                reason = "editor windows are open and are not set to reopen on start";
                return false;
            }

            if (TimeSinceCapture < IdleThreshold)
            {
                reason = "a capture window was open recently";
                return false;
            }

            if (GetSystemIdleTime() is { } systemIdle)
            {
                if (systemIdle < IdleThreshold)
                {
                    reason = "the computer is in use";
                    return false;
                }
            }
            else if (TimeSinceInteraction < IdleThreshold)
            {
                // no system-wide idle API here — fall back to how long ago Clowd itself was touched.
                reason = "Clowd was used recently";
                return false;
            }

            reason = null;
            return true;
        }

        [SupportedOSPlatform("windows")]
        private static TimeSpan? GetSystemIdleTimeWindows()
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref info))
                return null;

            // both are 32-bit millisecond tick counts that wrap roughly every 49 days; unsigned
            // subtraction gives the right answer across the wrap.
            var elapsed = unchecked((uint)Environment.TickCount - info.dwTime);
            return TimeSpan.FromMilliseconds(elapsed);
        }

        [SupportedOSPlatform("macos")]
        private static TimeSpan? GetSystemIdleTimeMacOS()
        {
            var seconds = CGEventSourceSecondsSinceLastEventType(CombinedSessionState, AnyInputEventType);
            return seconds < 0 ? null : TimeSpan.FromSeconds(seconds);
        }

        private static TimeSpan Elapsed(ref long ticksField)
        {
            var elapsed = DateTime.UtcNow - new DateTime(Interlocked.Read(ref ticksField), DateTimeKind.Utc);
            return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        // kCGEventSourceStateCombinedSessionState — input across the whole login session.
        private const int CombinedSessionState = 0;

        // kCGAnyInputEventType
        private const uint AnyInputEventType = 0xFFFFFFFF;

        [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
        private static extern double CGEventSourceSecondsSinceLastEventType(int sourceStateId, uint eventType);
    }
}

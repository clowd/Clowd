using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Prisc.Config;
using Prisc.Interop;
using Prisc.UI.VideoEditor;

namespace Prisc.UI
{
    /// <summary>
    /// Decides whether now is a quiet enough moment to restart Prisc underneath the user in order to
    /// apply a downloaded update (<see cref="Prisc.Config.SettingsGeneral.AutoApplyUpdates"/>).
    ///
    /// The primary signal is how long the *machine* has been without keyboard or mouse input — an
    /// update should land while the user is away from the computer, not merely while they happen not
    /// to be looking at Prisc. Windows and macOS both expose that directly
    /// (<see cref="GetSystemIdleTime"/>); anywhere else it is unavailable and the fallback is the
    /// last interaction with a Prisc window, which the windows themselves report in here.
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

        // seeded with "now" rather than DateTime.MinValue: a Prisc that has only just started is not
        // idle, and shouldn't restart itself moments after the user launched it.
        private static long _lastInteractionTicks = DateTime.UtcNow.Ticks;
        private static long _lastCaptureTicks = DateTime.UtcNow.Ticks;

        /// <summary>Called when the user does something in a Prisc window (editors, settings). Only
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
        /// platforms that cannot report it (<see cref="IdleTime"/>: GetLastInputInfo on Windows,
        /// CGEventSourceSecondsSinceLastEventType on macOS).</summary>
        public static TimeSpan? GetSystemIdleTime()
        {
            try
            {
                return IdleTime.SinceLastInput();
            }
            catch (InteropException ex) when (ex.Status == InteropStatus.Os)
            {
                // GetLastInputInfo refused (a disconnected RDP session, for one). That can repeat on
                // every poll while an update waits and was never reported, so it stays quiet: no
                // idle time, and IsGoodTimeToRestart falls back to the last Prisc interaction.
                Debug.WriteLine("IdleMonitor: the OS gave no idle time: " + ex.Message);
                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("IdleMonitor: failed to read system idle time: " + ex);
                SentryConfig.CaptureHandled(ex, "idle.read");
            }

            return null;
        }

        /// <summary>
        /// True when Prisc can be restarted without the user noticing or losing anything.
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

            if (ShareRegionPage.ActiveInstance != null)
            {
                // restarting would kill the mirror window a meeting app is bound to, and the
                // one-HWND invariant means it cannot be handed back afterwards: the user would have
                // to re-point their meeting app at a new window, mid-call, with no idea why.
                reason = "a region is being shared";
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

            if ((EditorWindow.GetOpenEditors().Any() || VideoEditorWindow.GetOpenEditors().Any())
                && SettingsRoot.Current?.Editor?.RestoreSessionsOnPriscStart != true)
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
                // no system-wide idle API here — fall back to how long ago Prisc itself was touched.
                reason = "Prisc was used recently";
                return false;
            }

            reason = null;
            return true;
        }

        private static TimeSpan Elapsed(ref long ticksField)
        {
            var elapsed = DateTime.UtcNow - new DateTime(Interlocked.Read(ref ticksField), DateTimeKind.Utc);
            return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
        }
    }
}

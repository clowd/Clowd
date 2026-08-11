using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia.Threading;
using Clowd.Config;
using Clowd.PlatformUtil;
using Clowd.UI.Helpers;

namespace Clowd.UI
{
    /// <summary>
    /// Supervises one scrolling capture. The overlay has already picked the region and the point
    /// the wheel will be aimed at and exited; from here this page owns the session directory
    /// until either the editor has the finished image or the directory is deleted.
    /// <para>Window-less like <see cref="VideoCapturePage"/>, and single-instance via
    /// <see cref="ActiveInstance"/> (UI thread only). Its job is small: put the border up, show
    /// the HUD if there is room for it outside the region, run the driver
    /// (<see cref="ScrollDriver"/>), and route whatever comes back. Every await lives inside a
    /// try/catch funnelling to <see cref="OnCriticalError"/> — an unhandled exception in an
    /// async void kills the process.</para>
    /// </summary>
    internal sealed class ScrollCapturePage : IScrollCapturePage
    {
        /// <summary>The scrolling capture currently running, if any. UI thread only; the app-exit
        /// path reaches the live run through this.</summary>
        internal static ScrollCapturePage ActiveInstance { get; private set; }

        private BorderWindow _border;
        private ScrollStatusWindow _status;
        private ScrollDriver _driver;
        private string _sessionDir;

        // the user asked for the run to be thrown away (or the app is exiting): the driver writes
        // nothing, so there is no outcome left to act on when it finally exits.
        private bool _cancelled;

        // this page has reached a terminal state and owns its own cleanup — suppresses the error
        // funnel and everything after an await.
        private bool _closing;

        public async void Open(ScreenRect region, ScreenPoint scrollPoint, long targetHwnd, string sessionDir)
        {
            try
            {
                Dispatcher.UIThread.VerifyAccess();

                if (ActiveInstance != null)
                {
                    // one scrolling capture at a time: two drivers would fight over the cursor and
                    // the foreground window. The fresh directory would otherwise leak — nothing
                    // else will ever look at it.
                    Debug.WriteLine("A scrolling capture is already running; ignoring the new one.");
                    CaptureSessionDispatcher.DeleteSessionDir(sessionDir);
                    return;
                }

                ActiveInstance = this;
                _sessionDir = sessionDir;

                // macOS drops synthetic scroll events from a process it does not trust, without
                // telling it: the driver would raise the target, photograph one frame, find that
                // nothing ever moved and report `no_movement`. It checks for itself and refuses
                // with a message, but asking here is what gets the user a button to the right
                // System Settings pane instead of a sentence about one.
                if (!await EnsureAccessibilityPermissionAsync())
                {
                    CaptureSessionDispatcher.DeleteSessionDir(_sessionDir);
                    Close();
                    return;
                }

                var binary = CaptureBinaryLocator.ResolveScrollDriver();
                if (binary == null)
                {
                    await NiceDialog.ShowNoticeAsync(null, NiceDialogIcon.Error,
                        $"The scrolling capture binary ({CaptureBinaryLocator.ScrollDriverFileName}) could not be found. " +
                        $"Run 'cargo build' in the clowd-rust repository, or set the {CaptureBinaryLocator.EnvVarName} " +
                        "environment variable to the capture binary's location — the driver ships beside it.",
                        "Scrolling capture unavailable");
                    CaptureSessionDispatcher.DeleteSessionDir(_sessionDir);
                    Close();
                    return;
                }

                // The frame is inflated strictly OUTSIDE the region (BorderWindow.ApplyGeometry),
                // so it can never appear in a captured frame. Its centered overlay text renders
                // *inside* the region, and unlike a recording — where the text is cleared before
                // frames start flowing — there is no safe window for it here: the driver's first
                // BitBlt lands a few hundred milliseconds after the spawn below. So this page
                // never calls SetOverlayText at all; the HUD says what is happening instead.
                _border = new BorderWindow(region);
                _border.Show();

                _status = new ScrollStatusWindow();
                _status.FinishClicked += (s, e) => Finish();
                _status.CancelClicked += (s, e) => Cancel();
                if (!_status.TryShowNear(region))
                {
                    // Every placement overlapped the region and would have been stitched into the
                    // result. Esc (polled inside the driver) and the automatic end detection still
                    // finish the run; the user just loses the progress readout.
                    Debug.WriteLine("No room outside the region for the scrolling capture HUD; running without it.");
                    // guarded like every other Close() here: this one is closing a window that was
                    // never shown, and a HUD we decided not to display must not be able to fail the
                    // capture it was only ever going to annotate.
                    try { _status.Close(); }
                    catch { }
                    _status = null;
                }

                _driver = new ScrollDriver();
                _driver.StatusReceived += OnDriverStatus;

                var outcome = await _driver.RunAsync(binary, sessionDir, region, scrollPoint, targetHwnd,
                                                     SettingsRoot.Current.Capture.ScrollCaptureRewindToTop);

                // app exit took the run down while we were waiting; it has already cleaned up.
                if (_closing)
                    return;

                // a driver that saw the cancel in time writes nothing at all, so there is only an
                // empty directory to remove.
                if (DiscardIfCancelled(null))
                    return;

                HideWindows();

                if (outcome.ExitCode == 0 && IsSessionResult(outcome.Result))
                {
                    // the driver wrote session.json last, so the directory is complete: this is
                    // the same call the screenshot overlay's own completion makes, and it comes
                    // back as an Edit action with the session loaded and registered.
                    var result = CaptureSessionDispatcher.ProcessFinishedSession(_sessionDir);

                    // loading registered the session with SessionManager, which is the first thing
                    // a cancel would have to undo; re-check before any of it becomes visible.
                    if (DiscardIfCancelled(result?.Session))
                        return;

                    if (result?.Session != null)
                    {
                        result.Session.Name = "Scrolling Capture";
                        EditorWindow.ShowSession(result.Session);
                        ShowOutcomeToast(outcome.Result);
                        Close();
                        return;
                    }

                    // it said it had written a session and had not — the directory is either gone
                    // (ProcessFinishedSession deletes what it cannot use) or unusable either way.
                    OnCriticalError("The scrolling capture finished but no image was saved.", null, outcome);
                    return;
                }

                OnCriticalError(DescribeFailure(outcome), null, outcome);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to run the scrolling capture: " + ex);
                if (!_closing)
                    OnCriticalError(ex.Message, ex, null);
            }
        }

        /// <summary>
        /// Returns whether the run may go ahead. True on every platform without an Accessibility
        /// permission; on macOS the first attempt is where the OS may still have its own one-tap
        /// prompt to offer, and every later one falls through to the dialog, since by then the
        /// only route left is System Settings plus a restart. Mirrors
        /// <c>ScreenCaptureService.EnsureScreenRecordingPermissionAsync</c>, which gates the
        /// overlay on the other of the two permissions.
        /// </summary>
        private static async Task<bool> EnsureAccessibilityPermissionAsync()
        {
            if (MacPermissions.HasAccessibility || MacPermissions.Request(MacPermission.Accessibility))
                return true;

            var openSettings = await NiceDialog.ShowDialogAsync(null, NiceDialogIcon.Warning,
                "Clowd needs Accessibility permission to scroll the window for you.\n\n"
                + "Enable Clowd under Privacy & Security → Accessibility, then restart Clowd.",
                "Accessibility permission required", "Open System Settings", "Cancel");

            if (openSettings)
                MacPermissions.OpenSettings(MacPermission.Accessibility);

            return false;
        }

        /// <summary>Stops the run and keeps everything captured so far (the HUD's FINISH button —
        /// Esc reaches the driver directly, which polls it while the target holds focus).</summary>
        private void Finish()
        {
            if (_closing || _cancelled)
                return;

            _status?.SetStatus("Finishing…", "Stitching the last frames");
            _driver?.Send(ScrollDriverCommand.Stop);
        }

        /// <summary>Throws the whole run away. The windows go immediately for feedback; the
        /// directory is deleted once the driver has actually exited, so the delete cannot race a
        /// process that is still writing into it.</summary>
        private void Cancel()
        {
            if (_closing || _cancelled)
                return;

            _cancelled = true;
            HideWindows();
            _driver?.Send(ScrollDriverCommand.Cancel);
            WatchCancelledDriver();
        }

        /// <summary>
        /// Honours a cancel that arrived while the run was already finishing. Reaching CANCEL
        /// means moving the cursor onto the HUD, which pauses the driver rather than ending it —
        /// but a cancel can still land in the window between the driver deciding it is done and
        /// this page hearing about it. It wins whenever it does: nothing the run produced is shown
        /// or kept. A session that
        /// <see cref="CaptureSessionDispatcher.ProcessFinishedSession"/> already loaded is
        /// unregistered along with its directory, or the directory alone when there is none.
        /// </summary>
        private bool DiscardIfCancelled(SessionInfo session)
        {
            if (!_cancelled)
                return false;

            if (session != null)
            {
                try
                {
                    // removes it from the recents list and deletes the directory in one step; no
                    // editor holds it yet, which is the only case this refuses.
                    SessionManager.Current.DeleteSession(session);
                    Close();
                    return true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Failed to discard a cancelled scrolling capture session: " + ex);
                }
            }

            CaptureSessionDispatcher.DeleteSessionDir(_sessionDir);
            Close();
            return true;
        }

        /// <summary>
        /// Backstop for a driver that never acts on the cancel — a wedged Win32 call, a lost
        /// display, or a stdin write that failed so the command never arrived. Everything is
        /// already hidden at this point, so without this the run would go on scrolling and
        /// photographing the user's window invisibly, <see cref="ActiveInstance"/> would stay set
        /// for the rest of the app session, and every later scrolling capture would be silently
        /// discarded. <see cref="ScrollDriver.ShutdownAsync"/> is idempotent, so racing the
        /// shutdown <see cref="ScrollDriver.RunAsync"/> performs on its own way out is harmless.
        /// </summary>
        private async void WatchCancelledDriver()
        {
            // the driver polls the cancel flag at the top of each step and once more after
            // settling, so a worst-case settle cycle plus the write is well inside this.
            var grace = TimeSpan.FromSeconds(4);
            var driver = _driver;

            try
            {
                await Task.Delay(grace);

                // the run resolved on its own and this page has already cleaned up after it.
                if (_closing || driver == null)
                    return;

                Debug.WriteLine("The scrolling capture driver did not exit after being cancelled; killing it.");
                await driver.ShutdownAsync();
            }
            catch (Exception ex)
            {
                // async void: nothing is awaiting this, so an escaping exception would take the
                // process down. A failed backstop is not worth a dialog either — the cancel path
                // in Open still owns the outcome — so it is reported and dropped here rather than
                // funnelled through OnCriticalError.
                Debug.WriteLine("The scrolling capture cancel watchdog failed: " + ex);
                SentryConfig.CaptureHandled(ex, "scroll.cancel-watchdog");
            }
        }

        /// <summary>
        /// App-exit path. A scrolling capture has nothing partial worth keeping — the composite
        /// only exists inside the driver until it finishes — so this cancels rather than stops:
        /// the driver writes nothing and the directory goes with it. Without this the driver
        /// would keep scrolling someone's window after Clowd is gone (stdin EOF would eventually
        /// stop it, but only once our process actually dies) and leave a session nobody routes.
        /// Never shows UI; the app is exiting.
        /// </summary>
        internal async Task ShutdownAsync()
        {
            if (_closing)
                return;
            _closing = true; // suppresses the error funnel; cleanup is handled inline
            _cancelled = true;

            HideWindows();

            try
            {
                if (_driver != null)
                    await _driver.ShutdownAsync();

                CaptureSessionDispatcher.DeleteSessionDir(_sessionDir);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error shutting down the scrolling capture: " + ex);
                SentryConfig.CaptureHandled(ex, "scroll.shutdown");
            }

            Close();
        }

        /// <summary>
        /// The run failed: no session was produced, or one was claimed and is not there. Deletes
        /// the directory, reports the failure once (with the driver's log attached) and tells the
        /// user. Every failure path in this page ends here, including exceptions out of the async
        /// void entry point.
        /// </summary>
        private async void OnCriticalError(string message, Exception detail, ScrollDriverOutcome outcome)
        {
            try
            {
                if (_closing)
                    return;
                _closing = true;

                Debug.WriteLine("Scrolling capture failed: " + message);
                HideWindows();

                // the driver may still be alive (a failure raised from this side): nothing may be
                // holding the directory open when it is deleted.
                if (_driver != null)
                    await _driver.ShutdownAsync();

                CaptureSessionDispatcher.DeleteSessionDir(_sessionDir);

                // The reported message is kept constant so every scroll-driver death groups into
                // one Sentry issue; the specifics ride along in Data (the same convention
                // ScreenCapturePage uses for a capturer crash).
                var reported = detail ?? new InvalidOperationException("Scrolling capture failed");
                AttachDiagnostics(reported, message, outcome);
                SentryConfig.CaptureHandled(reported, "capture.scroll");

                await NiceDialog.ShowNoticeAsync(null, NiceDialogIcon.Error, message, "Scrolling capture failed");

                Close();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error handling a scrolling capture failure: " + ex);
                SentryConfig.CaptureHandled(ex, "scroll.handle-failure");
                Close();
            }
        }

        private void OnDriverStatus(object sender, ScrollProgress progress)
        {
            if (_closing || _cancelled)
                return;

            _status?.SetStatus(
                $"Frame {progress.Frames.ToString("N0", CultureInfo.CurrentCulture)} · {progress.HeightPx.ToString("N0", CultureInfo.CurrentCulture)} px",
                DescribeState(progress));
        }

        /// <summary>True for the outcomes that leave a finished session on disk. Everything the
        /// driver can end with does, except an outright failure — a run that was stopped early,
        /// capped, or never moved is still a picture the user asked for.</summary>
        private static bool IsSessionResult(string result) => result is
            ScrollDriverResult.Complete or
            ScrollDriverResult.Stopped or
            ScrollDriverResult.MaxReached or
            ScrollDriverResult.NoMovement;

        private static string DescribeState(ScrollProgress progress) => progress.State switch
        {
            // Says what it is doing, not just that it is busy: the rewind can
            // run for several seconds before the first frame is captured, and
            // an unexplained pause with a frame count of zero reads as a hang.
            "rewinding" => "Scrolling to the top…",
            // The driver pauses whenever the cursor leaves the scroll point, so
            // this is the one state the user is holding in place themselves —
            // it has to say what ends it, or a frame counter that has stopped
            // advancing reads as a hang.
            "paused" => "Paused — stop moving the mouse to resume",
            // …and once they have stopped, the driver counts down rather than
            // yanking the cursor back unannounced. Moving again reverts to
            // "paused" above, so the readout always matches what the mouse is
            // actually doing.
            "resuming" => $"Resuming in {progress.ResumeInS.ToString(CultureInfo.CurrentCulture)}…",
            "scrolling" => "Scrolling…",
            "settling" => "Waiting for the page…",
            "stitching" => "Stitching…",
            _ => "Esc or FINISH to stop",
        };

        /// <summary>Explains an outcome that is not simply "here is your image". Only the two
        /// surprising ones get a toast: a capture that stopped because it hit a limit, and one
        /// that produced a single screen because nothing scrolled.</summary>
        private static void ShowOutcomeToast(string result)
        {
            var message = result switch
            {
                ScrollDriverResult.MaxReached =>
                    "The scrolling capture hit its length limit — everything up to that point was kept.",
                ScrollDriverResult.NoMovement => OperatingSystem.IsWindows()
                    ? "That window did not scroll, so only one screen was captured. Windows running as administrator ignore synthetic scrolling."
                    : "That window did not scroll, so only one screen was captured. Try aiming the scroll point at a part of the page that scrolls.",
                _ => null,
            };

            if (message == null)
                return;

            // null-owner tolerant: with no window open there is simply nowhere to host a toast,
            // and the editor this capture just opened is normally the host.
            var host = Toast.GetActiveOrMainWindow();
            if (host != null)
                Toast.Show(host, message);
        }

        /// <summary>Turns a failed outcome into something worth putting in a dialog. The driver's
        /// own message wins when it managed to send one; otherwise all we know is how it died.</summary>
        private static string DescribeFailure(ScrollDriverOutcome outcome)
        {
            if (!String.IsNullOrWhiteSpace(outcome?.Message))
                return outcome.Message;

            if (outcome?.Result == ScrollDriverResult.Failed)
                return "The scrolling capture tool could not produce an image.";

            if (outcome?.ExitCode is int code && code != 0)
                return $"The scrolling capture tool exited unexpectedly (code {code}).";

            return "The scrolling capture tool stopped without producing an image.";
        }

        private void AttachDiagnostics(Exception ex, string message, ScrollDriverOutcome outcome)
        {
            try
            {
                ex.Data["detail"] = message;
                ex.Data["result"] = outcome?.Result ?? "none";
                ex.Data["exit_code"] = outcome?.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
                ex.Data["frames"] = outcome?.Frames ?? 0;

                var log = _driver?.GetLogTail();
                if (log != null && !ex.Data.Contains(SentryConfig.ProcessLogKey))
                    ex.Data[SentryConfig.ProcessLogKey] = log;
            }
            catch
            {
                // Exception.Data may be read-only for exotic exception types
            }
        }

        private void HideWindows()
        {
            try { _border?.Hide(); }
            catch { }
            try { _status?.Hide(); }
            catch { }
        }

        private void Close()
        {
            _closing = true;

            if (ReferenceEquals(ActiveInstance, this))
                ActiveInstance = null;

            if (_driver != null)
                _driver.StatusReceived -= OnDriverStatus;

            try { _border?.Close(); }
            catch { }
            try { _status?.Close(); }
            catch { }
            _border = null;
            _status = null;
        }
    }
}

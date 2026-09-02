using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Threading;
using Clowd.Config;
using Clowd.PlatformUtil;
using Clowd.UI.Helpers;

namespace Clowd.UI
{
    /// <summary>
    /// Orchestrates a "share region" session: hosts the <see cref="ShareRegionDriver"/> process,
    /// which mirrors a rectangle of the screen into an ordinary top-level window that a meeting app
    /// (Teams, Zoom, Meet, …) can share, and puts Clowd's own chrome around it — the click-through
    /// <see cref="BorderWindow"/> so the user can see what is being broadcast, and a three-tile
    /// <see cref="FloatingToolbarWindow"/> (DRAG ME / BLUR / CANCEL) so they can obscure it or stop.
    /// <para>The third sibling of <see cref="VideoCapturePage"/> and <see cref="ScrollCapturePage"/>
    /// and built the same way: window-less, single-instance via <see cref="ActiveInstance"/> (UI
    /// thread only), with every public entry point an <c>async void</c> whose awaits sit inside a
    /// try/catch funnelling to <see cref="OnCriticalError"/> — an unhandled exception out of an
    /// async void kills the process. <see cref="_closing"/> is a one-way latch: every terminal path
    /// sets it, and every path rechecks it after an await, because a CANCEL or an app exit can land
    /// in any of those gaps.</para>
    /// <para>A share and a recording deliberately do NOT exclude each other. They are separate
    /// helper processes with separate single-instance guards, and sharing a region while recording
    /// it (or recording while sharing) is a perfectly sensible thing to want; neither page knows
    /// about the other.</para>
    /// </summary>
    internal sealed class ShareRegionPage : IShareRegionPage
    {
        /// <summary>The share session currently running, if any. UI thread only; the app-exit path
        /// reaches the live session through this, and <see cref="App.ToggleShareRegion"/> uses it to
        /// decide whether the entry point starts a share or ends one.</summary>
        internal static ShareRegionPage ActiveInstance { get; private set; }

        // Everything the helper is spawned with is frozen here, deliberately as constants rather
        // than settings. The wire protocol has no "configure": there is no way to change the title,
        // the frame rate or the cursor policy of a running helper, and the process can never be
        // respawned to pick up a new value — the meeting app's share is bound to the mirror
        // window's HWND, so a second process is a window nobody is watching (the one-HWND
        // invariant, see ShareRegionDriver's class docs). A setting that could only take effect on
        // the *next* share would be a setting that silently does nothing, so there isn't one.
        private const string MirrorWindowTitle = "Clowd Shared Region";
        private const int MirrorFps = 30;
        private const bool MirrorCaptureCursor = true;

        // How strong a blur the BLUR tile asks for. The tile is a plain on/off toggle — there is no
        // UI for picking a strength — so this is simply "obviously blurred, still recognisable as
        // your own screen", which is what a presenter wants while they type a password.
        private const int BlurStrength = 50;

        private ShareRegionDriver _driver;
        private BorderWindow _border;
        private FloatingToolbarWindow _toolbar;

        // The region actually being mirrored. Seeded from the overlay's selection and replaced by
        // what the helper reports it applied (it forces each side to at least 64 px and to an even
        // number), so the border frames the pixels that are really in the meeting.
        private ScreenRect _region;

        // The handshake settled as "started": the helper is mirroring and the session UI is up.
        // Until then there is nothing of Clowd's on screen at all.
        private bool _sharing;

        // Terminal-state latch: this page owns its own cleanup from here, so the error funnel and
        // everything after an await must stand down.
        private bool _closing;

        // The helper exited between the handshake resolving and the session UI being wired up — a
        // window of a few dispatcher frames, but a real one for a helper that dies immediately.
        // Stashed rather than acted on, because tearing down UI that has not been built yet would
        // leave the border and toolbar behind; ShowSessionUi drains it.
        private ShareSessionEnded _pendingEnd;

        public async void Open(ScreenRect region)
        {
            try
            {
                Dispatcher.UIThread.VerifyAccess();

                if (ActiveInstance != null)
                {
                    // One share at a time. Unlike a recording there is no session directory to
                    // clean up here — a share writes no files — so this simply declines, leaving
                    // the live session and its mirror window completely undisturbed.
                    Debug.WriteLine("A region share is already active; ignoring the new one.");
                    return;
                }

                ActiveInstance = this;
                _region = region;

                var binary = ShareRegionDriver.ResolveBinary();
                if (binary == null)
                {
                    await NiceDialog.ShowNoticeAsync(null, NiceDialogIcon.Error,
                        $"The region sharing binary ({ShareRegionDriver.BinaryFileName}) could not be found. " +
                        $"Run 'cargo build' in the obs-express-rs repository, or set the {ShareRegionDriver.EnvVarName} " +
                        "environment variable to its location.",
                        "Region sharing unavailable");
                    Close();
                    return;
                }

                Debug.WriteLine("Resolved region sharing binary: " + binary);

                _driver = new ShareRegionDriver();
                _driver.ObscureChanged += OnObscureChanged;
                _driver.StatusReceived += OnStatusReceived;
                _driver.CommandError += OnCommandError;
                _driver.Ended += OnEnded;

                // Spawned while Clowd still holds the foreground rights the capture overlay handed
                // back to it on its way out (--shell-pid, CAPTURE_PROTOCOL.md §2.5). That matters:
                // the helper's prompt window has to come to the front to be findable, and a process
                // spawned by a background app is refused the foreground — the prompt would only
                // blink in the taskbar and the user would never see the thing they are meant to pick.
                await _driver.InitializeAsync(region, MirrorWindowTitle, MirrorCaptureCursor, MirrorFps, binary);

                if (_closing)
                    return;

                // The helper is now showing its OWN "Share this window" prompt, and Clowd shows
                // nothing whatsoever: no border, no toolbar. The user is looking at that dialog and
                // at their meeting app's share picker, and a Clowd frame drawn around a region
                // nobody is broadcasting yet would only be one more thing to interpret. This wait
                // has no timeout by design — walking a Teams or Zoom picker can take minutes.
                var decision = await _driver.WaitForDecisionAsync();

                if (_closing)
                    return;

                switch (decision)
                {
                    case ShareHandshake.Started:
                        ShowSessionUi();
                        break;

                    case ShareHandshake.Cancelled:
                        // The user closed the prompt or pressed Escape: they backed out before
                        // anything was shared, nothing of ours was ever shown, and there is no file
                        // and no state to reconcile. Anything said here would be a dialog reporting
                        // that the user did what they just did.
                        Debug.WriteLine("The region share was cancelled at the helper's prompt.");
                        Close();
                        break;

                    default:
                        // Failed: the process died before the user answered. This is the one
                        // handshake outcome that must never be silent — the prompt window vanished
                        // on its own and the user is left wondering where their share went.
                        OnCriticalError("The region sharing tool stopped before the share could start.", null);
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to open the region share: " + ex);
                SentryConfig.CaptureHandled(ex, "share.open");
                if (!_closing)
                    OnCriticalError(ex.Message, ex);
            }
        }

        /// <summary>
        /// The user pressed OK on the helper's prompt and their meeting app is now watching the
        /// mirror window: put Clowd's chrome up around the region.
        /// </summary>
        private void ShowSessionUi()
        {
            // set before anything can raise an event: from here the helper's acks and its exit are
            // this session's to act on, not the handshake's.
            _sharing = true;

            // The helper normalises the region it was asked for (each side forced to at least 64 px
            // and to an even number) and its acks always report what it ACTUALLY applied. Frame that
            // rather than what we asked for, or the border quietly lies about which pixels are in
            // the meeting.
            _region = ToScreenRect(_driver?.AppliedRegion) ?? _region;

            _border = new BorderWindow(_region);
            // Deliberately NO SetOverlayText anywhere in this page. The border's frame is inflated
            // strictly outside the region, but its overlay text renders INSIDE it — i.e. straight
            // into the pixels being mirrored to everyone in the meeting. The toolbar's drag-handle
            // label, which sits outside the region, is where this session says anything at all.
            _border.Show();

            _toolbar = new FloatingToolbarWindow(FloatingToolbarProfile.ShareRegion);
            _toolbar.BlurToggled += (s, on) => OnBlurToggled(on);
            _toolbar.CancelClicked += (s, e) => Cancel();
            // The toolbar has no clock of its own and this page deliberately does not grow one: an
            // elapsed timer would need a DispatcherTimer whose only job is to say how long a thing
            // that is plainly still happening has been happening. The helper's own status line is
            // free and says something the user cannot otherwise see, so the label carries that; it
            // reads "SHARING" until the first status arrives about a second in.
            _toolbar.SetStatusText("SHARING");
            _toolbar.ShowNear(_region);

            // The GPU effect can fail to build before the toolbar exists (the helper emits its
            // unsolicited obscure/none as soon as it happens, which may be before or after the
            // handshake), so the tile is retired here rather than only from the ack handler.
            if (_driver != null && !_driver.BlurAvailable)
                _toolbar.SetBlurAvailable(false);

            // …and the process itself may already be gone. See _pendingEnd.
            if (_pendingEnd is { } ended)
            {
                _pendingEnd = null;
                OnShareStopped(ended);
            }
        }

        /// <summary>
        /// The BLUR tile was pressed. The tile has already flipped itself — it is optimistic — but
        /// the helper's <c>obscure</c> ack is the only confirmation there is, and
        /// <see cref="OnObscureChanged"/> is what makes the tile agree with the process actually
        /// drawing the frames.
        /// </summary>
        private void OnBlurToggled(bool on)
        {
            if (_closing)
                return;

            _driver?.SetObscure(on ? ShareObscureMode.Blur : ShareObscureMode.None, BlurStrength);
        }

        /// <summary>
        /// The helper acknowledged an obscure state. Two kinds arrive here: the ack for a toggle the
        /// user just made, and the UNSOLICITED <c>obscure/none</c> the helper sends when its GPU
        /// effect fails to build. That failure is permanent for the life of the process — it never
        /// tries to build the effect again — so the second kind retires the tile rather than merely
        /// showing it off, and the state carried by the event (not the driver property, which the
        /// pump thread may already have moved on) is what decides which one this is.
        /// </summary>
        private void OnObscureChanged(object sender, ShareObscureState state)
        {
            if (_closing)
                return;

            _toolbar?.SetBlurEnabled(state.Mode != ShareObscureMode.None);

            if (state.Unsolicited && !state.BlurAvailable)
            {
                Debug.WriteLine("The region sharing helper retracted its obscure effect; retiring the BLUR tile.");
                // also unlights the tile and blips "NO BLUR" on the drag handle. Never a dialog:
                // this can land in the middle of a meeting the user is presenting to.
                _toolbar?.SetBlurAvailable(false);
            }
        }

        /// <summary>Once-a-second frame rate report from the helper — the only evidence the user has
        /// that the mirror is still live, so it goes on the drag-handle label.</summary>
        private void OnStatusReceived(object sender, double fps)
        {
            if (_closing)
                return;

            _toolbar?.SetStatusText(fps.ToString("F0", CultureInfo.CurrentCulture) + " FPS");
        }

        /// <summary>
        /// The helper rejected a command. Logged and dropped: every command this page sends is a
        /// blur toggle, the tile's next ack (or the lack of one) is already the user-visible answer,
        /// and a modal raised over a live presentation is worse than the thing it is reporting.
        /// </summary>
        private void OnCommandError(object sender, string message)
        {
            Debug.WriteLine("The region sharing helper rejected a command: " + message);
        }

        /// <summary>
        /// The helper process is gone. This is the ONLY signal that a running share stopped — the
        /// protocol has no terminal line after <c>sharing_started</c>; the process simply exits and
        /// the pipe closes — so ObsCapturer's rule that an exit without a terminal message is fatal
        /// must NOT be applied here. An exit code of 0 after the share started is the normal end of
        /// a share.
        /// </summary>
        private void OnEnded(object sender, ShareSessionEnded ended)
        {
            if (_closing)
                return;

            // Cancelled/Failed belong to the handshake, which Open is awaiting and owns; acting on
            // them here as well would double-report the failure.
            if (ended.Handshake != ShareHandshake.Started)
                return;

            if (!_sharing)
            {
                // the decision has resolved but ShowSessionUi has not run yet; it will drain this.
                _pendingEnd = ended;
                return;
            }

            OnShareStopped(ended);
        }

        /// <summary>
        /// A share that was actually running has ended without us asking it to — the helper crashed,
        /// the user closed the mirror window, or the meeting app took it down. Either way the
        /// meeting is no longer receiving anything, and staying silent would leave the user
        /// presenting a dead window with no way to find that out.
        /// </summary>
        private async void OnShareStopped(ShareSessionEnded ended)
        {
            try
            {
                if (_closing)
                    return;

                if (ended.ExitCode != 0)
                {
                    OnCriticalError(
                        $"The region sharing tool exited unexpectedly (code {ended.ExitCode.ToString(CultureInfo.InvariantCulture)}).",
                        null);
                    return;
                }

                _closing = true;
                Debug.WriteLine("The region share ended on its own (exit code 0).");
                HideWindows();

                await NiceDialog.ShowNoticeAsync(null, NiceDialogIcon.Information,
                    "Region sharing stopped. Your meeting app is no longer receiving the region.",
                    "Region sharing stopped");

                Close();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error handling the end of a region share: " + ex);
                SentryConfig.CaptureHandled(ex, "share.ended");
                Close();
            }
        }

        /// <summary>
        /// Ends the session at the user's request: the CANCEL tile, and the Share Region tray item
        /// / hotkey firing a second time (<see cref="App.ToggleShareRegion"/>) — a toggle that
        /// starts a share has to be able to end one, and "end it" is the only other thing a live
        /// share can be told to do. No dialog either way: they asked for this, and the windows and
        /// the mirror going away is the confirmation.
        /// </summary>
        internal async void Cancel()
        {
            try
            {
                if (_closing)
                    return;
                _closing = true; // also suppresses the Ended handler for the exit we are causing

                HideWindows();

                // Awaited rather than fire-and-forget so the mirror window is really gone before
                // this returns — a meeting app left holding a stale window is exactly the confusion
                // CANCEL exists to end.
                if (_driver != null)
                    await _driver.DisposeAsync();

                Close();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error cancelling the region share: " + ex);
                SentryConfig.CaptureHandled(ex, "share.cancel");
                Close();
            }
        }

        /// <summary>
        /// App-exit path, mirroring <see cref="VideoCapturePage.ShutdownAsync"/>. Without it the
        /// helper would outlive Clowd: stdin EOF does eventually stop it, but only once our process
        /// has actually died, so a meeting would keep receiving a frozen (or still-live) mirror of
        /// the user's screen from an app they just closed. Guarded by <see cref="_closing"/> so a
        /// second call is a no-op, and never shows UI — the app is exiting.
        /// </summary>
        internal async Task ShutdownAsync()
        {
            if (_closing)
                return;
            _closing = true; // suppresses the error funnel; cleanup is handled inline

            HideWindows();

            try
            {
                if (_driver != null)
                    await _driver.DisposeAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error shutting down the region share: " + ex);
                SentryConfig.CaptureHandled(ex, "share.shutdown");
            }

            Close();
        }

        /// <summary>
        /// The session failed: the helper died before the user answered its prompt, it exited
        /// non-zero, or an entry point threw. Writes the helper's log OUTSIDE any session directory
        /// (there isn't one — a share produces no files — so it goes beside the settings file with
        /// a Desktop fallback, the same shape <see cref="VideoCapturePage"/> uses), reports it once
        /// and tells the user. Every failure path in this page ends here.
        /// </summary>
        private async void OnCriticalError(string message, Exception detail)
        {
            try
            {
                if (_closing)
                    return;
                _closing = true;

                Debug.WriteLine("Region sharing failed: " + message);
                HideWindows();

                var logPath = WriteErrorLog(message);

                // the helper may still be alive (a failure raised from this side, or a wedged
                // process): the mirror window must not survive the session that owned it.
                if (_driver != null)
                    await _driver.DisposeAsync();

                // The reported message is kept constant so every helper death groups into a single
                // Sentry issue; the specifics ride along in Data, which is the convention
                // ScrollCapturePage and ScreenCapturePage both use for a helper crash.
                var reported = detail ?? new InvalidOperationException("Region sharing failed");
                AttachDiagnostics(reported, message);
                SentryConfig.CaptureHandled(reported, "capture.share");

                if (logPath != null)
                {
                    if (await NiceDialog.ShowPromptAsync(null, NiceDialogIcon.Error, message,
                                                        "Region sharing failed", "Open Error Log"))
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo(logPath) { UseShellExecute = true });
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine("Failed to open error log: " + ex.Message);
                            SentryConfig.CaptureHandled(ex, "share.open-error-log");
                        }
                    }
                }
                else
                {
                    await NiceDialog.ShowNoticeAsync(null, NiceDialogIcon.Error, message, "Region sharing failed");
                }

                Close();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error handling a region sharing failure: " + ex);
                SentryConfig.CaptureHandled(ex, "share.handle-failure");
                Close();
            }
        }

        /// <summary>Writes the helper's ring-buffer log next to the settings file (%APPDATA%\Clowd),
        /// falling back to the Desktop — the same place a failed recording leaves its log, and
        /// deliberately not a session directory, because a share never has one. Returns null on
        /// failure, which only costs the "Open Error Log" button.</summary>
        private string WriteErrorLog(string message)
        {
            try
            {
                string dir = null;
                try
                {
                    dir = Path.GetDirectoryName(SettingsService.FilePath);
                }
                catch { }

                if (String.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                    dir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

                var logPath = Path.Combine(dir, $"share_error_log_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                File.WriteAllText(logPath, message + Environment.NewLine + Environment.NewLine + (_driver?.GetLog() ?? ""));
                return logPath;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to write the region sharing error log: " + ex.Message);
                SentryConfig.CaptureHandled(ex, "share.write-error-log");
                return null;
            }
        }

        private void AttachDiagnostics(Exception ex, string message)
        {
            try
            {
                ex.Data["detail"] = message;
                ex.Data["sharing"] = _sharing;
                ex.Data["region"] = _region.ToString();

                var log = _driver?.GetLog();
                if (!String.IsNullOrEmpty(log) && !ex.Data.Contains(SentryConfig.ProcessLogKey))
                    ex.Data[SentryConfig.ProcessLogKey] = log;
            }
            catch
            {
                // Exception.Data may be read-only for exotic exception types
            }
        }

        /// <summary>The region the helper reported, in the shell's own rect type. Both sides speak
        /// capture space (physical px on the Windows virtual desktop, x/y possibly negative), so
        /// this is a re-wrapping and never a conversion. Null in, null out — the helper has said
        /// nothing yet and the caller keeps what it asked for.</summary>
        private static ScreenRect ToScreenRect(ShareRegionRect rect) =>
            rect == null ? null : new ScreenRect(rect.X, rect.Y, rect.Width, rect.Height);

        private void HideWindows()
        {
            try { _border?.Hide(); }
            catch { }
            try { _toolbar?.Hide(); }
            catch { }
        }

        private void Close()
        {
            _closing = true;

            if (ReferenceEquals(ActiveInstance, this))
                ActiveInstance = null;

            if (_driver != null)
            {
                _driver.ObscureChanged -= OnObscureChanged;
                _driver.StatusReceived -= OnStatusReceived;
                _driver.CommandError -= OnCommandError;
                _driver.Ended -= OnEnded;
                // Safety net for the paths that reach here without having awaited a shutdown (the
                // binary-missing bail-out, a cancelled handshake). Idempotent and memoized, so
                // racing a shutdown another path already started is a no-op.
                _driver.Dispose();
            }

            try { _border?.Close(); }
            catch { }
            try { _toolbar?.Close(); }
            catch { }
            _border = null;
            _toolbar = null;
        }
    }
}

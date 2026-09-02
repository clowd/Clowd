using System;
using System.ComponentModel;
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
    /// <see cref="BorderWindow"/> so the user can see what is being broadcast, and a four-tile
    /// <see cref="FloatingToolbarWindow"/> (DRAG ME / HIDE / RESIZE / CANCEL) so they can obscure
    /// it, change which pixels it covers, or stop.
    /// <para>Resize mode is a small explicit state machine (<see cref="ResizeState"/>) that lives
    /// here and nowhere else. The toolbar only latches a tile and raises an event, and
    /// <see cref="ShareResizeWindow"/> only owns the pending rectangle and the drag; this page owns
    /// the sequencing that makes the mode safe to run over a live meeting — obscuring the region
    /// BEFORE any of Clowd's chrome is drawn inside it, writing exactly one <c>move</c> on the way
    /// out (acks carry no request id, so a second one in flight could not be told from the first),
    /// and restoring afterwards exactly the obscure state the user had before it started.</para>
    /// <para>The border and the overlay are a SWAP, never a stack (spec addendum 8.1): the border
    /// is hidden for exactly as long as the overlay is up, the overlay draws the frame in its
    /// place, and the border comes back on the region the helper ACTUALLY applied. Sequencing that
    /// is this page's job, and the rule it holds is that a live session always has exactly one of
    /// the two marking the region — <see cref="RestoreBorder"/> is on every way out of the mode,
    /// including the ones nobody asked for (a refused move, the backstop timer, Esc). The overlay
    /// is also the one surface in a share session that takes focus (addendum 8.2), which is what
    /// makes Esc possible and why the toolbar is re-raised above it: the tile that ends the mode
    /// must not end up underneath the window it started.</para>
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

        // What the helper is spawned with. The wire protocol has no "configure": nothing here can be
        // changed on a RUNNING helper, and the process can never be respawned to pick up a new value
        // — the meeting app's share is bound to the mirror window's HWND, so a second process is a
        // window nobody is watching (the one-HWND invariant, see ShareRegionDriver's class docs).
        // Every value below is therefore fixed for the life of a session, whatever its source.
        //
        // That argument is specifically about SPAWN-TIME parameters, and it does not generalise to
        // everything about a share. The obscure mode and strength, and the region itself, are live
        // stdin commands the protocol accepts on a running helper (obscure / unobscure / move), so
        // they take effect on the share the user is looking at rather than the next one. Those are
        // therefore legitimately settings, and they are: see ConfiguredObscure and
        // OnShareSettingChanged, which pushes an edit made mid-share straight down the pipe.
        //
        // Frame rate is the deliberate exception, and it is worth being precise about why it does
        // not break the rule. It IS a spawn-time parameter — the canvas is built once, during
        // Mirror::bootstrap, and no stdin command resizes it — so it cannot reach a running share.
        // What makes it a legitimate setting anyway is that its effect is real and predictable on
        // the NEXT share rather than nonexistent, and that SettingsShareRegion.Fps says so in
        // the description the user reads. The title and the cursor flag stay frozen because neither
        // has an answer that good: a per-session window title would change the string the user hunts
        // for in their meeting app's picker, and the cursor belongs in the picture of a shared region
        // essentially always.
        private const string MirrorWindowTitle = "Clowd Shared Region";
        private const bool MirrorCaptureCursor = true;

        /// <summary>The mirror's canvas frame rate for THIS session, read once at spawn (see the
        /// note above). Held rather than re-read so a mid-share edit cannot make the page disagree
        /// with the helper it actually started — the running canvas is whatever it was built with.
        /// Falls back to 30 when the settings graph is unavailable, which is the same default
        /// <c>SettingsShareRegion.Fps</c> and the helper's own <c>--fps</c> both carry.</summary>
        private int _mirrorFps = 30;

        /// <summary>
        /// Where the resize mode stands. Deliberately a four-state enum rather than a pair of
        /// booleans: the two waiting states exist because both ends of the mode are asynchronous —
        /// entry waits for the helper to confirm the region is obscured before any chrome is drawn
        /// inside it, and exit waits for the helper to report which rectangle it actually applied —
        /// and every guard in this page needs to tell "waiting to start" from "running" from
        /// "waiting to finish" rather than just "on".
        /// </summary>
        private enum ResizeState
        {
            /// <summary>Not resizing. The only state in which the HIDE tile and the settings
            /// listener are allowed to write obscure commands.</summary>
            Off,

            /// <summary>The <c>obscure hide</c> has been written and we are waiting for its ack (or
            /// for the 400 ms arm timer to give up) before showing the overlay.</summary>
            Entering,

            /// <summary>The overlay is up — and the border is therefore down, they swap — and the
            /// user is dragging.</summary>
            Active,

            /// <summary>Exactly one <c>move</c> has been written and we are waiting for
            /// <c>region_changed</c>, a <c>command_error</c>, or the 2000 ms backstop.</summary>
            Exiting,
        }

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

        private ResizeState _resizeState = ResizeState.Off;

        // The hit-testable overlay that draws the region frame, the wash and the eight grab
        // handles. Alive only between TryShowResizeChrome and BeginExitResize / CancelResize /
        // AbortResize, and registered in BOTH teardown paths (HideWindows hides it, Close closes it
        // through AbortResize): it is topmost, takes clicks and now takes focus as well, and the
        // Esc that dismisses it is answered by this page, so one left behind after the page has
        // gone would be a rectangle over the user's desktop with nothing left to act on it.
        // Non-null is also this page's test for "the overlay is up" — see OnRegionChanged.
        private ShareResizeWindow _resizeWindow;

        // What this page last COMMANDED, and the user's own HIDE tile state as the page understands
        // it. Never read back from an ack: ShareObscureState.Strength is 0 for both "none" and
        // "hide", and BuildObscureCommand clamps a strength to 1..100, so restoring Blur from a
        // remembered wire value would send "obscure blur 1" — an invisible blur the user believes
        // is on, over a region their meeting can read.
        private bool _hideRequested;
        private ShareObscureMode _intendedMode = ShareObscureMode.None;
        private int _intendedStrength;

        // Captured when resize mode is entered and put back when it ends. _resizeHideSent is the
        // one that decides whether anything is restored at all: if this page never wrote an obscure
        // command on entry (the region was already hidden, or the helper's effect is dead) then it
        // owes nothing on exit and must send nothing, or it would unobscure something the user
        // deliberately hid.
        private bool _resizeWasHidden;
        private ShareObscureMode _preResizeMode = ShareObscureMode.None;
        private int _preResizeStrength;
        private bool _resizeHideSent;

        // Exactly one move may be in flight (the acks carry no request id, so a second could not be
        // matched to its own request). This is the guard, and every path out of the wait — the ack,
        // a command_error, the backstop timer and teardown — clears it.
        private bool _movePending;

        // 2000 ms backstop on the move, because a refusal arrives as a command_error that carries
        // no correlation id and can be missed or mis-attributed, and there is no other terminal
        // signal for a move that will never be applied.
        private DispatcherTimer _moveTimeout;

        // 400 ms fallback for the entry hide's ack, so a lost ack or a blocked command drain cannot
        // strand the user in a mode whose overlay never appears.
        private DispatcherTimer _resizeArm;

        // Held rather than re-read through SettingsRoot.Current on the way out: a settings reload
        // replaces the ShareRegion object wholesale, and unsubscribing from whatever the property points
        // at THEN would detach the handler from a different instance and leave this page rooted by
        // the old one for the rest of the app's life.
        private SettingsShareRegion _shareSettings;

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
                _driver.RegionChanged += OnRegionChanged;
                _driver.StatusReceived += OnStatusReceived;
                _driver.CommandError += OnCommandError;
                _driver.Ended += OnEnded;

                // Read once, here, and held for the session: the canvas the helper is about to build
                // is the only frame rate this share will ever have, so latching the value the
                // spawn actually used is what keeps the page honest about it.
                _mirrorFps = SettingsRoot.Current?.ShareRegion?.Fps ?? _mirrorFps;

                // Spawned while Clowd still holds the foreground rights the capture overlay handed
                // back to it on its way out (--shell-pid, CAPTURE_PROTOCOL.md §2.5). That matters:
                // the helper's prompt window has to come to the front to be findable, and a process
                // spawned by a background app is refused the foreground — the prompt would only
                // blink in the taskbar and the user would never see the thing they are meant to pick.
                await _driver.InitializeAsync(region, MirrorWindowTitle, MirrorCaptureCursor, _mirrorFps, binary);

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
            _toolbar.HideToggled += (s, hidden) => OnHideToggled(hidden);
            _toolbar.ResizeToggled += (s, on) => OnResizeToggled(on);
            _toolbar.CancelClicked += (s, e) => Cancel();

            // OPTIONS, exactly as VideoCapturePage wires it, only pointed at this feature's own page.
            // The settings window is a separate top-level and does not disturb the share: the mirror
            // is a region of the desktop, not a window, so raising Clowd's own settings over it
            // changes nothing about what the meeting sees.
            _toolbar.SettingsClicked += (s, e) =>
                PageManager.Current.GetSettingsPage().Open(SettingsPageTab.SettingsShareRegion);
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
                _toolbar.SetObscureAvailable(false);

            CreateResizeTimers();

            // The obscure mode and strength are live commands, so an edit made in the settings
            // window while this share is running belongs to THIS share. Captured into a field so the
            // unsubscribe in Close detaches from the same object it attached to, whatever
            // SettingsRoot.Current points at by then.
            _shareSettings = SettingsRoot.Current?.ShareRegion;
            if (_shareSettings != null)
                _shareSettings.PropertyChanged += OnShareSettingChanged;

            // …and the process itself may already be gone. See _pendingEnd.
            if (_pendingEnd is { } ended)
            {
                _pendingEnd = null;
                OnShareStopped(ended);
            }
        }

        /// <summary>
        /// Builds the two timers this page's resize mode waits on. Created once, when the session UI
        /// goes up, because neither can be needed before there is a toolbar to latch or a helper to
        /// talk to. Both are single-shot in effect: each stops itself in its own Tick, and every
        /// path that resolves the thing it was waiting for stops it explicitly, so a stale tick can
        /// never land on a later mode.
        /// </summary>
        private void CreateResizeTimers()
        {
            // The move backstop. A refused move answers command_error and emits no region_changed
            // at all, and that error carries no correlation id, so this is the only guarantee that
            // the mode ends: without it a refusal that was missed leaves both tiles locked for the
            // rest of the session and the obscure this page owes the user never restored, so the
            // region sits hidden with no control left that can unhide it.
            _moveTimeout = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2000) };
            _moveTimeout.Tick += (s, e) =>
            {
                _moveTimeout.Stop();
                if (_closing || !_movePending)
                    return;
                _movePending = false;
                // Nothing was applied, so the border stays where BeginExitResize put it back: on
                // the rect really being mirrored. Re-asserted rather than assumed, because this
                // timer also covers a move written from a state that never showed an overlay.
                _border?.SetRegion(_region);
                if (_resizeState == ResizeState.Exiting)
                    FinishResize("NO MOVE");
            };

            // The entry-hide arm. The overlay is gated on the helper confirming the region is
            // obscured, but an ack can be lost and the command drain can be blocked behind another
            // command, and a user who pressed RESIZE and got nothing has no way to find out why.
            // 400 ms is long enough that the ack normally wins and short enough not to read as a
            // hang. TryShowResizeChrome is a no-op unless the mode is still Entering.
            _resizeArm = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _resizeArm.Tick += (s, e) => { _resizeArm.Stop(); TryShowResizeChrome(); };
        }

        /// <summary>The obscure the HIDE tile asks for, from settings. Strength is passed
        /// unconditionally: <see cref="ShareRegionProtocol.BuildObscureCommand"/> DROPS it for Hide
        /// (producing a bare <c>obscure hide</c>), and hand-building the line instead is forbidden —
        /// <c>obscure hide 50</c> is refused with <c>command_error</c>, no ack ever arrives, and the
        /// driver's NoteObscureSent count is stranded, which makes the next genuine unsolicited
        /// <c>obscure/none</c> look solicited and leaves the tile lit over a region being mirrored
        /// in the clear.</summary>
        private static (ShareObscureMode Mode, int Strength) ConfiguredObscure()
        {
            var s = SettingsRoot.Current?.ShareRegion;
            return (ToProtocolMode(s?.ObscureStyle ?? ShareRegionObscureStyle.Blur),
                    s?.ObscureStrength ?? 75);
        }

        /// <summary>Maps the settings enum onto the protocol one. They are two types on purpose:
        /// <see cref="ShareObscureMode"/> lives in this assembly and the project reference to
        /// Clowd.Shared is one-way, so the settings side cannot see it. The settings enum has no
        /// <c>None</c> member — "not obscured" is the tile being off, not a configuration — so this
        /// mapping is total and its default arm can never be reached by a valid value.</summary>
        private static ShareObscureMode ToProtocolMode(ShareRegionObscureStyle style) => style switch
        {
            ShareRegionObscureStyle.Pixelate => ShareObscureMode.Pixelate,
            ShareRegionObscureStyle.Hide => ShareObscureMode.Hide,
            _ => ShareObscureMode.Blur,
        };

        /// <summary>
        /// The HIDE tile was pressed. The tile is optimistic: <c>HideClicked</c> flips itself and
        /// then reports the state it flipped TO, so it is already showing the new state by the time
        /// this runs. The helper's <c>obscure</c> ack is the only confirmation there is, and
        /// <see cref="OnObscureChanged"/> is what makes the tile agree with the process actually
        /// drawing the frames — including flipping it back when the helper does not do what it was
        /// asked. That is deliberately the opposite of the RESIZE tile, which never latches itself
        /// because the page can refuse the mode outright (see <see cref="OnResizeToggled"/>); a
        /// refused HIDE is only ever a wrong-looking tile for one round trip, while a refused
        /// resize would strand the tile in a mode that never began.
        /// What is sent is whatever the user configured (blur, pixelate or a
        /// black card, at their chosen strength), remembered here rather than read back from the
        /// wire so a later restore can reproduce it exactly.
        /// <para>Refused outright while resize mode is anything but Off. Resize mode owns the
        /// obscure state for its duration — it has hidden the region on the user's behalf and owes a
        /// restore — and a HIDE press landing in the middle of that would either be overwritten by
        /// the restore or overwrite it. The toolbar disables the tile for exactly this window, so
        /// this guard is the second line of that rule rather than the only one.</para>
        /// </summary>
        private void OnHideToggled(bool hidden)
        {
            if (_closing || _resizeState != ResizeState.Off)
                return;

            var (mode, strength) = ConfiguredObscure();
            _hideRequested = hidden;
            _intendedMode = hidden ? mode : ShareObscureMode.None;
            _intendedStrength = hidden ? strength : 0;
            _driver?.SetObscure(_intendedMode, _intendedStrength);
        }

        /// <summary>
        /// The user changed the obscure settings while a share is running. Because these are live
        /// stdin commands, a region that is currently hidden switches style or strength on the share
        /// the user is looking at, within one command round trip.
        /// <para>Silent unless the region is actually hidden: pushing a mode change down the pipe
        /// while nothing is obscured would obscure a region the user has not asked to hide. Silent
        /// while resize mode runs, for the same reason <see cref="OnHideToggled"/> is, and because
        /// the move abort path in <see cref="OnCommandError"/> consumes any <c>command_error</c> it
        /// sees while a move is pending — which is only sound while the move is the sole command
        /// this page has in flight.</para>
        /// </summary>
        private void OnShareSettingChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_closing || _resizeState != ResizeState.Off)
                return;

            // A null or empty name means "everything changed", which is what a wholesale settings
            // reload raises, so it has to be treated as a hit rather than skipped.
            if (!(String.IsNullOrEmpty(e.PropertyName)
                  || e.PropertyName == nameof(SettingsShareRegion.ObscureStyle)
                  || e.PropertyName == nameof(SettingsShareRegion.ObscureStrength)))
                return;

            if (!_hideRequested || _driver == null || !_driver.BlurAvailable)
                return;

            var (mode, strength) = ConfiguredObscure();
            _intendedMode = mode;
            _intendedStrength = strength;
            _driver.SetObscure(mode, strength);
        }

        /// <summary>
        /// The RESIZE tile was pressed. The tile deliberately does not flip itself: it raises the
        /// state it is ASKING for and this page answers with
        /// <see cref="FloatingToolbarWindow.SetResizeState"/>, so a request that arrives when the
        /// session cannot honour it (closing, not sharing yet, driver gone) leaves the tile alone
        /// instead of latching a mode that does not exist. That is what the corrective call in the
        /// bail-out branch is for.
        /// </summary>
        private void OnResizeToggled(bool on)
        {
            if (_closing || !_sharing || _driver == null)
            {
                _toolbar?.SetResizeState(false, false);
                return;
            }

            if (on)
            {
                if (_resizeState != ResizeState.Off)
                    return;
                EnterResize();
            }
            else
            {
                // Entering counts: the user can press RESIZE again before the overlay has appeared,
                // and that press must still end the mode (and restore the obscure) rather than being
                // dropped because the chrome was not up yet. Exiting does not — one move is already
                // in flight and a second is unmatchable.
                if (_resizeState is not (ResizeState.Entering or ResizeState.Active))
                    return;
                BeginExitResize();
            }
        }

        /// <summary>
        /// Enters resize mode: remember the obscure state to put back, obscure the region, and hand
        /// off to <see cref="TryShowResizeChrome"/> once the helper says the region is covered.
        /// Nothing of Clowd's is drawn inside the mirrored rectangle before that point.
        /// </summary>
        private void EnterResize()
        {
            _resizeState = ResizeState.Entering;
            _resizeWasHidden = _hideRequested;
            _preResizeMode = _intendedMode;
            _preResizeStrength = _intendedStrength;
            _resizeHideSent = false;

            // Latch the tile and lock HIDE BEFORE anything can await or ack.
            _toolbar?.SetResizeState(true, false);

            // Hide, not the configured style: the helper's blur at strength 50 is a 5x downscale and
            // its pixelate blocks top out at 9 canvas px, so a viewer plainly watches a region being
            // dragged through either. Hide is the only mode that reveals nothing (it samples no
            // source texture). No strength — the helper REFUSES one on hide.
            if (_intendedMode != ShareObscureMode.Hide && _driver.BlurAvailable)
            {
                _driver.SetObscure(ShareObscureMode.Hide);
                _resizeHideSent = true;
                _resizeArm.Start();     // 400 ms fallback if the ack is lost or the drain is blocked
            }
            else
            {
                // Either the region is already hidden — nothing to send, nothing owed back — or the
                // helper's effect is dead and this resize happens in full view of the meeting. The
                // second case is the one the user must be told about, and a blip on the drag handle
                // is the only channel there is: a dialog here lands over a live presentation.
                if (!_driver.BlurAvailable)
                    _toolbar?.ShowStatusBlip("LIVE");
                TryShowResizeChrome();
            }
        }

        /// <summary>Shows the overlay. Gated on the helper's Hide ack (or the arm timer) because the
        /// wash and the handles are drawn INSIDE the mirrored rectangle and there is no
        /// capture-exclusion mechanism anywhere in clowd_ui — this is the one deliberate, bounded
        /// exception to the rule that this page draws nothing into the pixels being broadcast. It
        /// does not close the window: the ack is emitted by the helper's command drain BEFORE the
        /// graphics thread has drawn the hidden frame, so roughly one frame (~33 ms at the default
        /// 30 fps, and longer on a session configured slower) is still exposed. It bounds the
        /// exposure to that instead of to a full command round trip.
        /// <para>Also one half of the swap (addendum 8.1): the border comes down here and goes back
        /// up in <see cref="RestoreBorder"/>, and between those two calls the overlay's own frame
        /// is the only thing marking the region.</para>
        /// <para>Idempotent by its own guard: the ack and the arm timer race deliberately, and the
        /// retraction path calls it too, so every one of those callers is written as "try".</para>
        /// </summary>
        private void TryShowResizeChrome()
        {
            if (_closing || _resizeState != ResizeState.Entering)
                return;

            _resizeArm.Stop();
            _resizeState = ResizeState.Active;

            // The border goes down FIRST (addendum 8.1), so the two windows never mark the region
            // at once and the overlay's frame is unambiguously the only one. The few dispatcher
            // frames between this and the Show below leave the region unframed; that is invisible
            // in practice and in any case lands while the region is obscured, which is the one
            // thing this method is gated on.
            _border?.Hide();

            _resizeWindow = new ShareResizeWindow(_region);
            _resizeWindow.RegionPreview += OnResizePreview;
            _resizeWindow.CancelRequested += OnResizeCancelled;

            // The other two are the window manager's, not the overlay's, and both only exist
            // because addendum 8.2 made this a focusable, activatable window: it can now be closed
            // out from under this page (Alt+F4) and re-activated over the toolbar. Neither is
            // observable any other way, and neither is survivable unobserved.
            _resizeWindow.Closed += OnResizeWindowClosed;
            _resizeWindow.Activated += OnResizeWindowActivated;
            _resizeWindow.Show();

            // ShowActivated alone is not reliable for a topmost tool window and the overlay really
            // must hold focus: on macOS a window that never activates does not get its cursor rects
            // honoured, so the per-handle resize cursors — the whole affordance — never appear, and
            // without focus there is no Esc either. Deactivating whatever the user was in (usually
            // the meeting client) does not disturb the share: the helper mirrors a region of the
            // desktop, not the focused window.
            _resizeWindow.Activate();

            // Two topmost windows now, one of them freshly activated over the other. The placement
            // cascade's last rung parks the strip INSIDE the region (reachable whenever the region
            // covers a whole monitor), so without this the RESIZE tile that ends the mode can be
            // covered by the very window that press put up — with the region hidden. Esc is a
            // second way out now, but a mode whose only escape is an unadvertised key is not one.
            // This is the first assertion, not the only one: OnResizeWindowActivated repeats it for
            // every LATER activation of the overlay, which re-orders it just the same.
            if (_toolbar != null)
                WindowNativeExtensions.RaiseTopmostNoActivate(_toolbar);
        }

        /// <summary>An accepted drag step. What the user watches follow their pointer is the
        /// overlay's own frame now (addendum 8.1); the page's <c>_region</c> does not move, because
        /// that field is the rectangle actually being mirrored — it is what the border is restored
        /// on and what AttachDiagnostics reports — and it may only ever be written from an applied
        /// ack.
        /// <para>The <c>SetRegion</c> below is deliberate, and it is NOT what any re-show depends
        /// on: the border is hidden for the whole of the mode, and every path that shows it again
        /// sets the APPLIED region first, precisely because a draft rectangle is not being
        /// mirrored. It is kept because it costs one geometry pass on a window nobody can see and
        /// it stops the hidden border's idea of the region from drifting arbitrarily far from the
        /// live one — the difference between a future path that shows it without re-homing being
        /// wrong by a clamp and being wrong by the whole drag.</para></summary>
        private void OnResizePreview(object sender, ScreenRect rect)
        {
            if (_closing)
                return;

            _border?.SetRegion(rect);
        }

        /// <summary>
        /// Esc outside a drag: leave resize mode without committing (addendum 8.2). Esc DURING a
        /// drag never reaches here — the overlay reverts that drag itself and raises nothing — and
        /// a commit is always the RESIZE tile's job, so there is exactly one thing this can mean,
        /// and it is what a refused move already does minus the move.
        /// </summary>
        private void OnResizeCancelled(object sender, EventArgs e)
        {
            // Active is the only state in which an overlay exists to raise this, but the guard is
            // free: a cancel landing during Exiting would write a second obscure restore on top of
            // the one that exit already owes.
            if (_closing || _resizeState != ResizeState.Active)
                return;

            CancelResize();
        }

        /// <summary>
        /// The window manager destroyed the overlay — Alt+F4, or anything else that closes a window
        /// this page did not close itself. Reachable only because addendum 8.2 made the overlay a
        /// focusable, activatable window, and the one accident the swap rule (8.1) cannot survive
        /// unnoticed: the border is hidden for the whole of the mode, so a page that goes on
        /// believing the overlay is up leaves a live share with NEITHER window marking the
        /// rectangle being broadcast.
        /// <para>Every close this page performs goes through <see cref="DestroyResizeWindow"/>,
        /// which detaches before it calls Close, so none of them reach here and none of them can
        /// double-run the cancel path — this handler answers only the closes it did not perform.
        /// The window is detached and forgotten BEFORE that path runs, which is also what keeps the
        /// <see cref="DestroyResizeWindow"/> inside <see cref="CancelResize"/> from calling Close a
        /// second time on a window that is already closing.</para>
        /// </summary>
        private void OnResizeWindowClosed(object sender, EventArgs e)
        {
            if (_resizeWindow == null || !ReferenceEquals(sender, _resizeWindow))
                return;

            DetachResizeWindow(_resizeWindow);
            _resizeWindow = null;

            // The same guard OnResizeCancelled uses, and for the same reason: Active is the only
            // state in which an overlay exists at all, and a close landing during Exiting or after
            // _closing would write a restore on top of one that path already owes.
            if (_closing || _resizeState != ResizeState.Active)
                return;

            // Exactly a cancel, and deliberately not a commit: the border comes back on the last
            // APPLIED region, the obscure is restored and no move is written. Whatever rectangle
            // the user had dragged to died with the window, and a dismissed window is not consent
            // to move the region.
            CancelResize();
        }

        /// <summary>
        /// The overlay took activation — at <c>Show</c>, and again every time the user comes back to
        /// it from another app. Activation re-orders a window above its topmost peers and the
        /// toolbar is one of them, so the raise done once in <see cref="TryShowResizeChrome"/> is a
        /// guarantee with a lifetime of one click: the strip can be parked INSIDE the region (the
        /// placement cascade's last rung), and a buried strip means the RESIZE tile — the only
        /// gesture that commits — is unreachable for the rest of the mode.
        /// <para>Activation is the whole of the exposure, which is why there is no pointer hook here
        /// and no timer. A press that does NOT change activation re-orders nothing: Windows raises a
        /// window when it is activated, not when an already-active one is clicked, and on macOS the
        /// toolbar sits a whole window level above the overlay (see
        /// <see cref="WindowNativeExtensions.RaiseTopmostNoActivate"/>), which no re-ordering within
        /// a level can beat. The event that can bury the toolbar is the event that un-buries
        /// it.</para>
        /// </summary>
        private void OnResizeWindowActivated(object sender, EventArgs e)
        {
            if (_closing || _toolbar == null)
                return;

            WindowNativeExtensions.RaiseTopmostNoActivate(_toolbar);
        }

        /// <summary>
        /// Ends resize mode without writing a <c>move</c>. The order is the same as
        /// <see cref="BeginExitResize"/>'s and for the same reasons: the overlay comes out of the
        /// mirrored rectangle first, then the border goes back on the last APPLIED region — which
        /// here is simply the region, since a cancel changes nothing — and then
        /// <see cref="FinishResize"/> does the obscure restore and the tiles.
        /// <para>Silent, with no blip: the overlay vanishing and the RESIZE tile unlatching are the
        /// feedback for something the user just asked for. The blips are reserved for the outcomes
        /// they did not ask for, like a move the helper refused.</para>
        /// </summary>
        private void CancelResize()
        {
            DestroyResizeWindow();

            if (_closing) { AbortResize(); return; }

            RestoreBorder();
            FinishResize(null);
        }

        /// <summary>
        /// Leaves resize mode, committing whatever the drag arrived at. Pressing RESIZE again is
        /// the only gesture that commits: a single drag is undone with a right-button press or Esc
        /// inside the overlay, and Esc outside a drag leaves the mode without committing at all
        /// (<see cref="CancelResize"/>).
        /// <para>Order matters here and is the reason this is not folded into
        /// <see cref="FinishResize"/>: the overlay comes out of the mirrored rectangle FIRST, before
        /// the move is written and long before the obscure is lifted, so there is no instant in
        /// which the meeting sees Clowd's chrome over an unobscured region. The border goes back up
        /// immediately behind it rather than on the ack, because a move can be in flight for up to
        /// the 2000 ms backstop and a session that spends two seconds with neither window up has
        /// stopped saying which pixels it is broadcasting.</para>
        /// </summary>
        private void BeginExitResize()
        {
            var target = _resizeWindow?.Region ?? _region;

            // Chrome out of the mirrored rectangle FIRST, before anything else can happen.
            DestroyResizeWindow();

            if (_closing) { AbortResize(); return; }

            // …and the border straight back behind it, on what is being mirrored right now.
            // Pointedly not on `target`: that rectangle has not been applied yet and may never be
            // (the helper clamps it, or refuses the move outright), and the border must never frame
            // pixels that are not in the meeting. OnRegionChanged moves it the rest of the way when
            // the ack says what was actually applied.
            RestoreBorder();

            if (target == _region || _driver == null)    // ScreenRect is a record: value equality
            {
                // Nothing moved (the user entered the mode and left it, or dragged back to where
                // they started), so there is no move to write and no ack to wait for. Straight to
                // the funnel, which still owes the obscure restore.
                FinishResize(null);
                return;
            }

            _resizeState = ResizeState.Exiting;
            _movePending = true;
            // busy keeps HIDE locked and RESIZE un-pressable until the region has actually moved.
            _toolbar?.SetResizeState(true, true);
            _driver.MoveRegion(target);                  // EXACTLY ONE move: acks carry no request id
            _moveTimeout.Start();                        // 2000 ms
        }

        /// <summary>
        /// The helper applied a region — the answer to this page's <c>move</c>. The rect it reports
        /// is what it ACTUALLY applied after its own clamp (each side floored at 64 px and then
        /// rounded down to even), which is routinely a pixel or two off what was asked for, so this
        /// is the only place <c>_region</c> is written and everything visible reflows from it.
        /// </summary>
        private void OnRegionChanged(object sender, ShareRegionRect rect)
        {
            if (_closing)
                return;

            var applied = ToScreenRect(rect);
            if (applied == null)
                return;

            // Applied unconditionally, even outside Exiting: a late ack that arrived after a timeout
            // still corrects the border rather than leaving it lying about which pixels are in the
            // meeting. The border may be hidden here (resize mode swapped it out), in which case
            // the same write is what RestoreBorder puts back up.
            _region = applied;
            _border?.SetRegion(_region);
            _toolbar?.UpdateRegion(_region);

            // Re-home the overlay when the region moved underneath it. The two tests say different
            // things and both are needed: _resizeWindow != null is "the overlay is up", and
            // !_movePending is "this ack is not the answer to our own move" — the region changed
            // for a reason that did not come from the drag being committed. BeginExitResize takes
            // the overlay down BEFORE it writes its move, so in practice a pending move never
            // coincides with a live overlay; the guard states which ack is ours rather than leaning
            // on that ordering, because reading it backwards would yank the rect out from under a
            // user mid-mode. Without the call the overlay stays anchored to a stale rectangle and
            // its next commit writes a move that undoes the one just applied.
            // ResetRegion is itself a no-op while a drag is in progress (the pointer is the
            // authority then) and never echoes RegionPreview back, so this can neither fight the
            // user nor loop.
            if (_resizeWindow != null && !_movePending)
                _resizeWindow.ResetRegion(_region);

            if (_movePending)
            {
                _movePending = false;
                _moveTimeout.Stop();
                if (_resizeState == ResizeState.Exiting)
                    FinishResize(null);
            }
        }

        /// <summary>The single funnel every exit from resize mode reaches exactly once — normal ack,
        /// refusal, timeout, the Esc cancel and the no-op path all land here. That is what keeps the
        /// obscure command count balanced: at most one Hide out at entry, at most one restore out at
        /// exit. Being that funnel is also why it re-asserts the border swap.</summary>
        /// <param name="blip">A short message for the drag-handle label, or null for the silent
        /// case. Never a dialog: every one of these paths can land in the middle of a meeting the
        /// user is presenting to.</param>
        private void FinishResize(string blip)
        {
            _resizeArm.Stop();
            _moveTimeout.Stop();
            _movePending = false;
            _resizeState = ResizeState.Off;

            // The funnel's half of the swap rule (addendum 8.1). Every caller has already put the
            // border back — they take the overlay down and restore in the same breath — so this is
            // normally an idempotent re-assert of a window that is already up on the same rect. It
            // is here anyway because this is the one method every exit passes through: a path added
            // later that ends the mode without going through BeginExitResize or CancelResize would
            // otherwise leave a live session with nothing at all framing the region being
            // broadcast, which is the single failure the rule exists to prevent.
            RestoreBorder();

            if (!_closing && _driver != null && _resizeHideSent)
            {
                if (_resizeWasHidden && _preResizeMode != ShareObscureMode.None && _driver.BlurAvailable)
                {
                    // the user had deliberately hidden it: put back exactly what they had.
                    _driver.SetObscure(_preResizeMode, _preResizeStrength);
                    _intendedMode = _preResizeMode;
                    _intendedStrength = _preResizeStrength;
                    _hideRequested = true;
                }
                else
                {
                    _driver.SetObscure(ShareObscureMode.None);
                    _intendedMode = ShareObscureMode.None;
                    _intendedStrength = 0;
                    _hideRequested = false;
                }
            }
            _resizeHideSent = false;

            // reconcile the tile explicitly: the acks were suppressed while resize owned the state,
            // and the restore ack may never come (BlurAvailable already false).
            _toolbar?.SetHidden(_hideRequested);
            _border?.SetHiddenIndicator(_hideRequested);
            _toolbar?.SetResizeState(false, false);
            if (blip != null)
                _toolbar?.ShowStatusBlip(blip);
        }

        /// <summary>Puts the border window back up, on the rectangle the helper is ACTUALLY
        /// mirroring — <c>_region</c>, which only an applied ack ever writes — and never on a
        /// dragged or a merely requested one, which would frame pixels the meeting is not being
        /// shown. The other half of the swap performed in <see cref="TryShowResizeChrome"/>:
        /// between them they hold the rule that a live session always has exactly one of the two
        /// windows marking the region.
        /// <para>Idempotent and cheap, which is why callers do not check first: showing a window
        /// that is already up is at worst a redundant platform Show, and the border is
        /// <c>ShowActivated="False"</c> with <c>WS_EX_NOACTIVATE</c>, so it cannot pull focus off
        /// the overlay or the meeting app either way; <see cref="BorderWindow.SetRegion"/> is one
        /// geometry pass. Silent once <see cref="_closing"/> is latched, because every teardown path
        /// is taking the border DOWN and re-showing it there would flash a frame around a region
        /// that is no longer being shared.</para></summary>
        private void RestoreBorder()
        {
            if (_closing || _border == null)
                return;

            // Region before Show, so the window is never composited for even one frame on the rect
            // it was hidden at — which, after a drag, is wherever the last preview left it.
            _border.SetRegion(_region);
            _border.Show();

            // Re-showing a topmost window puts it above its topmost peers, and the strip can sit
            // right against the region edge the frame is drawn on. The toolbar was raised over the
            // overlay on the way in for a stronger reason; this keeps it there on the way out.
            if (_toolbar != null)
                WindowNativeExtensions.RaiseTopmostNoActivate(_toolbar);
        }

        /// <summary>Takes the overlay down, unsubscribing through
        /// <see cref="DetachResizeWindow"/> first: a live
        /// <see cref="ShareResizeWindow.CancelRequested"/> on a window this page has already
        /// dropped would drive the mode from a rectangle nobody can see. Detaching BEFORE the Close
        /// is what keeps this page's own closes out of <see cref="OnResizeWindowClosed"/>, which
        /// exists for the closes it does not perform. Hide before Close because Close is the slower
        /// of the two and the wash is drawn inside the mirrored rectangle.
        /// <para>Nothing here tries to hand focus back to whatever held it before the overlay took
        /// it (addendum 8.2): there is no reliable cross-platform previous-focus handle, and
        /// guessing wrong — pulling the user into a window they had left — is worse than leaving
        /// focus where the window manager puts it.</para></summary>
        private void DestroyResizeWindow()
        {
            if (_resizeWindow == null)
                return;

            DetachResizeWindow(_resizeWindow);
            try { _resizeWindow.Hide(); } catch { }
            try { _resizeWindow.Close(); } catch { }
            _resizeWindow = null;
        }

        /// <summary>Drops all four of this page's handlers off an overlay window. The single place
        /// any of them is unsubscribed, so a path that remembers one and forgets another cannot
        /// exist. Split out of <see cref="DestroyResizeWindow"/> rather than inlined twice because
        /// <see cref="OnResizeWindowClosed"/> needs the unsubscribe without the Hide/Close — the
        /// window is already closing there, and calling Close on it from inside its own Closed
        /// event is the one thing that path must not do.</summary>
        private void DetachResizeWindow(ShareResizeWindow window)
        {
            window.RegionPreview -= OnResizePreview;
            window.CancelRequested -= OnResizeCancelled;
            window.Closed -= OnResizeWindowClosed;
            window.Activated -= OnResizeWindowActivated;
        }

        /// <summary>Teardown-only: drop resize mode without touching the helper (the process is going
        /// away and WriteCommand is deliberately silent once disposed). Called first from
        /// <see cref="Close"/>, so the overlay cannot outlive the session that owns it — it is
        /// topmost, hit-testable and activated, and the Esc that dismisses it is answered by this
        /// page, so one left behind after the page has gone is a rectangle over the user's desktop
        /// with nothing left to act on it.
        /// <para>Deliberately does NOT restore the border, unlike every other way out of the mode:
        /// every caller is a teardown that is hiding the border in the same breath
        /// (<see cref="HideWindows"/>) or closing it a few lines later (<see cref="Close"/>), and
        /// <see cref="RestoreBorder"/> stands down on <see cref="_closing"/> for that reason.</para></summary>
        private void AbortResize()
        {
            _resizeArm?.Stop();
            _moveTimeout?.Stop();
            _movePending = false;
            _resizeHideSent = false;
            _resizeState = ResizeState.Off;

            DestroyResizeWindow();
        }

        /// <summary>
        /// The helper acknowledged an obscure state. Three kinds arrive here: the ack for a toggle
        /// the user just made, the ack for the <c>hide</c> this page writes on its own account when
        /// resize mode starts, and the UNSOLICITED <c>obscure/none</c> the helper sends when its GPU
        /// effect fails to build. That failure is permanent for the life of the process — it never
        /// tries to build the effect again — so the third kind retires the tile rather than merely
        /// showing it off, and the state carried by the event (not the driver property, which the
        /// pump thread may already have moved on) is what decides which one this is.
        /// </summary>
        private void OnObscureChanged(object sender, ShareObscureState state)
        {
            if (_closing)
                return;

            // While resize mode owns the obscure state the tile must not follow the wire: the entry
            // Hide would light it and the exit restore unlight it, and the user watches a control
            // they were told is disabled flicker around a resize they never associated with hiding.
            // FinishResize reconciles the tile once, from the page's own remembered state.
            if (_resizeState == ResizeState.Off)
            {
                _toolbar?.SetHidden(state.Mode != ShareObscureMode.None);

                // Driven from the WIRE state, not from _hideRequested: the eye is a claim about what
                // the meeting is seeing right now, so the only honest source for it is the mode the
                // helper says it is actually compositing. Suppressed for the same reason the tile is
                // while resize owns the obscure state — the border is hidden for all of that mode
                // anyway, and FinishResize reconciles both once at the end.
                _border?.SetHiddenIndicator(state.Mode != ShareObscureMode.None);
            }

            if (_resizeState == ResizeState.Entering && state.Mode == ShareObscureMode.Hide)
                TryShowResizeChrome();

            if (state.Unsolicited && !state.BlurAvailable)
            {
                Debug.WriteLine("The region sharing helper retracted its obscure effect; retiring the HIDE tile.");

                // GfxState::Failed is permanent for the PROCESS and gates hide, pixelate and blur
                // alike, so the remembered mode is now unrestorable — clear it rather than sending a
                // command that can only produce another retraction.
                _intendedMode = ShareObscureMode.None;
                _intendedStrength = 0;
                _hideRequested = false;
                _preResizeMode = ShareObscureMode.None;
                _resizeWasHidden = false;
                _resizeHideSent = false;

                // also unlights the tile and blips "NO HIDE" on the drag handle. Never a dialog:
                // this can land in the middle of a meeting the user is presenting to. Goes through
                // UpdateShareLocks, so HIDE stays locked while resize is still active.
                _toolbar?.SetObscureAvailable(false);

                if (_resizeState != ResizeState.Off)
                {
                    // The region this resize is happening over is no longer obscured and cannot be
                    // made so again. The user cannot be left to discover that from the picture their
                    // meeting is seeing, and the mode must not be stranded waiting for an ack that
                    // will never come.
                    _toolbar?.ShowStatusBlip("LIVE");
                    TryShowResizeChrome();              // no-op unless still Entering
                }
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
        /// The helper rejected a command. For an obscure command this is logged and dropped: the
        /// tile's next ack (or the lack of one) is already the user-visible answer, and a modal
        /// raised over a live presentation is worse than the thing it is reporting. While a move is
        /// pending it is the abort signal, because a refused move produces nothing else at all.
        /// </summary>
        private void OnCommandError(object sender, string message)
        {
            Debug.WriteLine("The region sharing helper rejected a command: " + message);
            if (_closing || !_movePending)
                return;

            // A refused move emits ONLY command_error and never region_changed, and it is reachable
            // in ordinary operation: the helper plans against the monitor snapshot it took at
            // bootstrap and never re-enumerates, so a move onto a display rearranged mid-session is
            // refused with "The capture region does not intersect any display" and nothing has
            // changed.
            // command_error carries no correlation id, so consuming one here is an inference — safe
            // because the only commands this page sends during Exiting are the move and nothing
            // else. OnHideToggled and OnShareSettingChanged both refuse while resize is running,
            // which is what keeps that true; anything added here that writes a command mid-move
            // would break this handler rather than itself.
            _movePending = false;
            _moveTimeout.Stop();
            _border?.SetRegion(_region);      // refused: nothing moved, so re-assert what is mirrored
            if (_resizeState == ResizeState.Exiting)
                FinishResize("NO MOVE");
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
            // The resize overlay goes first: it is the only one of the three that draws inside the
            // mirrored rectangle, and every caller of this is a path where the region may still be
            // being broadcast for another moment.
            try { _resizeWindow?.Hide(); }
            catch { }
            try { _border?.Hide(); }
            catch { }
            try { _toolbar?.Hide(); }
            catch { }
        }

        private void Close()
        {
            _closing = true;

            // First, before the driver goes: this stops both timers, drops the pending move and
            // closes the overlay without writing a single command — the process is on its way out
            // and a restore sent into a closing pipe is silently discarded anyway. Miss this and a
            // topmost, hit-testable, focus-taking rectangle outlives the session over the user's
            // desktop — its Esc is answered by this page, which by then is gone.
            AbortResize();

            if (_shareSettings != null)
            {
                _shareSettings.PropertyChanged -= OnShareSettingChanged;
                _shareSettings = null;
            }

            if (ReferenceEquals(ActiveInstance, this))
                ActiveInstance = null;

            if (_driver != null)
            {
                _driver.ObscureChanged -= OnObscureChanged;
                _driver.RegionChanged -= OnRegionChanged;
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

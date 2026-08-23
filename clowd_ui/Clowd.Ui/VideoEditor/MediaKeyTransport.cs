using System;
using System.Collections.Generic;
using Avalonia.Input;
using Clowd.Config;

namespace Clowd.UI.VideoEditor
{
    /// <summary>The transport a media key press is routed to — implemented by the video editor
    /// window (see <see cref="MediaKeyTransport"/>).</summary>
    internal interface IMediaKeyTarget
    {
        void MediaPlayPause();

        /// <summary><paramref name="direction"/> is +1 (next track key) or -1 (previous track).</summary>
        void MediaStepFrame(int direction);
    }

    /// <summary>
    /// Routes the keyboard's play/pause and next/previous track keys to the focused video editor
    /// window while <see cref="SettingsRecording.CaptureMediaKeys"/> is on (off by default: these
    /// keys belong to whatever is playing music until the user says otherwise).
    ///
    /// The keys never arrive as ordinary window input — Windows turns them into WM_APPCOMMAND for
    /// the shell and macOS keeps them as system-defined events — so they are picked up through the
    /// same SharpHook keyboard hook the global hotkeys use (<see cref="HotkeyManager.Host"/>),
    /// which also lets the press be swallowed so the media player does not act on it too. That
    /// hook is global, so the registration is held only while an editor window is actually
    /// focused: <see cref="SetTarget"/> on activation, <see cref="ReleaseTarget"/> on deactivation
    /// and on close. Everything here runs on the UI thread (window activation events, and hook
    /// callbacks, which the host marshals).
    /// </summary>
    internal static class MediaKeyTransport
    {
        private static readonly (Key Key, Action<IMediaKeyTarget> Invoke)[] Bindings =
        {
            (Key.MediaPlayPause, t => t.MediaPlayPause()),
            (Key.MediaNextTrack, t => t.MediaStepFrame(1)),
            (Key.MediaPreviousTrack, t => t.MediaStepFrame(-1)),
        };

        private static IMediaKeyTarget _target;
        private static List<IGlobalTriggerRegistration> _registrations;

        /// <summary>Points the media keys at <paramref name="target"/>, registering them if the
        /// setting allows it. Called on every activation, so a setting toggled while the editor was
        /// in the background takes effect the moment it comes back to the front.</summary>
        public static void SetTarget(IMediaKeyTarget target)
        {
            _target = target;

            if (target != null && SettingsRoot.Current?.Recording?.CaptureMediaKeys == true)
                Register();
            else
                Unregister();
        }

        /// <summary>Drops <paramref name="target"/>, but only while it is still the current one:
        /// switching between two editor windows raises the incoming window's Activated before the
        /// outgoing window's Deactivated on some platforms, and that must not unhook the new one.</summary>
        public static void ReleaseTarget(IMediaKeyTarget target)
        {
            if (ReferenceEquals(_target, target))
                SetTarget(null);
        }

        private static void Register()
        {
            if (_registrations != null)
                return;

            var host = HotkeyManager.Current?.Host;
            if (host == null)
                return; // no hotkey manager (dev harness) — nothing to hook

            var registrations = new List<IGlobalTriggerRegistration>(Bindings.Length);
            foreach (var (key, invoke) in Bindings)
            {
                // a failed registration (the hook could not start, or the key is already a global
                // hotkey) is kept and disposed with the rest: it simply never fires, and the
                // editor's own Space / arrow keys are unaffected.
                registrations.Add(host.RegisterTrigger(new SimpleKeyGesture(key), () =>
                {
                    var target = _target;
                    if (target != null)
                        invoke(target);
                }));
            }

            _registrations = registrations;
        }

        private static void Unregister()
        {
            if (_registrations == null)
                return;

            foreach (var reg in _registrations)
                reg.Dispose();

            _registrations = null;
        }
    }
}

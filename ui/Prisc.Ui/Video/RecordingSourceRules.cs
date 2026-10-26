using System;
using System.Collections.Generic;
using System.Linq;
using Clowd.UI.Controls.Tray;

namespace Clowd.UI
{
    /// <summary>
    /// The decisions the recording strip makes about its capture sources, with the settings, the
    /// device managers and the controls taken out. Every rule here was a line inside the old toolbar
    /// window that could only be exercised by opening a recording session with a particular set of
    /// devices attached — which is to say, never on purpose. They are the rules
    /// that decide whether a source records at all ("default" backed by nothing is not a device),
    /// whether a picker is worth offering (one real device is not a choice), and whether a toggle
    /// stays live once frames are flowing (a mute needs something to mute).
    /// <para>
    /// <see cref="RecordingSources"/> supplies the device lists and the settings; the strip supplies
    /// the recording flags. Nothing in here reads either, so all of it is testable
    /// (<c>RecordingSourceRulesTests</c>).
    /// </para>
    /// </summary>
    public static class RecordingSourceRules
    {
        /// <summary>
        /// Whether <paramref name="deviceId"/> names a device that can actually be recorded right
        /// now, given the ids the enumerator currently offers.
        /// <para>
        /// "default" is a pointer at whichever device the OS currently favours, and the enumerator
        /// always offers it — so on a machine with no inputs at all it would read as a selection and
        /// earn a track of silence. It only counts while something backs it.
        /// </para>
        /// <para>
        /// A stored id for a device that has since been unplugged counts as none: the recorder would
        /// open nothing and the user would find that out after the recording, not before.
        /// </para>
        /// </summary>
        public static bool HasAudioDevice(string deviceId, IReadOnlyList<string> deviceIds, string defaultDeviceId)
        {
            if (deviceIds == null || !deviceIds.Any(id => id != defaultDeviceId))
                return false;

            return !String.IsNullOrEmpty(deviceId) && deviceIds.Any(id => id == deviceId);
        }

        /// <summary>
        /// Whether the stored webcam id names a camera that can actually be recorded right now, given
        /// the camera list the strip has. That list is tri-state and every state means something
        /// different here: <c>null</c> is "not enumerated yet", which is not "no cameras" — the stored
        /// id is trusted until there is something to check it against; an empty list is "enumerated,
        /// and there are none"; a list without the id is "the camera it names is gone".
        /// <para>
        /// Unlike audio there is no "default" pseudo-device to discount: the camera menu lists real
        /// devices only, so presence in the list is the whole rule.
        /// </para>
        /// </summary>
        public static bool HasCameraDevice(string deviceId, IReadOnlyList<string> cameraIds)
            => !String.IsNullOrEmpty(deviceId) && (cameraIds == null || cameraIds.Contains(deviceId));

        /// <summary>
        /// The devices the user could actually choose between — which is not what the menu lists.
        /// "default" is a pointer at one of the others, never an alternative to them: with a single
        /// microphone attached, "Default" and that microphone ARE that microphone, so a source with
        /// one real device has nothing to pick. (The menu still offers "default" when it opens at
        /// all — following the system default is a real preference once there is more than one
        /// device to follow.)
        /// <para><c>null</c> in — a list that has not been enumerated yet — is an empty list out:
        /// "we cannot say" and "nothing to choose" lead to the same strip.</para>
        /// </summary>
        public static IReadOnlyList<string> RealDeviceIds(IEnumerable<string> deviceIds, string defaultDeviceId)
            => deviceIds == null
                ? Array.Empty<string>()
                : deviceIds.Where(id => id != defaultDeviceId).ToList();

        /// <summary>
        /// Whether a source's picker half is worth showing. A picker only earns its chevron when
        /// there is a choice to make with it — otherwise the toggle's own click is the whole
        /// interaction and a chevron would promise something that does not exist.
        /// </summary>
        public static bool ShowsChevron(int realDeviceCount) => realDeviceCount > 1;

        /// <summary>
        /// Whether a source's toggle stays usable. Mic and system audio are mutes, so they stay live
        /// while recording — but only where the recorder actually built a source to mute. With no
        /// device there is nothing to unmute, and the picker that would fix that is frozen too, so
        /// the toggle locks with it rather than lighting up over silence.
        /// </summary>
        public static bool SourceEnabled(bool recording, bool hasDevice) => !recording || hasDevice;

        /// <summary>
        /// The transport state of the primary button. Recording wins over waiting (a respawn while
        /// frames flow must not turn PAUSE back into a dead WAIT tile), and paused is only a thing a
        /// running recording can be.
        /// </summary>
        public static TrayPrimaryState PrimaryState(bool waiting, bool recording, bool paused)
            => recording
                ? (paused ? TrayPrimaryState.Paused : TrayPrimaryState.Active)
                : (waiting ? TrayPrimaryState.Waiting : TrayPrimaryState.Idle);

        /// <summary>
        /// The primary button's label while a recording runs: <c>mm:ss</c>, the same format the page
        /// used to push through <c>SetStatusText</c>. Minutes are not wrapped into hours — a
        /// two-hour recording reads "120:00", which is what the old strip did and what the spec
        /// accepts (the label clips rather than growing, so the strip never changes width).
        /// <c>null</c> — no status has arrived yet — is "00:00" rather than an empty tile.
        /// </summary>
        public static string FormatElapsed(TimeSpan? elapsed)
        {
            if (elapsed == null)
                return "00:00";

            var value = elapsed.Value;
            return $"{(int)value.TotalMinutes:D2}:{value.Seconds:D2}";
        }
    }
}

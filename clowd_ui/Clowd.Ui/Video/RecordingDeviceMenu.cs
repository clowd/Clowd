using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;

namespace Clowd.UI
{
    /// <summary>
    /// The device picker behind a source slot's chevron: one radio row per device, captioned with
    /// the source it is choosing for. Builds the flyout only — placement is the chassis's
    /// (<c>FloatingTrayWindow.ShowMenu</c> anchors it on the slot and picks the free edge), and the
    /// rows write through <see cref="RecordingSources"/>, so this file holds no state of its own
    /// and a menu that outlives its strip can do nothing.
    /// </summary>
    public static class RecordingDeviceMenu
    {
        /// <summary>
        /// Builds the radio device menu for <paramref name="source"/>. Audio fills synchronously
        /// ("No microphones found" / "No speakers found" when only the default pseudo-device
        /// exists); camera adds "Looking for cameras…" and then refills from
        /// <see cref="RecordingSources.RefreshCamerasAsync"/> ("No cameras found" when empty).
        /// Picking a row writes the device id through <see cref="RecordingSources.PickDevice"/> and
        /// then runs <paramref name="onPicked"/>, which is how a turn-on that had nowhere to record
        /// from completes the click the user made (null when the menu was opened from the chevron,
        /// which is a device change and leaves the toggle alone).
        /// </summary>
        public static MenuFlyout Build(RecordingSources sources, CaptureSource source, Action onPicked)
        {
            var flyout = new MenuFlyout();
            flyout.Items.Add(Caption(source));

            if (source == CaptureSource.Webcam)
                // …and again on every open, so the list is current without the user having to ask
                // for a refresh. This is what replaced the explicit "Refresh camera list" row.
                FillCameraMenu(flyout, sources, onPicked);
            else
                FillAudioMenu(flyout, sources, source, onPicked);

            return flyout;
        }

        /// <summary>The caption row that names what the list is choosing. Uppercase here because
        /// the popup theme has no text-transform (spec §5), and disabled because it is a sentence,
        /// not an offer.</summary>
        private static MenuItem Caption(CaptureSource source) => new MenuItem
        {
            Classes = { "header" },
            Header = source switch
            {
                CaptureSource.Microphone => "MICROPHONE",
                CaptureSource.Speaker => "SYSTEM AUDIO",
                _ => "CAMERA",
            },
            IsEnabled = false,
        };

        /// <summary>Audio enumeration is a local call (WASAPI / CoreAudio), so the menu is built
        /// complete. A stored device that is no longer connected is simply absent, leaving nothing
        /// checked — which is the truth: it is not what would be recorded.</summary>
        private static void FillAudioMenu(MenuFlyout flyout, RecordingSources sources, CaptureSource source, Action onPicked)
        {
            var isSpeaker = source == CaptureSource.Speaker;
            var devices = sources.AudioDevices(source);
            var current = sources.DeviceId(source);

            // "default" alone is not an offer: it would point at nothing, and picking it would
            // turn the source on to record silence.
            if (sources.RealDeviceIds(source).Count == 0)
            {
                flyout.Items.Add(new MenuItem { Header = isSpeaker ? "No speakers found" : "No microphones found", IsEnabled = false });
                return;
            }

            foreach (var device in devices)
                flyout.Items.Add(DeviceItem(device.FriendlyName, device.DeviceId, current, source, sources, onPicked));
        }

        /// <summary>The enumeration is off-thread (a native call, but a click must never wait on
        /// one), so the menu opens on a placeholder and fills itself when the list lands. That is
        /// usually the same frame; it is visible only on the recorder fallback path.</summary>
        private static void FillCameraMenu(MenuFlyout flyout, RecordingSources sources, Action onPicked)
        {
            flyout.Items.Add(new MenuItem { Header = "Looking for cameras…", IsEnabled = false });
            _ = FillCameraMenuAsync(flyout, sources, onPicked);
        }

        private static async Task FillCameraMenuAsync(MenuFlyout flyout, RecordingSources sources, Action onPicked)
        {
            // shares the strip's one camera list, so an enumeration that lands while the menu is
            // open also settles whether the camera slot belongs there at all.
            IReadOnlyList<CameraDeviceInfo> cameras = await sources.RefreshCamerasAsync();

            // the menu may have been dismissed (or the whole window closed) while the recorder was
            // listing devices; refilling a detached flyout is harmless, and nothing here reopens it.
            flyout.Items.Clear();
            flyout.Items.Add(Caption(CaptureSource.Webcam));

            if (cameras.Count == 0)
                flyout.Items.Add(new MenuItem { Header = "No cameras found", IsEnabled = false });

            // read after the await: the refresh may have unticked a webcam whose stored id turned
            // out not to be attached, and the tick belongs on what would actually be recorded.
            var current = sources.DeviceId(CaptureSource.Webcam);

            foreach (var camera in cameras)
                flyout.Items.Add(DeviceItem(camera.FriendlyName, camera.DeviceId, current, CaptureSource.Webcam, sources, onPicked));
        }

        private static MenuItem DeviceItem(string header, string deviceId, string currentId, CaptureSource source, RecordingSources sources, Action onPicked)
        {
            var item = new MenuItem
            {
                Header = header,
                // radio rather than a checkmark: exactly one device is recorded per source, and
                // the group makes the menu say so on its own.
                ToggleType = MenuItemToggleType.Radio,
                GroupName = "clowdToolbarDevice" + source,
                IsChecked = deviceId == currentId,
            };

            // the device id first, then the deferred turn-on: a toggle that comes on before its
            // source has something to record from is a promise the recording cannot keep. Writing
            // the settings property is the whole of the pick — VideoCapturePage turns the change
            // into a `configure` on the waiting recorder, exactly as it does for the settings page.
            item.Click += (s, e) =>
            {
                sources.PickDevice(source, deviceId);
                onPicked?.Invoke();
            };

            return item;
        }
    }
}

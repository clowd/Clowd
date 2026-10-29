using System;
using System.Collections.Generic;
using System.Linq;
using Prisc.Interop;
using Prisc.Interop.Testing;
using Prisc.UI;
using Xunit;

namespace Prisc.VideoSDK.Tests
{
    /// <summary>
    /// The device pickers' lists (<see cref="AudioDeviceManager"/> and <see cref="CameraDeviceManager"/>)
    /// on top of <see cref="CaptureDevices"/>. They live here because there is no Prisc.Ui test project
    /// and this one references both Prisc.Ui and Prisc.Interop. Nothing here opens a device, so only
    /// the facts about actual devices need hardware, and those skip when the machine reports none.
    /// </summary>
    public class CoreAudioDeviceEnumerationTests
    {
        private static void AssertWellFormed(List<AudioDeviceInfo> devices, string type)
        {
            Assert.NotEmpty(devices);
            Assert.Equal(AudioDeviceManager.DefaultDeviceId, devices[0].DeviceId);
            Assert.All(devices, d => Assert.Equal(type, d.DeviceType));
            Assert.All(devices, d => Assert.False(String.IsNullOrEmpty(d.DeviceId)));
            Assert.Equal(devices.Count, devices.Select(d => d.DeviceId).Distinct(StringComparer.Ordinal).Count());
        }

        [Fact]
        public void Pickers_list_the_default_first_then_unique_devices()
        {
            InteropTestGate.Require();

            AssertWellFormed(AudioDeviceManager.GetMicrophones(), AudioDeviceManager.TypeMicrophone);
            AssertWellFormed(AudioDeviceManager.GetSpeakers(), AudioDeviceManager.TypeSpeaker);
        }

        [Fact]
        public void The_macos_pickers_are_the_hal_lists()
        {
            Assert.SkipUnless(OperatingSystem.IsMacOS(), "the CoreAudio HAL lists are macOS only");
            InteropTestGate.Require();

            var inputs = CaptureDevices.Enumerate(DeviceKind.AudioInput);
            var outputs = CaptureDevices.Enumerate(DeviceKind.AudioOutput);
            Assert.Equal(inputs.Select(d => (d.Id, d.Name)),
                AudioDeviceManager.GetMicrophones().Skip(1).Select(d => (d.DeviceId, d.FriendlyName)));
            Assert.Equal(outputs.Select(d => (d.Id, d.Name)),
                AudioDeviceManager.GetSpeakers().Skip(1).Select(d => (d.DeviceId, d.FriendlyName)));
            Assert.All(inputs, d => Assert.True(d.InputChannels > 0, d.Id + " has no input channels"));
            Assert.All(outputs, d => Assert.Equal(0, d.InputChannels));
            Assert.True(inputs.Count(d => d.IsDefault) <= 1);
            Assert.True(outputs.Count(d => d.IsDefault) <= 1);
        }

        [Fact]
        public void Verify_keeps_a_listed_id_and_maps_anything_else_to_default()
        {
            InteropTestGate.Require();

            var microphones = AudioDeviceManager.GetMicrophones();
            Assert.SkipWhen(microphones.Count < 2, "this machine reports no microphone");
            Assert.Equal(microphones[1].DeviceId, AudioDeviceManager.VerifyMicrophoneOrDefault(microphones[1].DeviceId));
            Assert.Equal(AudioDeviceManager.DefaultDeviceId, AudioDeviceManager.VerifyMicrophoneOrDefault("prisc-no-such-device"));
            Assert.Equal(AudioDeviceManager.DefaultDeviceId, AudioDeviceManager.VerifyMicrophoneOrDefault(null));
        }

        [Fact]
        public void Windows_audio_is_unsupported_in_the_library()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "the Windows answer");
            InteropTestGate.Require();

            // NAudio serves Windows audio (R15); the library says so instead of answering empty.
            var e = Assert.Throws<InteropException>(() => CaptureDevices.Enumerate(DeviceKind.AudioInput));
            Assert.Equal(InteropStatus.Unsupported, e.Status);
        }

        [Fact]
        public void Cameras_have_unique_ids_and_names()
        {
            Assert.SkipUnless(OperatingSystem.IsMacOS() || OperatingSystem.IsWindows(), "cameras are listed on macOS and Windows");
            InteropTestGate.Require();

            var cameras = CaptureDevices.Enumerate(DeviceKind.Video);
            Assert.All(cameras, c => Assert.False(String.IsNullOrEmpty(c.Id)));
            Assert.All(cameras, c => Assert.False(String.IsNullOrEmpty(c.Name)));
            Assert.All(cameras, c => Assert.False(c.IsDefault));
            Assert.Equal(cameras.Count, cameras.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count());

            // The manager takes the native answer as is ("no cameras" included), so it matches.
            var listed = CameraDeviceManager.Refresh();
            Assert.Equal(cameras.Select(c => (c.Id, c.Name)), listed.Select(c => (c.DeviceId, c.FriendlyName)));
        }
    }
}

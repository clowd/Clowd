using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Clowd.UI
{
    /// <summary>
    /// macOS audio device enumeration via CoreAudio P/Invoke — the same three property calls
    /// OBS's own mac enumerator uses (audio-device-enum.c): the system device list, each
    /// device's UID (what obs-express's coreaudio sources take as device_id) and name, with
    /// input/output classification from the stream configuration. Enumeration only; metering
    /// is deferred (see AudioDeviceManager.CreateLevelListener).
    /// </summary>
    [SupportedOSPlatform("macos")]
    internal static class CoreAudioInterop
    {
        private const string CoreAudioLib = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
        private const string CoreFoundationLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        private const uint kAudioObjectSystemObject = 1;

        // four-char codes
        private const uint kAudioHardwarePropertyDevices = 0x64657623;        // 'dev#'
        private const uint kAudioDevicePropertyDeviceUID = 0x75696420;        // 'uid '
        private const uint kAudioObjectPropertyName = 0x6C6E616D;             // 'lnam'
        private const uint kAudioDevicePropertyStreamConfiguration = 0x736C6179; // 'slay'
        private const uint kAudioObjectPropertyScopeGlobal = 0x676C6F62;      // 'glob'
        private const uint kAudioObjectPropertyScopeInput = 0x696E7074;       // 'inpt'
        private const uint kAudioObjectPropertyScopeOutput = 0x6F757470;      // 'outp'
        private const uint kAudioObjectPropertyElementMain = 0;

        private const uint kCFStringEncodingUTF8 = 0x08000100;

        [StructLayout(LayoutKind.Sequential)]
        private struct AudioObjectPropertyAddress
        {
            public uint Selector;
            public uint Scope;
            public uint Element;

            public AudioObjectPropertyAddress(uint selector, uint scope)
            {
                Selector = selector;
                Scope = scope;
                Element = kAudioObjectPropertyElementMain;
            }
        }

        [DllImport(CoreAudioLib)]
        private static extern int AudioObjectGetPropertyDataSize(
            uint objectId, ref AudioObjectPropertyAddress address, uint qualifierSize, IntPtr qualifier, out uint dataSize);

        [DllImport(CoreAudioLib)]
        private static extern int AudioObjectGetPropertyData(
            uint objectId, ref AudioObjectPropertyAddress address, uint qualifierSize, IntPtr qualifier, ref uint dataSize, IntPtr data);

        [DllImport(CoreFoundationLib)]
        private static extern bool CFStringGetCString(IntPtr theString, byte[] buffer, long bufferSize, uint encoding);

        [DllImport(CoreFoundationLib)]
        private static extern void CFRelease(IntPtr cf);

        /// <summary>Enumerates output (speakers) or input (microphones) devices. Returns an empty
        /// list on any failure — the caller always prepends the "default" pseudo-device.</summary>
        public static List<AudioDeviceInfo> GetDevices(bool output, string deviceType)
        {
            var result = new List<AudioDeviceInfo>();
            try
            {
                var listAddr = new AudioObjectPropertyAddress(kAudioHardwarePropertyDevices, kAudioObjectPropertyScopeGlobal);
                if (AudioObjectGetPropertyDataSize(kAudioObjectSystemObject, ref listAddr, 0, IntPtr.Zero, out var size) != 0 || size == 0)
                    return result;

                var count = (int)(size / 4);
                var raw = new int[count];
                var buffer = Marshal.AllocHGlobal((int)size);
                try
                {
                    if (AudioObjectGetPropertyData(kAudioObjectSystemObject, ref listAddr, 0, IntPtr.Zero, ref size, buffer) != 0)
                        return result;
                    Marshal.Copy(buffer, raw, 0, count);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }

                var ids = new uint[count];
                for (int i = 0; i < count; i++)
                    ids[i] = unchecked((uint)raw[i]);

                foreach (var id in ids)
                {
                    if (!HasStreams(id, output))
                        continue;

                    var uid = GetStringProperty(id, kAudioDevicePropertyDeviceUID);
                    if (String.IsNullOrEmpty(uid))
                        continue;

                    var name = GetStringProperty(id, kAudioObjectPropertyName) ?? uid;
                    result.Add(new AudioDeviceInfo(uid, deviceType, name));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("CoreAudio enumeration failed: " + ex.Message);
            }

            return result;
        }

        private static bool HasStreams(uint deviceId, bool output)
        {
            var scope = output ? kAudioObjectPropertyScopeOutput : kAudioObjectPropertyScopeInput;
            var addr = new AudioObjectPropertyAddress(kAudioDevicePropertyStreamConfiguration, scope);

            if (AudioObjectGetPropertyDataSize(deviceId, ref addr, 0, IntPtr.Zero, out var size) != 0 || size < 4)
                return false;

            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (AudioObjectGetPropertyData(deviceId, ref addr, 0, IntPtr.Zero, ref size, buffer) != 0)
                    return false;

                // AudioBufferList starts with UInt32 mNumberBuffers
                return Marshal.ReadInt32(buffer) > 0;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static string GetStringProperty(uint deviceId, uint selector)
        {
            var addr = new AudioObjectPropertyAddress(selector, kAudioObjectPropertyScopeGlobal);
            var size = (uint)IntPtr.Size;
            var holder = Marshal.AllocHGlobal(IntPtr.Size);
            try
            {
                if (AudioObjectGetPropertyData(deviceId, ref addr, 0, IntPtr.Zero, ref size, holder) != 0)
                    return null;

                var cfString = Marshal.ReadIntPtr(holder);
                if (cfString == IntPtr.Zero)
                    return null;

                try
                {
                    var bytes = new byte[1024];
                    if (!CFStringGetCString(cfString, bytes, bytes.Length, kCFStringEncodingUTF8))
                        return null;

                    var len = Array.IndexOf(bytes, (byte)0);
                    return Encoding.UTF8.GetString(bytes, 0, len < 0 ? bytes.Length : len);
                }
                finally
                {
                    CFRelease(cfString);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(holder);
            }
        }
    }
}

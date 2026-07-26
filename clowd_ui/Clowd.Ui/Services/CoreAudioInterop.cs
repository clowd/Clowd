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
    /// input/output classification from the stream configuration. Also hosts the HAL IOProc and
    /// process-tap plumbing behind CoreAudioLevelListener's peak metering.
    /// </summary>
    [SupportedOSPlatform("macos")]
    internal static class CoreAudioInterop
    {
        private const string CoreAudioLib = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
        private const string CoreFoundationLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        private const uint kAudioObjectSystemObject = 1;

        // four-char codes
        private const uint kAudioHardwarePropertyDevices = 0x64657623;        // 'dev#'
        private const uint kAudioHardwarePropertyDefaultInputDevice = 0x64496E20;  // 'dIn '
        private const uint kAudioHardwarePropertyDefaultOutputDevice = 0x644F7574; // 'dOut'
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
                foreach (var id in GetAllDeviceIds())
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
                SentryConfig.CaptureHandled(ex, "audio.coreaudio-enumerate");
            }

            return result;
        }

        private static uint[] GetAllDeviceIds()
        {
            var listAddr = new AudioObjectPropertyAddress(kAudioHardwarePropertyDevices, kAudioObjectPropertyScopeGlobal);
            if (AudioObjectGetPropertyDataSize(kAudioObjectSystemObject, ref listAddr, 0, IntPtr.Zero, out var size) != 0 || size == 0)
                return Array.Empty<uint>();

            var count = (int)(size / 4);
            var raw = new int[count];
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (AudioObjectGetPropertyData(kAudioObjectSystemObject, ref listAddr, 0, IntPtr.Zero, ref size, buffer) != 0)
                    return Array.Empty<uint>();
                Marshal.Copy(buffer, raw, 0, count);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            var ids = new uint[count];
            for (int i = 0; i < count; i++)
                ids[i] = unchecked((uint)raw[i]);
            return ids;
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

        // -- peak metering support (CoreAudioLevelListener) --

        /// <summary>HAL IO callback; input/output point at AudioBufferLists (Float32 samples).</summary>
        public delegate int AudioDeviceIOProc(
            uint device, IntPtr now, IntPtr inputData, IntPtr inputTime, IntPtr outputData, IntPtr outputTime, IntPtr clientData);

        [DllImport(CoreAudioLib)]
        public static extern int AudioDeviceCreateIOProcID(uint device, AudioDeviceIOProc proc, IntPtr clientData, out IntPtr procId);

        [DllImport(CoreAudioLib)]
        public static extern int AudioDeviceDestroyIOProcID(uint device, IntPtr procId);

        [DllImport(CoreAudioLib)]
        public static extern int AudioDeviceStart(uint device, IntPtr procId);

        [DllImport(CoreAudioLib)]
        public static extern int AudioDeviceStop(uint device, IntPtr procId);

        [DllImport(CoreAudioLib)]
        private static extern int AudioHardwareCreateProcessTap(IntPtr tapDescription, out uint tapId);

        [DllImport(CoreAudioLib)]
        public static extern int AudioHardwareDestroyProcessTap(uint tapId);

        [DllImport(CoreAudioLib)]
        private static extern int AudioHardwareCreateAggregateDevice(IntPtr description, out uint deviceId);

        [DllImport(CoreAudioLib)]
        public static extern int AudioHardwareDestroyAggregateDevice(uint deviceId);

        /// <summary>Resolves "default" or a device UID to an AudioObjectID; 0 when not found.</summary>
        public static uint FindDevice(string deviceId, bool output)
        {
            if (String.IsNullOrEmpty(deviceId) ||
                String.Equals(deviceId, AudioDeviceManager.DefaultDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                return GetUIntProperty(kAudioObjectSystemObject,
                                       output ? kAudioHardwarePropertyDefaultOutputDevice : kAudioHardwarePropertyDefaultInputDevice);
            }

            foreach (var id in GetAllDeviceIds())
            {
                if (GetStringProperty(id, kAudioDevicePropertyDeviceUID) == deviceId)
                    return id;
            }

            return 0;
        }

        /// <summary>
        /// Creates a global stereo-mixdown process tap plus a private aggregate device wired to
        /// it (macOS 14.2+); the aggregate's input IOProc then carries the system audio mix.
        /// The first call ever triggers the "record system audio" permission prompt. Throws on
        /// failure; on success the caller owns both ids (destroy aggregate first, then tap).
        /// </summary>
        public static (uint TapId, uint AggregateId) CreateSystemAudioTap()
        {
            // CATapDescription lives in CoreAudio.framework; make sure it is mapped before
            // objc_getClass (P/Invokes load it lazily, and this may be the first call).
            NativeLibrary.Load(CoreAudioLib);

            // no autorelease pool exists on this (background) thread; everything convenience-
            // constructed below is autoreleased.
            var pool = MsgSend(MsgSend(ObjCClass("NSAutoreleasePool"), Sel("alloc")), Sel("init"));
            try
            {
                var descClass = ObjCClass("CATapDescription");
                if (descClass == IntPtr.Zero)
                    throw new NotSupportedException("CATapDescription is not available (requires macOS 14.2+).");

                var emptyArray = MsgSend(ObjCClass("NSArray"), Sel("array"));
                var desc = MsgSend(MsgSend(descClass, Sel("alloc")), Sel("initStereoGlobalTapButExcludeProcesses:"), emptyArray);
                if (desc == IntPtr.Zero)
                    throw new InvalidOperationException("Failed to create CATapDescription.");

                uint tapId = 0;
                try
                {
                    MsgSendBool(desc, Sel("setPrivate:"), true);

                    var status = AudioHardwareCreateProcessTap(desc, out tapId);
                    if (status != 0)
                        throw new InvalidOperationException($"AudioHardwareCreateProcessTap failed ({status}).");

                    var tapUid = MsgSend(MsgSend(desc, Sel("UUID")), Sel("UUIDString"));
                    var subTap = Dict(("uid", tapUid), ("drift", Num(1)));

                    // Deliberately tap-only: adding the physical output device as a sub-device
                    // (Apple's AudioCap shape) force-activates it, which was observed to wedge
                    // the HAL when the default output is an idle Bluetooth device. A tap-only
                    // aggregate still delivers IO callbacks, which is all a meter needs.
                    var aggDesc = Dict(
                        ("uid", Str(Guid.NewGuid().ToString())),
                        ("name", Str("Clowd speaker meter")),
                        ("private", Num(1)),
                        ("tapautostart", Num(1)),
                        ("taps", MsgSend(ObjCClass("NSArray"), Sel("arrayWithObject:"), subTap)));

                    status = AudioHardwareCreateAggregateDevice(aggDesc, out var aggregateId);
                    if (status != 0)
                        throw new InvalidOperationException($"AudioHardwareCreateAggregateDevice failed ({status}).");

                    return (tapId, aggregateId);
                }
                catch
                {
                    if (tapId != 0)
                        AudioHardwareDestroyProcessTap(tapId);
                    throw;
                }
                finally
                {
                    MsgSend(desc, Sel("release"));
                }
            }
            finally
            {
                MsgSend(pool, Sel("drain"));
            }
        }

        private static uint GetUIntProperty(uint objectId, uint selector)
        {
            var addr = new AudioObjectPropertyAddress(selector, kAudioObjectPropertyScopeGlobal);
            var size = 4u;
            var holder = Marshal.AllocHGlobal(4);
            try
            {
                if (AudioObjectGetPropertyData(objectId, ref addr, 0, IntPtr.Zero, ref size, holder) != 0)
                    return 0;
                return unchecked((uint)Marshal.ReadInt32(holder));
            }
            finally
            {
                Marshal.FreeHGlobal(holder);
            }
        }

        // -- minimal ObjC runtime bridge for the tap/aggregate descriptions --

        private const string ObjCLib = "/usr/lib/libobjc.A.dylib";

        [DllImport(ObjCLib)]
        private static extern IntPtr objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        [DllImport(ObjCLib)]
        private static extern IntPtr sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
        private static extern IntPtr MsgSend(IntPtr receiver, IntPtr sel);

        [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
        private static extern IntPtr MsgSend(IntPtr receiver, IntPtr sel, IntPtr a);

        [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
        private static extern IntPtr MsgSend(IntPtr receiver, IntPtr sel, IntPtr a, IntPtr b);

        [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
        private static extern IntPtr MsgSendStr(IntPtr receiver, IntPtr sel, [MarshalAs(UnmanagedType.LPUTF8Str)] string s);

        [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
        private static extern IntPtr MsgSendInt(IntPtr receiver, IntPtr sel, int v);

        [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
        private static extern void MsgSendBool(IntPtr receiver, IntPtr sel, [MarshalAs(UnmanagedType.I1)] bool v);

        private static IntPtr ObjCClass(string name) => objc_getClass(name);

        private static IntPtr Sel(string name) => sel_registerName(name);

        private static IntPtr Str(string s) => MsgSendStr(ObjCClass("NSString"), Sel("stringWithUTF8String:"), s);

        private static IntPtr Num(int v) => MsgSendInt(ObjCClass("NSNumber"), Sel("numberWithInt:"), v);

        private static IntPtr Dict(params (string Key, IntPtr Value)[] entries)
        {
            var dict = MsgSend(ObjCClass("NSMutableDictionary"), Sel("dictionary"));
            foreach (var (key, value) in entries)
                MsgSend(dict, Sel("setObject:forKey:"), value, Str(key));
            return dict;
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

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Prisc.Interop;
using Prisc.Ipc;

namespace Prisc.UI
{
    /// <summary>An enumerated camera. DeviceId is what prisc_recorder consumes verbatim (Windows:
    /// DirectShow device path; macOS: AVFoundation unique id).</summary>
    public sealed record CameraDeviceInfo(string DeviceId, string FriendlyName);

    /// <summary>
    /// Camera enumeration (same API shape as <see cref="AudioDeviceManager"/>). Asks the platform
    /// directly through <see cref="CaptureDevices"/> (prisc_interop: AVFoundation on macOS,
    /// DirectShow on Windows), which reproduces what OBS's own enumerators do so the ids are the ids
    /// prisc_recorder can open (crates/prisc_interop/src/ffi/devices.rs says how each id is derived,
    /// which is the part that has to stay faithful).
    ///
    /// The fallback, and what this used to do exclusively, is spawning prisc_recorder with
    /// <c>--list-cameras</c>: authoritative, but measured at 5.2 s on macOS — not the process
    /// spawn (libobs is up in 0.16 s) but something inside mac-avcapture's property callback,
    /// while an AVFoundation discovery session answers the same question in under 0.1 s. It is
    /// kept for the case the native path cannot answer at all.
    ///
    /// The result is cached for the life of the process; call <see cref="Refresh"/> to
    /// re-enumerate after a hot-plug, which is now cheap enough to do on every dropdown.
    ///
    /// Nothing here throws: a missing binary, a failed or mismatched recorder, or a timeout all come
    /// back as an empty list (logged). A user with no cameras and a
    /// user whose recorder cannot list them look the same on purpose — neither can record one.
    /// </summary>
    public static class CameraDeviceManager
    {
        /// <summary>The recorder flag; it answers with a <c>cameras</c> terminal and exits without
        /// touching OBS.</summary>
        public const string ListCamerasFlag = "--list-cameras";

        // the fallback spawn opens each capture device long enough to read its name, which a
        // wedged driver can stall; past this the child is killed and the user gets an empty list.
        // Generous because the floor is already ~5 s on a healthy machine (the old 5 s budget lost
        // that race and reported "no cameras" on a machine that had one), and nothing waits on
        // this synchronously — the app warms the cache at startup and the pickers read it.
        private static readonly TimeSpan EnumerateTimeout = TimeSpan.FromSeconds(20);

        private static readonly object _lock = new();
        private static List<CameraDeviceInfo> _cache;

        /// <summary>The cameras attached to this machine, enumerated once per app run. Blocks for
        /// up to <see cref="EnumerateTimeout"/> on the first call; prefer
        /// <see cref="GetCamerasAsync"/> from the UI thread.</summary>
        public static List<CameraDeviceInfo> GetCameras()
        {
            lock (_lock)
            {
                if (_cache != null)
                    return new List<CameraDeviceInfo>(_cache);
            }

            var enumerated = Enumerate();

            lock (_lock)
            {
                _cache ??= enumerated;
                return new List<CameraDeviceInfo>(_cache);
            }
        }

        /// <summary>Off-thread <see cref="GetCameras"/>, for callers on the UI thread.</summary>
        public static Task<List<CameraDeviceInfo>> GetCamerasAsync() => Task.Run(() => GetCameras());

        /// <summary>Drops the cache and enumerates again (device hot-plug). Blocks like
        /// <see cref="GetCameras"/>.</summary>
        public static List<CameraDeviceInfo> Refresh()
        {
            var enumerated = Enumerate();

            lock (_lock)
            {
                _cache = enumerated;
                return new List<CameraDeviceInfo>(_cache);
            }
        }

        /// <summary>Off-thread <see cref="Refresh"/>.</summary>
        public static Task<List<CameraDeviceInfo>> RefreshAsync() => Task.Run(() => Refresh());

        /// <summary>Asks the platform, falling back to the recorder when it cannot answer.
        /// "No cameras" is an answer and is taken at face value; only a native path that failed
        /// outright (null) falls through, so a machine with no camera does not pay for a spawn.</summary>
        private static List<CameraDeviceInfo> Enumerate()
        {
            var native = EnumerateNative();
            if (native != null)
                return native;

            return EnumerateWithRecorder();
        }

        /// <summary>The cameras the platform reports, or null when it could not be asked at all (so
        /// the caller can fall back), never "no cameras" for a failure.</summary>
        private static List<CameraDeviceInfo> EnumerateNative()
        {
            // no third platform ships a recorder either, so this ends at the same empty list.
            if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows())
                return null;

            var cameras = CaptureDevices.TryEnumerate(DeviceKind.Video, out var error);
            if (cameras == null)
            {
                // a missing framework, a failed COM call: all of them mean "ask the recorder
                // instead", never "this machine has no cameras".
                Debug.WriteLine("Native camera enumeration failed: " + error);
                SentryConfig.CaptureHandled(error, OperatingSystem.IsMacOS() ? "camera.avfoundation" : "camera.directshow");
                return null;
            }

            return cameras.Select(camera => new CameraDeviceInfo(camera.Id, camera.Name)).ToList();
        }

        private static List<CameraDeviceInfo> EnumerateWithRecorder()
        {
            var empty = new List<CameraDeviceInfo>();

            var exePath = HelperLocator.Find("prisc_recorder");
            if (String.IsNullOrEmpty(exePath))
            {
                Debug.WriteLine("Cannot enumerate cameras: prisc_recorder was not found.");
                return empty;
            }

            try
            {
                // never on the UI thread (GetCamerasAsync), and the client awaits nothing on a
                // captured context, so blocking on it here cannot deadlock.
                return EnumerateWithRecorderAsync(exePath).GetAwaiter().GetResult() ?? empty;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to enumerate cameras: " + ex);
                SentryConfig.CaptureHandled(ex, "camera.enumerate");
                return empty;
            }
        }

        /// <summary>The recorder's <c>cameras</c> terminal, or null when it ended any other way.</summary>
        private static async Task<List<CameraDeviceInfo>> EnumerateWithRecorderAsync(string exePath)
        {
            // one budget for the whole enumeration, start included, like the single exit wait it replaced
            var elapsed = Stopwatch.StartNew();
            HelperClient<RecorderEvent, RecorderCommand> client;
            try
            {
                client = await HelperClient<RecorderEvent, RecorderCommand>.StartAsync(HelperSpecs.Recorder, exePath,
                    new[] { ListCamerasFlag }, new HelperStartOptions { HelloTimeout = EnumerateTimeout }).ConfigureAwait(false);
            }
            catch (HelperStartException ex)
            {
                Debug.WriteLine("Cannot enumerate cameras: " + ex.Message);
                return null;
            }

            await using (client.ConfigureAwait(false))
            {
                HelperOutcome<RecorderEvent> outcome;
                try
                {
                    var left = EnumerateTimeout - elapsed.Elapsed;
                    outcome = await client.Completion.WaitAsync(left > TimeSpan.Zero ? left : TimeSpan.Zero).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    Debug.WriteLine($"Camera enumeration did not finish within {EnumerateTimeout.TotalSeconds:0}s; killing it.");
                    client.Kill();
                    return null;
                }

                if (outcome.Terminal is not RecorderEvent.Cameras cameras)
                {
                    Debug.WriteLine($"Camera enumeration produced no camera list ({outcome.Describe()}). {Truncate(outcome.StderrTail)}");
                    return null;
                }

                var list = new List<CameraDeviceInfo>();
                foreach (var camera in cameras.Items ?? Array.Empty<RecorderCamera>())
                {
                    if (String.IsNullOrEmpty(camera?.Id))
                        continue;
                    list.Add(new CameraDeviceInfo(camera.Id, String.IsNullOrEmpty(camera.Name) ? camera.Id : camera.Name));
                }

                return list;
            }
        }

        private static string Truncate(string text)
        {
            if (String.IsNullOrWhiteSpace(text))
                return "";

            text = text.Trim();
            return text.Length <= 500 ? text : text.Substring(0, 500) + "…";
        }
    }
}

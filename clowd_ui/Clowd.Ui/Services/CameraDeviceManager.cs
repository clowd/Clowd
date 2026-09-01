using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace Clowd.UI
{
    /// <summary>An enumerated camera. DeviceId is what obs-express consumes verbatim (Windows:
    /// DirectShow device path; macOS: AVFoundation unique id).</summary>
    public sealed record CameraDeviceInfo(string DeviceId, string FriendlyName);

    /// <summary>
    /// Camera enumeration (same API shape as <see cref="AudioDeviceManager"/>). Asks the platform
    /// directly — AVFoundation on macOS, DirectShow on Windows — reproducing what OBS's own
    /// enumerators do so the ids are the ids obs-express can open. See
    /// <see cref="AvCaptureInterop"/> and <see cref="DirectShowInterop"/> for how each id is
    /// derived, which is the part that has to stay faithful.
    ///
    /// The fallback, and what this used to do exclusively, is spawning obs-express with
    /// <c>--list-cameras</c>: authoritative, but measured at 5.2 s on macOS — not the process
    /// spawn (libobs is up in 0.16 s) but something inside mac-avcapture's property callback,
    /// while an AVFoundation discovery session answers the same question in under 0.1 s. It is
    /// kept for the case the native path cannot answer at all.
    ///
    /// The result is cached for the life of the process; call <see cref="Refresh"/> to
    /// re-enumerate after a hot-plug, which is now cheap enough to do on every dropdown.
    ///
    /// Nothing here throws: a missing binary, a recorder too old to know the flag, a timeout or
    /// unparseable output all come back as an empty list (logged). A user with no cameras and a
    /// user whose recorder cannot list them look the same on purpose — neither can record one.
    /// </summary>
    public static class CameraDeviceManager
    {
        /// <summary>The recorder flag; it prints the camera list and exits without touching OBS.</summary>
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

        private static List<CameraDeviceInfo> EnumerateNative()
        {
            if (OperatingSystem.IsMacOS())
                return AvCaptureInterop.GetCameras();

            if (OperatingSystem.IsWindows())
                return DirectShowInterop.GetCameras();

            // no third platform ships a recorder either, so this ends at the same empty list.
            return null;
        }

        private static List<CameraDeviceInfo> EnumerateWithRecorder()
        {
            var empty = new List<CameraDeviceInfo>();

            var exePath = ObsBinaryLocator.Resolve();
            if (String.IsNullOrEmpty(exePath))
            {
                Debug.WriteLine("Cannot enumerate cameras: " + ObsBinaryLocator.BinaryFileName + " was not found.");
                return empty;
            }

            Process proc = null;
            try
            {
                var psi = new ProcessStartInfo(exePath)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    // obs-express loads the OBS libraries that sit beside it relative to the
                    // working directory — it cannot start anywhere else.
                    WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(exePath)),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                psi.ArgumentList.Add(ListCamerasFlag);

                proc = Process.Start(psi);
                if (proc == null)
                {
                    Debug.WriteLine("Cannot enumerate cameras: the recorder process did not start.");
                    return empty;
                }

                // read stdout and stderr concurrently: both are small here, but a full pipe buffer
                // on either one would deadlock a sequential read.
                var stdout = proc.StandardOutput.ReadToEndAsync();
                var stderr = proc.StandardError.ReadToEndAsync();

                if (!proc.WaitForExit((int)EnumerateTimeout.TotalMilliseconds))
                {
                    Debug.WriteLine($"Camera enumeration did not finish within {EnumerateTimeout.TotalSeconds:0}s; killing it.");
                    KillQuietly(proc);
                    return empty;
                }

                var output = stdout.GetAwaiter().GetResult();
                var cameras = Parse(output);

                if (cameras == null)
                {
                    // an older recorder rejects the flag outright (clap exits 2 with a usage
                    // error): expected until the recorder side ships, so it is a log line, not a
                    // report. The webcam settings simply offer nothing to pick.
                    var error = stderr.GetAwaiter().GetResult();
                    Debug.WriteLine($"Camera enumeration produced no camera list (exit {proc.ExitCode}). {Truncate(error)}");
                    return empty;
                }

                return cameras;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to enumerate cameras: " + ex);
                SentryConfig.CaptureHandled(ex, "camera.enumerate");
                KillQuietly(proc);
                return empty;
            }
            finally
            {
                try { proc?.Dispose(); }
                catch { }
            }
        }

        /// <summary>Finds the <c>cameras</c> line in the recorder's stdout and reads it. Returns
        /// null when there is no such line (so the caller can tell "this recorder cannot list
        /// cameras" apart from "this machine has none").</summary>
        private static List<CameraDeviceInfo> Parse(string output)
        {
            if (String.IsNullOrWhiteSpace(output))
                return null;

            foreach (var raw in output.Split('\n'))
            {
                // same protocol rule as the recording stream: only lines that are a whole JSON
                // object are ours, everything else is chatter.
                var line = raw.Trim();
                if (!line.StartsWith("{", StringComparison.Ordinal) || !line.EndsWith("}", StringComparison.Ordinal))
                    continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("type", out var typeEl) ||
                        !String.Equals(typeEl.GetString(), "cameras", StringComparison.Ordinal))
                        continue;

                    var list = new List<CameraDeviceInfo>();
                    if (root.TryGetProperty("cameras", out var camerasEl) && camerasEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var camera in camerasEl.EnumerateArray())
                        {
                            var id = camera.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                            if (String.IsNullOrEmpty(id))
                                continue;

                            var name = camera.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                            list.Add(new CameraDeviceInfo(id, String.IsNullOrEmpty(name) ? id : name));
                        }
                    }

                    return list;
                }
                catch (JsonException ex)
                {
                    Debug.WriteLine("Unparseable camera list line: " + line + " (" + ex.Message + ")");
                }
            }

            return null;
        }

        private static void KillQuietly(Process proc)
        {
            try
            {
                if (proc != null && !proc.HasExited)
                    proc.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to kill the camera enumeration process: " + ex.Message);
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

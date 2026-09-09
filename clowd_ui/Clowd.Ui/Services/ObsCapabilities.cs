using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Clowd.UI
{
    /// <summary>
    /// What the resolved obs-express binary can be asked for. Clowd spawns whatever recorder it
    /// finds (an env var, a sibling cargo checkout, the bundled copy), so the build being run is
    /// not knowable from Clowd's own version, and the CLI declares no --version flag to ask. The
    /// probe is therefore "does --help list the flag": clap generates that text from the same
    /// derive that defines the arguments, so the flag appears if and only if it is accepted.
    ///
    /// This matters more than an ordinary feature check. An unknown argument makes clap print a
    /// usage error and exit 2 during parsing, before any protocol reaches stdout, so passing a
    /// flag an older recorder does not know loses the whole take rather than just the sidecar.
    /// Every failure mode here — a missing binary, a start failure, a timeout, an exception —
    /// therefore reads as "not supported": omitting the flag costs a feature, guessing costs a
    /// recording. Cached per binary identity (path, size, write time) for the life of the process.
    /// </summary>
    public static class ObsCapabilities
    {
        private const string WindowCaptureFlag = "--window-capture";
        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

        private static readonly object Sync = new object();
        // the TASK is cached, not the answer, so concurrent callers share one probe and the lock
        // is never held while a child process runs.
        private static readonly Dictionary<string, Task<bool>> Probes =
            new Dictionary<string, Task<bool>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Whether <paramref name="binaryPath"/> accepts <c>--window-capture</c>
        /// (obs-express 0.9.0 and later).</summary>
        public static Task<bool> SupportsWindowCaptureAsync(string binaryPath)
        {
            if (String.IsNullOrEmpty(binaryPath))
                return Task.FromResult(false);

            string key;
            try
            {
                var info = new FileInfo(binaryPath);
                if (!info.Exists)
                    return Task.FromResult(false);
                key = FormattableString.Invariant(
                    $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Could not stat the recorder for a capability probe: " + ex.Message);
                return Task.FromResult(false);
            }

            lock (Sync)
            {
                if (Probes.TryGetValue(key, out var existing))
                    return existing;

                var probe = Task.Run(() => HelpMentions(binaryPath, WindowCaptureFlag));
                Probes[key] = probe;
                return probe;
            }
        }

        private static bool HelpMentions(string exePath, string flag)
        {
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
                psi.ArgumentList.Add("--help");

                proc = Process.Start(psi);
                if (proc == null)
                {
                    Debug.WriteLine("Recorder capability probe: the process did not start.");
                    return false;
                }

                // read both concurrently: a full pipe buffer on either would deadlock a
                // sequential read.
                var stdout = proc.StandardOutput.ReadToEndAsync();
                var stderr = proc.StandardError.ReadToEndAsync();

                if (!proc.WaitForExit((int)ProbeTimeout.TotalMilliseconds))
                {
                    Debug.WriteLine($"Recorder capability probe did not finish within {ProbeTimeout.TotalSeconds:0}s; killing it.");
                    KillQuietly(proc);
                    return false;
                }

                // clap prints help to stdout on success and to stderr on a usage error; read both
                // so an odd build is still detected rather than assumed old.
                return stdout.GetAwaiter().GetResult().Contains(flag, StringComparison.Ordinal)
                    || stderr.GetAwaiter().GetResult().Contains(flag, StringComparison.Ordinal);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Recorder capability probe failed: " + ex.Message);
                KillQuietly(proc);
                return false;
            }
            finally
            {
                try { proc?.Dispose(); }
                catch { }
            }
        }

        private static void KillQuietly(Process proc)
        {
            try { proc?.Kill(entireProcessTree: true); }
            catch { }
        }
    }
}

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
        /// <summary>What the <c>--output</c> help line says once the recorder accepts a Matroska
        /// path (obs-express builds after 0.9.2). Not a flag but the same idea: the text comes from the
        /// argument's doc comment, which was changed in the commit that relaxed the check.</summary>
        private const string MkvOutputMention = ".mkv";
        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

        private static readonly object Sync = new object();
        // the TASK is cached, not the answer, so concurrent callers share one probe and the lock
        // is never held while a child process runs. What is cached is the help text itself, so
        // every capability question about one binary costs one spawn between them.
        private static readonly Dictionary<string, Task<string>> Probes =
            new Dictionary<string, Task<string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Whether <paramref name="binaryPath"/> accepts <c>--window-capture</c>
        /// (obs-express 0.9.0 and later).</summary>
        public static Task<bool> SupportsWindowCaptureAsync(string binaryPath)
            => HelpMentionsAsync(binaryPath, WindowCaptureFlag);

        /// <summary>Whether <paramref name="binaryPath"/> accepts an <c>--output</c> path ending
        /// in <c>.mkv</c> (obs-express builds after 0.9.2; single-track recordings only). An older
        /// recorder rejects the path during CLI validation with exit 2, which like an unknown flag
        /// costs the whole take, so the caller falls back to .mp4 when this says no.</summary>
        public static Task<bool> SupportsMkvOutputAsync(string binaryPath)
            => HelpMentionsAsync(binaryPath, MkvOutputMention);

        private static async Task<bool> HelpMentionsAsync(string binaryPath, string text)
        {
            var help = await GetHelpTextAsync(binaryPath).ConfigureAwait(false);
            return help != null && help.Contains(text, StringComparison.Ordinal);
        }

        /// <summary>The recorder's <c>--help</c> output (stdout and stderr concatenated), or null
        /// when it could not be obtained — every failure mode reads as "supports nothing".</summary>
        private static Task<string> GetHelpTextAsync(string binaryPath)
        {
            if (String.IsNullOrEmpty(binaryPath))
                return Task.FromResult<string>(null);

            string key;
            try
            {
                var info = new FileInfo(binaryPath);
                if (!info.Exists)
                    return Task.FromResult<string>(null);
                key = FormattableString.Invariant(
                    $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Could not stat the recorder for a capability probe: " + ex.Message);
                return Task.FromResult<string>(null);
            }

            lock (Sync)
            {
                if (Probes.TryGetValue(key, out var existing))
                    return existing;

                var probe = Task.Run(() => ReadHelpText(binaryPath));
                Probes[key] = probe;
                return probe;
            }
        }

        private static string ReadHelpText(string exePath)
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
                    return null;
                }

                // read both concurrently: a full pipe buffer on either would deadlock a
                // sequential read.
                var stdout = proc.StandardOutput.ReadToEndAsync();
                var stderr = proc.StandardError.ReadToEndAsync();

                if (!proc.WaitForExit((int)ProbeTimeout.TotalMilliseconds))
                {
                    Debug.WriteLine($"Recorder capability probe did not finish within {ProbeTimeout.TotalSeconds:0}s; killing it.");
                    KillQuietly(proc);
                    return null;
                }

                // clap prints help to stdout on success and to stderr on a usage error; read both
                // so an odd build is still detected rather than assumed old.
                return stdout.GetAwaiter().GetResult() + Environment.NewLine + stderr.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Recorder capability probe failed: " + ex.Message);
                KillQuietly(proc);
                return null;
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

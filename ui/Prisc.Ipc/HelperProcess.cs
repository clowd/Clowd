using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Prisc.Ipc
{
    /// <summary>How a helper is spawned (HELPER_PROTOCOL.md §7.2). The defaults suit every line-protocol
    /// helper; each runner overrides only what its helper needs.</summary>
    public sealed record HelperStartOptions
    {
        /// <summary>Assign the child to the process-wide kill-on-close job (§7.4), so it dies with
        /// Prisc.Ui on Windows. The recorder opts out: its parent-death path is stdin EOF, which
        /// stops and flushes the recording instead of truncating it.</summary>
        public bool KillWithParent { get; init; } = true;

        /// <summary>Hand the child our foreground rights (<c>AllowSetForegroundWindow</c>), for
        /// helpers that put a window of their own on screen.</summary>
        public bool GrantForeground { get; init; }

        /// <summary>Extra environment variables for the child, on top of our own.</summary>
        public IReadOnlyDictionary<string, string> Environment { get; init; }

        /// <summary>Defaults to the executable's own directory, where its native libraries live.</summary>
        public string WorkingDirectory { get; init; }

        /// <summary>How long the child gets to say <c>hello</c>. A freshly updated helper can sit in
        /// Windows Defender's block-at-first-sight check and must load its load-time-linked DLLs
        /// before <c>main</c> runs, so this matches the old readiness timeouts.</summary>
        public TimeSpan HelloTimeout { get; init; } = TimeSpan.FromSeconds(30);

        /// <summary>Lines of stderr (and non-protocol stdout) kept in the <see cref="HelperLog"/>.</summary>
        public int StderrLines { get; init; } = 1000;

        /// <summary>A second cap on the same buffer, in characters.</summary>
        public int StderrChars { get; init; } = 256 * 1024;
    }

    /// <summary>
    /// The semantics-free plumbing every helper spawn repeats: build the
    /// <see cref="ProcessStartInfo"/>, spawn, put the child in the kill-on-close job, hand it the
    /// foreground, and join the stdio pumps at the end. Nothing here interprets a protocol line;
    /// that is <see cref="HelperClient{TEvent, TCommand}"/>'s job (and, for the binary payload of
    /// prisc_ai, <c>AiClient</c>'s).
    /// </summary>
    public static class HelperProcess
    {
        /// <summary>
        /// Spawns <paramref name="exePath"/> with all three stdio streams redirected.
        /// <para>
        /// Every option here is load-bearing. <c>UseShellExecute=false</c> is what makes redirection
        /// possible at all; <c>CreateNoWindow=true</c> keeps a console-subsystem debug build from
        /// flashing a console over the user's screen. The working directory is the binary's own
        /// folder because these helpers load native libraries (libobs, FFmpeg, ONNX Runtime) that
        /// ship beside them. All three streams are redirected even when a driver never intends to
        /// write stdin: the open stdin pipe is the child's parent-lifetime lease (EOF means the
        /// parent is gone), and an unredirected stdout inherits the parent's, which for a
        /// GUI-subsystem Prisc.Ui is nothing at all.
        /// </para>
        /// <para>
        /// The two encoding-and-flush details are the ones hand-rolled copies kept missing. .NET's
        /// stdin writer does not auto-flush, so without <c>AutoFlush</c> a <c>quit</c> sits in the
        /// writer's buffer forever and the graceful shutdown silently becomes a kill. And the
        /// default console encoding is the OS ANSI code page, so a path or window title outside it
        /// would round-trip through mojibake in both directions; these helpers all speak UTF-8. The
        /// BOM is suppressed because a BOM written to stdin is a leading garbage character on the
        /// helper's first command line.
        /// </para>
        /// </summary>
        /// <param name="log">Where a failed job assignment is noted; may be null.</param>
        /// <exception cref="InvalidOperationException">The process could not be started.</exception>
        public static Process Start(string exePath, IReadOnlyList<string> args, HelperStartOptions options = null,
            HelperLog log = null)
        {
            if (String.IsNullOrEmpty(exePath))
                throw new ArgumentException("A helper process needs an executable path.", nameof(exePath));

            options ??= new HelperStartOptions();

            // no BOM: the helper reads its stdin as lines of text, and a BOM is not whitespace.
            var utf8 = new UTF8Encoding(false);

            var psi = new ProcessStartInfo(exePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = String.IsNullOrEmpty(options.WorkingDirectory)
                    ? Path.GetDirectoryName(Path.GetFullPath(exePath))
                    : options.WorkingDirectory,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = utf8,
                StandardOutputEncoding = utf8,
                StandardErrorEncoding = utf8,
            };

            if (args != null)
            {
                // ArgumentList, never a pre-joined string: it quotes each argument for the platform,
                // which is what keeps paths with spaces and negative coordinates intact.
                foreach (var arg in args)
                    psi.ArgumentList.Add(arg);
            }

            if (options.Environment != null)
            {
                foreach (var (key, value) in options.Environment)
                    psi.Environment[key] = value;
            }

            Debug.WriteLine("Starting helper process: " + exePath + " "
                            + String.Join(" ", args ?? (IReadOnlyList<string>)Array.Empty<string>()));

            var proc = Process.Start(psi);
            if (proc == null)
                throw new InvalidOperationException("Failed to start helper process: " + exePath);

            if (options.KillWithParent)
                JobObject.TryAssign(proc, log);

            if (options.GrantForeground)
                GrantForeground(proc);

            proc.StandardInput.AutoFlush = true;
            return proc;
        }

        /// <summary>
        /// Waits for the given stdio pumps to drain before the <see cref="Process"/> they read from
        /// is disposed: disposing it closes the stdio streams, which faults a parked
        /// <c>ReadLineAsync</c> instead of ending it at EOF. The pumps end on their own once the
        /// process is gone, so <paramref name="timeout"/> only matters if the child left an
        /// inherited handle to its own stdout open.
        /// <para>Null entries are ignored, so a caller may pass pumps it never started.</para>
        /// </summary>
        public static async Task JoinPumpsAsync(TimeSpan timeout, params Task[] pumps)
        {
            if (pumps == null)
                return;

            try
            {
                await Task.WhenAll(Array.FindAll(pumps, p => p != null)).WaitAsync(timeout).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Helper process pumps did not drain before disposal: " + ex.Message);
            }
        }

        /// <summary>
        /// Grants <paramref name="proc"/> the right to call <c>SetForegroundWindow</c>, for helpers
        /// that put a window of their own on screen (prisc_share_region's "share this window"
        /// prompt, prisc_scroll_driver's target activation, the capture overlay).
        /// <para>
        /// Windows refuses a freshly spawned process the foreground by default; this hands it our
        /// own foreground rights. Strictly best-effort: it only works while we still hold those
        /// rights (so the caller should spawn while Prisc.Ui is the foreground app), it is a no-op
        /// on every other platform, and every helper carries on without it: its window simply
        /// blinks in the taskbar instead of coming to the front.
        /// </para>
        /// </summary>
        public static void GrantForeground(Process proc)
        {
            if (proc == null)
                return;

            try
            {
                GrantForeground(proc.Id);
            }
            catch (Exception ex)
            {
                // the process may already have exited (Process.Id throws once it is reaped).
                Debug.WriteLine("AllowSetForegroundWindow failed: " + ex.Message);
            }
        }

        /// <inheritdoc cref="GrantForeground(Process)"/>
        public static void GrantForeground(int processId)
        {
            if (!OperatingSystem.IsWindows())
                return;

            try
            {
                AllowSetForegroundWindow(processId);
            }
            catch (Exception ex)
            {
                // user32 may be unavailable in an exotic session; not worth reporting.
                Debug.WriteLine("AllowSetForegroundWindow failed: " + ex.Message);
            }
        }

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool AllowSetForegroundWindow(int dwProcessId);
    }
}

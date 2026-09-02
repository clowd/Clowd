using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Clowd.UI
{
    /// <summary>
    /// The semantics-free plumbing every helper-binary driver in this app repeats: build the
    /// <see cref="ProcessStartInfo"/>, spawn, join the stdio pumps at the end, and hand the child
    /// the right to take the foreground. obs-express, vid2gif, clowd_scroll_driver, clowd_ai and
    /// clowd_share_region all spawn a redirected console child in exactly the same way, and the
    /// differences between their copies of that code have historically been bugs rather than
    /// intent (a missing <c>AutoFlush</c>, a missing UTF-8 encoding, a missing working directory).
    /// <para>
    /// NOTHING HERE MAY INTERPRET A PROTOCOL LINE. In particular, <see cref="ObsCapturer"/>'s rule
    /// that "an exit without a terminal message is fatal" is a rule of the *obs-express* protocol
    /// (DESIGN §1.4), not a rule of child processes in general, and it must never migrate into this
    /// file: clowd_share_region deliberately exits 0 with no terminal message once a share ends, so
    /// a driver that inherited that rule from shared code would report every normal share session
    /// as a crash. Each driver keeps its own protocol; this class only keeps the pipes.
    /// </para>
    /// </summary>
    public static class HelperProcess
    {
        /// <summary>
        /// Spawns <paramref name="exePath"/> with all three stdio streams redirected, ready for a
        /// line-oriented protocol.
        /// <para>
        /// Every option here is load-bearing. <c>UseShellExecute=false</c> is what makes redirection
        /// possible at all; <c>CreateNoWindow=true</c> keeps a console-subsystem debug build from
        /// flashing a console over the user's screen. The working directory is the binary's own
        /// folder because these helpers load native libraries (libobs, FFmpeg, ONNX Runtime) that
        /// ship beside them. All three streams are redirected even when a driver never intends to
        /// read stderr or write stdin: an *unredirected* stdout inherits the parent's, which for a
        /// GUI-subsystem Clowd.Ui is nothing at all, and every JSON line the helper writes would be
        /// silently discarded — and a redirected-but-undrained stream eventually blocks the child on
        /// a full pipe, which is why callers must pump both output streams for the child's lifetime.
        /// </para>
        /// <para>
        /// The two encoding-and-flush details are the ones hand-rolled copies keep missing. .NET's
        /// stdin writer does not auto-flush, so without <c>AutoFlush</c> a <c>quit</c> sits in the
        /// writer's buffer forever and the graceful shutdown silently becomes a kill. And the
        /// default console encoding is the OS ANSI code page, so a path or window title outside it
        /// would round-trip through mojibake in both directions; these helpers all speak UTF-8. The
        /// BOM is suppressed because a BOM written to stdin is a leading garbage character on the
        /// helper's first command line.
        /// </para>
        /// </summary>
        /// <param name="workingDirectory">Overrides the default (the executable's own directory).</param>
        /// <exception cref="InvalidOperationException">The process could not be started.</exception>
        public static Process Start(string exePath, IReadOnlyList<string> args, string workingDirectory = null)
        {
            if (String.IsNullOrEmpty(exePath))
                throw new ArgumentException("A helper process needs an executable path.", nameof(exePath));

            // no BOM: the helper reads its stdin as lines of text, and a BOM is not whitespace.
            var utf8 = new UTF8Encoding(false);

            var psi = new ProcessStartInfo(exePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = String.IsNullOrEmpty(workingDirectory)
                    ? Path.GetDirectoryName(Path.GetFullPath(exePath))
                    : workingDirectory,
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

            Debug.WriteLine("Starting helper process: " + exePath + " "
                            + String.Join(" ", args ?? (IReadOnlyList<string>)Array.Empty<string>()));

            var proc = Process.Start(psi);
            if (proc == null)
                throw new InvalidOperationException("Failed to start helper process: " + exePath);

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
                await Task.WhenAll(Array.FindAll(pumps, p => p != null)).WaitAsync(timeout);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Helper process pumps did not drain before disposal: " + ex.Message);
            }
        }

        /// <summary>
        /// Grants <paramref name="proc"/> the right to call <c>SetForegroundWindow</c>, for helpers
        /// that put a window of their own on screen (clowd_share_region's "share this window"
        /// prompt, clowd_scroll_driver's target activation).
        /// <para>
        /// Windows refuses a freshly spawned process the foreground by default; this hands it our
        /// own foreground rights. Strictly best-effort: it only works while *we* still hold those
        /// rights (so the caller should spawn while Clowd.Ui is the foreground app), it is a no-op
        /// on every other platform, and every helper carries on without it — its window simply
        /// blinks in the taskbar instead of coming to the front.
        /// </para>
        /// </summary>
        public static void GrantForeground(Process proc)
        {
            if (proc == null || !OperatingSystem.IsWindows())
                return;

            try
            {
                AllowSetForegroundWindow(proc.Id);
            }
            catch (Exception ex)
            {
                // the process may already have exited (Process.Id throws once it is reaped), or
                // user32 may be unavailable in an exotic session; neither is worth reporting.
                Debug.WriteLine("AllowSetForegroundWindow failed: " + ex.Message);
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
        private static extern bool AllowSetForegroundWindow(int dwProcessId);
    }
}

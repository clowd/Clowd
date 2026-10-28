using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Prisc.Ipc;

namespace Prisc.VideoSDK.Ai
{
    /// <summary>
    /// One running <c>prisc_ai</c> child process: binary payload over stdin/stdout, logging over
    /// stderr (HELPER_PROTOCOL.md §4.6: no line protocol, so no <c>HelperClient</c>). The pipe
    /// discipline is the whole design: the process protocol is "write raw frames to stdin, read one
    /// output frame per input frame from stdout", and an OS pipe buffer holds only ~64KB, so the
    /// caller MUST write <see cref="Input"/> and read <see cref="Output"/> concurrently (a
    /// stdin-writing task alongside a stdout-reading loop) or both sides fill and deadlock. Payload
    /// goes through the <b>BaseStream</b>s exposed here, never the encoding-aware reader/writer
    /// wrappers, which would corrupt binary data.
    ///
    /// <para>Spawned through <see cref="HelperProcess.Start"/>, so it is in the kill-on-close job on
    /// Windows. stderr is pumped line-wise on its own task into a bounded <see cref="HelperLog"/>
    /// (<see cref="StderrTail"/>). Cancellation is <see cref="Kill"/> (the whole process tree, so an
    /// in-flight inference cannot linger), and <see cref="Dispose"/> joins the stderr pump before
    /// releasing the process.</para>
    /// </summary>
    public sealed class AiClient : IDisposable
    {
        /// <summary>Lines of stderr retained: enough for the failure context that matters (the last
        /// error plus its surroundings) without buffering a chatty run.</summary>
        private const int StderrLines = 64;

        private readonly Process _process;
        private readonly Task _stderrPump;
        private readonly HelperLog _stderr;
        private bool _disposed;

        /// <summary>Spawns the binary with the given arguments. Throws when the process cannot
        /// start (missing/blocked binary).</summary>
        public static AiClient Start(string exePath, IReadOnlyList<string> args)
        {
            ArgumentNullException.ThrowIfNull(exePath);
            ArgumentNullException.ThrowIfNull(args);

            var stderr = new HelperLog(StderrLines, 64 * 1024);
            // stdin and stdout are binary (BaseStream); stderr is the helper's UTF-8 text.
            var process = HelperProcess.Start(exePath, args, new HelperStartOptions(), stderr);
            return new AiClient(process, stderr);
        }

        private AiClient(Process process, HelperLog stderr)
        {
            _process = process;
            _stderr = stderr;
            _stderrPump = Task.Run(PumpStderr);
        }

        /// <summary>The process's stdin as a raw stream. Write payload here from a dedicated task
        /// (see class remarks) and finish with <see cref="CloseInput"/> so the process sees EOF.</summary>
        public Stream Input => _process.StandardInput.BaseStream;

        /// <summary>The process's stdout as a raw stream: binary payload only, by the CLI
        /// contract.</summary>
        public Stream Output => _process.StandardOutput.BaseStream;

        public bool HasExited => _process.HasExited;

        public int ExitCode => _process.ExitCode;

        /// <summary>The retained tail of the process's stderr, newline-joined: the diagnostic to
        /// attach to any failure.</summary>
        public string StderrTail => _stderr.GetLog();

        /// <summary>Closes stdin so the process sees EOF and finishes its stream. Idempotent.</summary>
        public void CloseInput()
        {
            try { _process.StandardInput.Close(); }
            catch { /* already closed, or the process died first: WaitForExit tells the truth */ }
        }

        /// <summary>Cancellation path: kills the whole process tree. Safe to call at any time,
        /// any thread, repeatedly.</summary>
        public void Kill()
        {
            try
            {
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
            }
            catch { /* already exited */ }
        }

        /// <summary>Waits for the process to exit. False on timeout.</summary>
        public bool WaitForExit(int milliseconds) => _process.WaitForExit(milliseconds);

        /// <summary>Throws with the stderr tail when the exited process reported failure, with the
        /// "inference unavailable" exit named for what it is.</summary>
        public void ThrowIfFailed()
        {
            var code = _process.ExitCode;
            if (code == HelperExit.Ok)
                return;

            var tail = StderrTail;
            var reason = code switch
            {
                // current binaries link the ONNX Runtime statically and no longer emit it, but the
                // mapping stays for older shipped ones
                HelperExit.InferenceUnavailable => "inference is unavailable (no usable ONNX Runtime library)",
                HelperExit.RustPanic => "the process panicked",
                _ => $"exit code {code}",
            };
            throw new InvalidOperationException(
                $"prisc_ai failed: {reason}." + (tail.Length > 0 ? Environment.NewLine + tail : ""));
        }

        private void PumpStderr()
        {
            try
            {
                var reader = _process.StandardError;
                string line;
                while ((line = reader.ReadLine()) != null)
                    _stderr.Append(line);
            }
            catch
            {
                // the pipe broke because the process died or was killed: the exit code carries
                // the story from here.
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            Kill();
            try { _stderrPump.Wait(TimeSpan.FromSeconds(3)); }
            catch { /* a stuck pump must not hang disposal */ }
            _process.Dispose();
        }
    }
}

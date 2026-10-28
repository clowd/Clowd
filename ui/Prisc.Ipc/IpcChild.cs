using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Prisc.Ipc
{
    /// <summary>
    /// The child side of the protocol for a .NET helper (Prisc.VideoRender; HELPER_PROTOCOL.md §7.6):
    /// claims stdout, says <c>hello</c>, reads commands, and turns <c>quit</c>, stdin EOF and a dead
    /// stdout into one <see cref="OnQuit"/>.
    /// </summary>
    public static class IpcChild
    {
        private const int MaxLineBytes = 1024 * 1024;

        private static readonly object Sync = new();
        private static bool _opened;
        private static bool _readerStarted;
        private static int _quit;
        private static TimeSpan? _hardExitGrace;
        private static bool _hardExitArmed;

        /// <summary>Raised once, on whichever thread noticed: a <c>quit</c> command, stdin EOF, or the
        /// first failed write to stdout. Handlers must only flag or cancel; they must not block or emit.</summary>
        public static event Action OnQuit;

        /// <summary>
        /// Claims stdout and writes <c>hello</c>. Call first thing in <c>Main</c>, before any native
        /// library (FFmpeg, Skia, a GPU driver) is initialised: after this, fd 1 and the managed
        /// <see cref="Console.Out"/> both point at stderr, and only the returned writer reaches the
        /// parent's pipe. Hardware encoders and GPU drivers have been known to print to fd 1, and one
        /// stray line there would corrupt the protocol. Throws if called twice.
        /// </summary>
        public static IpcWriter<TEvent> Open<TEvent, TCommand>(string helper, HelperSpec<TEvent, TCommand> spec)
            where TEvent : class where TCommand : class
        {
            lock (Sync)
            {
                if (_opened)
                    throw new InvalidOperationException("IpcChild.Open may only be called once.");
                _opened = true;
            }

            var writer = new IpcWriter<TEvent>(ClaimStdout(), spec.EncodeEvent);
            writer.WriteLine(IpcCodec.EncodeHello(new IpcHello(IpcProtocol.Version, spec.Catalog, spec.CatalogHash,
                helper, Version(), (uint)Environment.ProcessId)));
            return writer;
        }

        /// <summary>
        /// Starts the one stdin thread. Each line is stripped of a trailing <c>\r</c> and a leading BOM,
        /// decoded as (lossy) UTF-8, and either raises <see cref="OnQuit"/> (<c>quit</c>), goes to
        /// <paramref name="onCommand"/>, or (not JSON, an unknown <c>type</c>, a bad field, or longer
        /// than 1 MiB) to <paramref name="onBadCommand"/> with the reason. EOF raises
        /// <see cref="OnQuit"/>. Empty lines are skipped.
        /// </summary>
        public static void ReadCommands<TCommand>(Func<string, TCommand> decode, Action<TCommand> onCommand,
            Action<string> onBadCommand) where TCommand : class
        {
            lock (Sync)
            {
                if (_readerStarted)
                    throw new InvalidOperationException("IpcChild.ReadCommands may only be called once.");
                _readerStarted = true;
            }

            var thread = new Thread(() => ReadLoop(decode, onCommand, onBadCommand))
            {
                IsBackground = true,
                Name = "ipc-stdin",
            };
            thread.Start();
        }

        /// <summary>Arms the orphan backstop: once <see cref="OnQuit"/> has fired, the process exits with
        /// <see cref="HelperExit.QuitTimeout"/> if it is still alive after <paramref name="grace"/>. A
        /// living parent kills a helper that overstays its catalog grace first, so this only matters
        /// when the parent is gone.</summary>
        public static void ArmHardExit(TimeSpan grace)
        {
            bool startNow;
            lock (Sync)
            {
                _hardExitGrace = grace;
                startNow = Volatile.Read(ref _quit) != 0;
            }

            if (startNow)
                StartHardExitTimer();
        }

        internal static void TriggerQuit()
        {
            if (Interlocked.Exchange(ref _quit, 1) != 0)
                return;

            try
            {
                OnQuit?.Invoke();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("ipc: a quit handler failed: " + ex.Message);
            }

            StartHardExitTimer();
        }

        private static void StartHardExitTimer()
        {
            TimeSpan grace;
            lock (Sync)
            {
                if (_hardExitArmed || _hardExitGrace is not { } armed)
                    return;
                _hardExitArmed = true;
                grace = armed;
            }

            var timer = new Thread(() =>
            {
                Thread.Sleep(grace);
                Console.Error.WriteLine($"ipc: still running {grace.TotalSeconds:0} s after quit; exiting");
                Environment.Exit(HelperExit.QuitTimeout);
            })
            {
                IsBackground = true,
                Name = "ipc-hard-exit",
            };
            timer.Start();
        }

        private static void ReadLoop<TCommand>(Func<string, TCommand> decode, Action<TCommand> onCommand,
            Action<string> onBadCommand) where TCommand : class
        {
            try
            {
                using var stdin = Console.OpenStandardInput();
                var buffer = new MemoryStream();
                var oversize = false;
                var chunk = new byte[4096];
                while (true)
                {
                    int read = stdin.Read(chunk, 0, chunk.Length);
                    if (read <= 0)
                        break;

                    int start = 0;
                    for (int i = 0; i < read; i++)
                    {
                        if (chunk[i] != (byte)'\n')
                            continue;

                        Append(buffer, chunk, start, i - start, ref oversize);
                        start = i + 1;
                        if (!Dispatch(buffer, oversize, decode, onCommand, onBadCommand))
                            return;
                        buffer.SetLength(0);
                        oversize = false;
                    }

                    Append(buffer, chunk, start, read - start, ref oversize);
                }

                // a final line without a newline still counts
                if (buffer.Length > 0 || oversize)
                    Dispatch(buffer, oversize, decode, onCommand, onBadCommand);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("ipc: reading stdin failed: " + ex.Message);
            }

            TriggerQuit();
        }

        private static void Append(MemoryStream buffer, byte[] chunk, int offset, int count, ref bool oversize)
        {
            if (oversize || count <= 0)
                return;
            if (buffer.Length + count > MaxLineBytes)
            {
                oversize = true;
                buffer.SetLength(0);
                return;
            }
            buffer.Write(chunk, offset, count);
        }

        /// <summary>Handles one complete line; false once the reader should stop (after quit).</summary>
        private static bool Dispatch<TCommand>(MemoryStream buffer, bool oversize, Func<string, TCommand> decode,
            Action<TCommand> onCommand, Action<string> onBadCommand) where TCommand : class
        {
            if (oversize)
            {
                onBadCommand?.Invoke("command line longer than 1 MiB");
                return true;
            }

            var line = DecodeLine(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
            if (line.Trim().Length == 0)
                return true;

            try
            {
                var type = IpcCodec.ReadType(line);
                if (type == "quit")
                {
                    TriggerQuit();
                    return false;
                }

                var command = decode(line);
                if (command == null)
                    onBadCommand?.Invoke(type == null ? "command has no type" : $"unknown command '{type}'");
                else
                    onCommand?.Invoke(command);
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
            {
                onBadCommand?.Invoke("malformed command: " + ex.Message);
            }

            return true;
        }

        private static string DecodeLine(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length > 0 && bytes[^1] == (byte)'\r')
                bytes = bytes[..^1];
            if (bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
                bytes = bytes[3..];
            return Encoding.UTF8.GetString(bytes);
        }

        private static string Version()
        {
            var assembly = Assembly.GetEntryAssembly() ?? typeof(IpcChild).Assembly;
            return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                   ?? assembly.GetName().Version?.ToString()
                   ?? "unknown";
        }

        /// <summary>
        /// The stdout guard (HELPER_PROTOCOL.md §3.2 and §7.6): a private, non-inheritable stream onto
        /// the real stdout, then fd 1 / STD_OUTPUT_HANDLE pointed at stderr, then the managed
        /// <see cref="Console.Out"/> after it. Non-inheritable matters: a grandchild (prisc_ai) that
        /// inherited the protocol pipe would keep the parent's stdout pump from ever seeing EOF.
        /// </summary>
        private static Stream ClaimStdout()
        {
            Stream stream;
            if (OperatingSystem.IsWindows())
            {
                var original = GetStdHandle(StdOutputHandle);
                var self = GetCurrentProcess();
                if (!DuplicateHandle(self, original, self, out var duplicate, 0, false, DuplicateSameAccess))
                    throw new IOException("DuplicateHandle(stdout) failed (error " + Marshal.GetLastWin32Error() + ")");

                stream = new FileStream(new SafeFileHandle(duplicate, ownsHandle: true), FileAccess.Write, 1);
                SetStdHandle(StdOutputHandle, GetStdHandle(StdErrorHandle));
                try
                {
                    // the CRT's fd 1, so native printf follows; assumes every native library shares the
                    // dynamic UCRT (§3.2).
                    UcrtDup2(2, 1);
                }
                catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
                {
                    Console.Error.WriteLine("ipc: could not redirect the CRT's stdout: " + ex.Message);
                }
            }
            else
            {
                // Opening /dev/fd/1 yields a new descriptor onto the same pipe, and .NET opens every
                // file O_CLOEXEC: the private, non-inheritable copy, with no variadic fcntl to P/Invoke
                // (which arm64 macOS cannot call correctly). Not Console.OpenStandardOutput: it is
                // close-on-exec too, but swallows EPIPE, and a dead parent must surface as a failed
                // write. Taken before fd 1 is repointed.
                var handle = File.OpenHandle("/dev/fd/1", FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                stream = new FileStream(handle, FileAccess.Write, 1);
                int rc = OperatingSystem.IsMacOS() ? MacDup2(2, 1) : LinuxDup2(2, 1);
                if (rc < 0)
                    throw new IOException("dup2(2, 1) failed (errno " + Marshal.GetLastPInvokeError() + ")");
            }

            Console.SetOut(Console.Error);
            return stream;
        }

        private const int StdOutputHandle = -11;
        private const int StdErrorHandle = -12;
        private const uint DuplicateSameAccess = 2;

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool SetStdHandle(int nStdHandle, IntPtr hHandle);

        [DllImport("kernel32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool DuplicateHandle(IntPtr hSourceProcessHandle, IntPtr hSourceHandle,
            IntPtr hTargetProcessHandle, out IntPtr lpTargetHandle, uint dwDesiredAccess, bool bInheritHandle,
            uint dwOptions);

        [DllImport("ucrtbase.dll", EntryPoint = "_dup2")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int UcrtDup2(int fd1, int fd2);

        [DllImport("libc", EntryPoint = "dup2", SetLastError = true)]
        private static extern int MacDup2(int fd1, int fd2);

        [DllImport("libc.so.6", EntryPoint = "dup2", SetLastError = true)]
        private static extern int LinuxDup2(int fd1, int fd2);
    }

    /// <summary>The child's protocol writer: one line per event, written and flushed under a lock.</summary>
    public sealed class IpcWriter<TEvent> where TEvent : class
    {
        private readonly object _lock = new();
        private readonly Stream _stream;
        private readonly Func<TEvent, string> _encode;
        private bool _closed;

        internal IpcWriter(Stream stream, Func<TEvent, string> encode)
        {
            _stream = stream;
            _encode = encode;
        }

        /// <summary>True once a write has failed (the parent is gone).</summary>
        public bool IsClosed
        {
            get { lock (_lock) return _closed; }
        }

        /// <summary>Writes <paramref name="ev"/>. Never throws: a failed write marks the writer closed and
        /// raises <see cref="IpcChild.OnQuit"/> once the lock is released.</summary>
        public bool Emit(TEvent ev)
        {
            string line;
            try
            {
                line = _encode(ev);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("ipc: could not encode an event: " + ex.Message);
                return false;
            }

            return WriteLine(line);
        }

        internal bool WriteLine(string line)
        {
            bool failed = false;
            lock (_lock)
            {
                if (_closed)
                    return false;

                try
                {
                    var bytes = Encoding.UTF8.GetBytes(line + "\n");
                    _stream.Write(bytes, 0, bytes.Length);
                    _stream.Flush();
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or NotSupportedException)
                {
                    _closed = true;
                    failed = true;
                    Console.Error.WriteLine("ipc: stdout is gone: " + ex.Message);
                }
            }

            // outside the lock, so a quit handler that emits cannot deadlock
            if (failed)
                IpcChild.TriggerQuit();
            return !failed;
        }
    }
}

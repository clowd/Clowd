using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using Prisc.Interop;

namespace Prisc.Ipc
{
    /// <summary>
    /// The child side of the protocol for a .NET helper (Prisc.VideoRender; HELPER_PROTOCOL.md §7.6):
    /// claims stdout, says <c>hello</c>, reads commands, and turns <c>quit</c>, stdin EOF, SIGTERM,
    /// SIGINT, SIGHUP and a dead stdout into one <see cref="OnQuit"/>.
    /// </summary>
    /// <remarks>The stdout guard is prisc_interop's (<see cref="StdoutGuard"/>), the same code every
    /// Rust helper runs. The command loop stays managed (a native callback per command would cost more
    /// than it shares), and is held to the Rust loop by data: its limit and refusal texts are the
    /// generated <see cref="IpcProtocol"/> constants, and Prisc.Ipc.Tests runs it over the golden
    /// framing vectors prisc_ipc's own tests run.</remarks>
    public static class IpcChild
    {
        private static readonly object Sync = new();
        private static readonly string QuitType = IpcCodec.ReadType(IpcCodec.QuitLine);
        private static bool _opened;
        private static bool _readerStarted;
        private static int _quit;
        private static TimeSpan? _hardExitGrace;
        private static bool _hardExitArmed;
        /// <summary>Held for the process lifetime: a collected registration unregisters itself.</summary>
        private static List<PosixSignalRegistration> _signals;
        private static Action _onQuit;

        /// <summary>Raised once, on whichever thread noticed: a <c>quit</c> command, stdin EOF, a
        /// termination signal (SIGTERM, SIGINT, SIGHUP; on Windows every console control event,
        /// CTRL_BREAK included, as the Rust helpers' console handler does), or the first failed write
        /// to stdout. A handler added after the quit already fired runs at once, on the adding thread
        /// (prisc_ipc lifetime.rs does the same), so a signal that lands between <see cref="Open"/> and
        /// the subscription is not lost. Handlers must only flag or cancel; they must not block or
        /// emit.</summary>
        public static event Action OnQuit
        {
            add
            {
                if (value == null)
                    return;
                lock (Sync)
                {
                    if (_quit == 0)
                    {
                        _onQuit += value;
                        return;
                    }
                }

                RunQuitHandlers(value);
            }
            remove
            {
                lock (Sync)
                    _onQuit -= value;
            }
        }

        /// <summary>
        /// Claims stdout and writes <c>hello</c>. Call first thing in <c>Main</c>, before any native
        /// library (FFmpeg, Skia, a GPU driver) is initialised: after this, fd 1 and the managed
        /// <see cref="Console.Out"/> both point at stderr, and only the returned writer reaches the
        /// parent's pipe. Hardware encoders and GPU drivers have been known to print to fd 1, and one
        /// stray line there would corrupt the protocol. Also routes the termination signals into
        /// <see cref="OnQuit"/>, like a <c>quit</c> command (prisc_ipc lifetime.rs does the same for
        /// the Rust helpers). Throws if called twice.
        /// <para>prisc_interop is a hard requirement (R12): without it this writes <c>hello</c> and one
        /// fatal <c>init_failed</c> error naming the reason, and exits with
        /// <see cref="HelperExit.Failed"/>. With it, its log sink is installed to write to stderr
        /// (every level but Debug).</para>
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

            var hello = IpcCodec.EncodeHello(new IpcHello(IpcProtocol.Version, spec.Catalog, spec.CatalogHash,
                helper, Version(), (uint)Environment.ProcessId));
            if (!InteropLibrary.IsAvailable)
                ExitWithoutLibrary(hello);

            var writer = new IpcWriter<TEvent>(ClaimStdout(), spec.EncodeEvent);
            // the library's callback panics and teardown failures reach a helper only through its
            // log sink; stderr is what the parent keeps of a helper's diagnostics
            InteropLog.SetQueuedSink(static (level, text) =>
            {
                if (level != InteropLogLevel.Debug)
                    Console.Error.WriteLine($"prisc_interop {level}: {text}");
            });
            writer.WriteLine(hello);
            RouteSignals();
            return writer;
        }

        /// <summary>
        /// Starts the one stdin thread. Each line is stripped of a trailing <c>\r</c> and a leading BOM,
        /// decoded as (lossy) UTF-8, and either raises <see cref="OnQuit"/> (<c>quit</c>), goes to
        /// <paramref name="onCommand"/>, or (not JSON, no string <c>type</c>, an unknown <c>type</c>, a
        /// bad field, or longer than <see cref="IpcProtocol.MaxLineBytes"/>) to
        /// <paramref name="onBadCommand"/> with the <see cref="IpcProtocol"/> reason. Reading goes on
        /// after <c>quit</c> (later lines are still delivered, HELPER_PROTOCOL.md §2.3). EOF raises
        /// <see cref="OnQuit"/>. Blank lines are skipped.
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

        /// <summary>Whether the quit path has fired (tests).</summary>
        internal static bool QuitTriggered => Volatile.Read(ref _quit) != 0;

        internal static void TriggerQuit()
        {
            Action handlers;
            lock (Sync)
            {
                // under the lock, so a handler is either in this snapshot or sees _quit set and runs
                // itself (the add accessor)
                if (_quit != 0)
                    return;
                Volatile.Write(ref _quit, 1);
                handlers = _onQuit;
            }

            RunQuitHandlers(handlers);
            StartHardExitTimer();
        }

        private static void RunQuitHandlers(Action handlers)
        {
            try
            {
                handlers?.Invoke();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("ipc: a quit handler failed: " + ex.Message);
            }
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

        /// <summary>SIGTERM, SIGINT and SIGHUP become the quit path instead of the runtime's default
        /// shutdown, so a signalled helper ends the way a <c>quit</c> command ends it. On Windows the
        /// runtime maps the console control events onto these, except CTRL_BREAK, which it reports as
        /// SIGQUIT; that is routed too there, because the Rust helpers' console handler quits on every
        /// event. On Unix SIGQUIT keeps its default, as it does for the Rust helpers (ctrlc's
        /// termination set). A signal this platform cannot register is skipped.</summary>
        private static void RouteSignals()
        {
            lock (Sync)
            {
                if (_signals != null)
                    return;
            }

            var registrations = new List<PosixSignalRegistration>();
            var signals = OperatingSystem.IsWindows()
                ? new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGHUP, PosixSignal.SIGQUIT }
                : new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGHUP };
            foreach (var signal in signals)
            {
                try
                {
                    registrations.Add(PosixSignalRegistration.Create(signal, context =>
                    {
                        context.Cancel = true;
                        Console.Error.WriteLine($"ipc: quitting ({context.Signal})");
                        TriggerQuit();
                    }));
                }
                catch (Exception ex) when (ex is PlatformNotSupportedException or IOException)
                {
                    Console.Error.WriteLine($"ipc: cannot route {signal} into the quit path: {ex.Message}");
                }
            }

            lock (Sync)
                _signals = registrations;
        }

        private static void ReadLoop<TCommand>(Func<string, TCommand> decode, Action<TCommand> onCommand,
            Action<string> onBadCommand) where TCommand : class
        {
            try
            {
                using var stdin = Console.OpenStandardInput();
                Pump(stdin, decode, onCommand, reason =>
                {
                    Console.Error.WriteLine("ipc: bad command: " + reason);
                    onBadCommand?.Invoke(reason);
                }, TriggerQuit);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("ipc: reading stdin failed: " + ex.Message);
            }

            // EOF, or a read error treated as one
            TriggerQuit();
        }

        /// <summary>
        /// The command loop over <paramref name="stdin"/> up to EOF, the twin of prisc_ipc's
        /// <c>stdin::pump</c> (both run the golden framing vectors). <paramref name="onQuitCommand"/>
        /// runs for every <c>quit</c> line, in order with the lines around it; reading goes on after it.
        /// The caller handles EOF.
        /// </summary>
        internal static void Pump<TCommand>(Stream stdin, Func<string, TCommand> decode, Action<TCommand> onCommand,
            Action<string> onBadCommand, Action onQuitCommand) where TCommand : class
        {
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
                    Dispatch(buffer, oversize, decode, onCommand, onBadCommand, onQuitCommand);
                    buffer.SetLength(0);
                    oversize = false;
                }

                Append(buffer, chunk, start, read - start, ref oversize);
            }

            // a final line without a newline still counts
            if (buffer.Length > 0 || oversize)
                Dispatch(buffer, oversize, decode, onCommand, onBadCommand, onQuitCommand);
        }

        private static void Append(MemoryStream buffer, byte[] chunk, int offset, int count, ref bool oversize)
        {
            if (oversize || count <= 0)
                return;
            if (buffer.Length + count > IpcProtocol.MaxLineBytes)
            {
                oversize = true;
                buffer.SetLength(0);
                return;
            }
            buffer.Write(chunk, offset, count);
        }

        /// <summary>Handles one complete line, classifying it exactly as prisc_ipc's <c>parse</c> does:
        /// blank, not JSON, no string <c>type</c>, <c>quit</c>, then the catalog's decode.</summary>
        private static void Dispatch<TCommand>(MemoryStream buffer, bool oversize, Func<string, TCommand> decode,
            Action<TCommand> onCommand, Action<string> onBadCommand, Action onQuitCommand) where TCommand : class
        {
            if (oversize)
            {
                onBadCommand?.Invoke(IpcProtocol.OversizeReason);
                return;
            }

            var line = DecodeLine(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
            if (String.IsNullOrWhiteSpace(line))
                return;

            string type;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                type = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("type", out var value)
                       && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
            }
            catch (JsonException ex)
            {
                onBadCommand?.Invoke(String.Format(IpcProtocol.NotJsonReason, ex.Message));
                return;
            }

            if (type == null)
            {
                onBadCommand?.Invoke(IpcProtocol.NoTypeReason);
                return;
            }

            if (type == QuitType)
            {
                onQuitCommand?.Invoke();
                return;
            }

            TCommand command;
            try
            {
                command = decode(line);
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or FormatException)
            {
                onBadCommand?.Invoke(String.Format(IpcProtocol.InvalidCommandReason, type, ex.Message));
                return;
            }

            if (command == null)
                onBadCommand?.Invoke(String.Format(IpcProtocol.UnknownCommandReason, type));
            else
                onCommand?.Invoke(command);
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
        /// The stdout guard (HELPER_PROTOCOL.md §3.2 and §7.6): prisc_interop's private,
        /// non-inheritable handle onto the real stdout, with fd 1 (and on Windows
        /// <c>STD_OUTPUT_HANDLE</c> and the UCRT's fd 1) already pointed at stderr, then the managed
        /// <see cref="Console.Out"/> after it. Non-inheritable matters: a grandchild (prisc_ai) that
        /// inherited the protocol pipe would keep the parent's stdout pump from ever seeing EOF. A
        /// failed redirect was logged natively and is not fatal: stdout then still reaches the pipe.
        /// A <see cref="FileStream"/>, not <see cref="Console.OpenStandardOutput()"/>, which swallows
        /// EPIPE: a dead parent must surface as a failed write.
        /// </summary>
        private static Stream ClaimStdout()
        {
            var handle = StdoutGuard.Claim(out _);
            var stream = new FileStream(handle, FileAccess.Write, 1);
            Console.SetOut(Console.Error);
            return stream;
        }

        /// <summary>No prisc_interop: <c>hello</c> and one fatal <c>init_failed</c> error on the
        /// unguarded stdout (nothing native has run, so nothing else can be on it), then exit.</summary>
        private static void ExitWithoutLibrary(string hello)
        {
            var message = SingleLine(InteropLibrary.LibraryFileName + " is not available: " + InteropLibrary.UnavailableReason);
            var error = IpcJson.Write(w =>
            {
                w.WriteString("type", "error");
                w.WriteString("code", IpcErrorCodes.InitFailed);
                w.WriteString("message", message);
                w.WriteBoolean("fatal", true);
            });
            Console.Error.WriteLine("Fatal " + IpcErrorCodes.InitFailed + ": " + message);
            try
            {
                using var stdout = Console.OpenStandardOutput();
                var bytes = Encoding.UTF8.GetBytes(hello + "\n" + error + "\n");
                stdout.Write(bytes, 0, bytes.Length);
                stdout.Flush();
            }
            catch (IOException)
            {
                // the parent is gone; the exit code still says it
            }
            Environment.Exit(HelperExit.Failed);
        }

        /// <summary>Every whitespace run as one space (prisc_ipc <c>single_line</c>), so the message
        /// cannot break the line protocol.</summary>
        private static string SingleLine(string text)
            => String.Join(' ', text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
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

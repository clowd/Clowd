using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Prisc.Interop
{
    /// <summary>The native library's log sink (README D3). Off until a host installs one: Prisc.Ui at
    /// startup and every C# helper in <c>IpcChild.Open</c> do, through <see cref="SetQueuedSink"/>.</summary>
    public static class InteropLog
    {
        private static Action<InteropLogLevel, string>? _sink;
        private static ChannelWriter<(InteropLogLevel, string)>? _queue;
        private static readonly object QueueSync = new();

        /// <summary>The most lines a queued sink holds before it drops new ones.</summary>
        public const int QueueCapacity = 1024;

        /// <summary>
        /// Installs (or, with null, removes) the process-global sink. Returns only after every
        /// in-flight call of the previous sink has returned. The sink runs on whatever thread called
        /// the library; it must not block and must not call into Prisc.Interop. A no-op when the
        /// library is not available.
        /// </summary>
        public static unsafe void SetSink(Action<InteropLogLevel, string>? sink)
        {
            if (!InteropLibrary.IsAvailable)
                return;

            Volatile.Write(ref _sink, sink);
            // The sink is process-global, so the thunk needs no user state and no GCHandle.
            delegate* unmanaged[Cdecl]<int, byte*, nuint, void*, void> thunk = sink == null ? null : &Receive;
            NativeMethods.prisc_log_set_callback(thunk, null);
        }

        /// <summary>
        /// Installs <paramref name="handler"/> behind a queue, the way a host wires the library's
        /// diagnostics to its own logs and crash reports (callback panics and teardown failures
        /// reach the host only here): the library's call only queues the line, and one background
        /// task hands lines to <paramref name="handler"/> in order, so it may block and may call into
        /// Prisc.Interop. Past <see cref="QueueCapacity"/> waiting lines new ones are dropped. Null
        /// removes it; installing another replaces it. A no-op when the library is not available.
        /// </summary>
        public static void SetQueuedSink(Action<InteropLogLevel, string>? handler)
        {
            if (!InteropLibrary.IsAvailable)
                return;

            lock (QueueSync)
            {
                _queue?.TryComplete();
                _queue = null;
                if (handler == null)
                {
                    SetSink(null);
                    return;
                }

                var channel = Channel.CreateBounded<(InteropLogLevel Level, string Text)>(new BoundedChannelOptions(QueueCapacity)
                {
                    FullMode = BoundedChannelFullMode.DropWrite,
                    SingleReader = true,
                });
                _ = Task.Run(async () =>
                {
                    await foreach (var (level, text) in channel.Reader.ReadAllAsync().ConfigureAwait(false))
                    {
                        try
                        {
                            handler(level, text);
                        }
                        catch
                        {
                            // A log handler is never worth losing the lines after it.
                        }
                    }
                });
                _queue = channel.Writer;
                var writer = channel.Writer;
                SetSink((level, text) => writer.TryWrite((level, text)));
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static unsafe void Receive(int level, byte* message, nuint length, void* user)
        {
            // Nothing may escape an UnmanagedCallersOnly method: an exception here is a fail-fast.
            try
            {
                Volatile.Read(ref _sink)?.Invoke((InteropLogLevel)level, Utf8.Read(message, length));
            }
            catch
            {
                // A log sink is never worth a crash.
            }
        }
    }
}

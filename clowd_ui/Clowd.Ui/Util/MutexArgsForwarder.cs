using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;

namespace Clowd.Util
{
    public class CommandLineEventArgs : EventArgs
    {
        public string[] Args { get; }

        public CommandLineEventArgs(string[] args)
        {
            Args = args;
        }
    }

    public record SendArgsRequestModel(int pid, string[] args);

    internal sealed class MutexArgsForwarder : IDisposable
    {
        public event EventHandler<CommandLineEventArgs> ArgsReceived;

        private const int MaxMessageSize = 1024 * 1024;

        // AllowSetForegroundWindow(ASFW_ANY): any process may take the foreground
        private const int ASFW_ANY = -1;

        private bool _ready;
        private List<string> _batch;
        private System.Timers.Timer _notifyTimer;
        private Mutex _mutex;
        private Thread _hostThread;
        private CancellationTokenSource _cts;

        public MutexArgsForwarder()
        {
            _ready = false;
            _batch = new List<string>();
            _notifyTimer = new System.Timers.Timer();
            _notifyTimer.Interval = 1000;
            _notifyTimer.Elapsed += OnCommandLineBatchTimerTick;
            _cts = new CancellationTokenSource();
        }

        /// <summary>
        /// If this method returns true, app should continue startup. If false, the args have been forwarded to another already running instance, and you should exit.
        /// </summary>
        public async Task<bool> Startup(string[] args)
        {
            Dispose();

            bool created;
            try
            {
                _mutex = new Mutex(false, Constants.ClowdMutex, out created);
            }
            catch (Exception ex)
            {
                // stale or inaccessible named mutex — assume we are the first instance.
                Debug.WriteLine("MutexArgsForwarder: failed to acquire named mutex: " + ex.Message);
                SentryConfig.CaptureHandled(ex, "mutex.acquire");
                _mutex = null;
                created = true;
            }

            if (!created)
            {
                try
                {
                    if (args != null && args.Length > 0)
                        await SendArgsToRemote(args);
                }
                finally
                {
                    // Can't call dispose here, we don't own the mutex and Dispose will try to release the mutex
                    _mutex.Dispose();
                    _mutex = null;
                }

                return false;
            }
            else
            {
                _hostThread = new Thread(ListenForConnectionRequests);
                _hostThread.IsBackground = true;
                _hostThread.Priority = ThreadPriority.BelowNormal;
                _hostThread.Start();
                ProcessArgs(Process.GetCurrentProcess().Id, args);
                return true;
            }
        }

        /// <summary>
        /// Call this method to start recieving queued command line arguments
        /// </summary>
        public void Ready()
        {
            _ready = true;
            OnCommandLineBatchTimerTick(this, new EventArgs());
        }

        private async Task SendArgsToRemote(string[] args)
        {
            var req = new SendArgsRequestModel(Process.GetCurrentProcess().Id, args);
            var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(req, ClowdUiJsonContext.Default.SendArgsRequestModel));
            var prefix = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);

            // Pass our foreground rights on to the instance that will handle these args: we
            // were just launched (from the shell extension or a shortcut) so we hold them,
            // and it needs them to raise its window. Best-effort — it only works while we
            // are still the foreground interaction.
            if (OperatingSystem.IsWindows())
            {
                try { AllowSetForegroundWindow(ASFW_ANY); }
                catch { }
            }

            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var pipeClient = new NamedPipeClientStream(".", Constants.ClowdNamedPipe, PipeDirection.Out, PipeOptions.Asynchronous);
                await pipeClient.ConnectAsync(timeout.Token);
                await pipeClient.WriteAsync(prefix, 0, prefix.Length, timeout.Token);
                await pipeClient.WriteAsync(payload, 0, payload.Length, timeout.Token);
                await pipeClient.FlushAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("Unable to forward command line arguments to running Clowd instance.");
            }
        }

        private async void ListenForConnectionRequests()
        {
            var token = _cts;

            async Task AcceptConnection()
            {
                using var server = new NamedPipeServerStream(
                    Constants.ClowdNamedPipe, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(token.Token);

                var prefix = new byte[4];
                await ReadExactAsync(server, prefix, token.Token);
                var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
                if (length <= 0 || length > MaxMessageSize)
                    return;

                var payload = new byte[length];
                await ReadExactAsync(server, payload, token.Token);

                var req = JsonSerializer.Deserialize(Encoding.UTF8.GetString(payload), ClowdUiJsonContext.Default.SendArgsRequestModel);
                if (req != null)
                    ProcessArgs(req.pid, req.args);
            }

            int err = 0;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await AcceptConnection();
                    err = 0;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("MutexArgsForwarder: unable to receive named pipe connection request: " + ex);
                    SentryConfig.CaptureHandled(ex, "mutex.pipe-accept");

                    // the pipe name itself is unusable on this platform (e.g. too long for a unix
                    // domain socket path) — retrying just reports the same defect five times over.
                    if (ex is ArgumentException)
                        return;

                    // a crashed previous instance can leave a stale unix domain socket behind which
                    // prevents the server pipe from binding — try removing it once and retrying.
                    if (err == 0)
                        TryDeleteStalePipe();

                    if (err++ > 3) return; // exit if several errors in a row.

                    try { await Task.Delay(500, token.Token); }
                    catch (OperationCanceledException) { return; }
                }
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
        private static extern bool AllowSetForegroundWindow(int dwProcessId);

        private static void TryDeleteStalePipe()
        {
            if (OperatingSystem.IsWindows())
                return;

            try
            {
                var stale = Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + Constants.ClowdNamedPipe);
                if (File.Exists(stale))
                    File.Delete(stale);
            }
            catch
            {
                // best effort
            }
        }

        private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
        {
            int read = 0;
            while (read < buffer.Length)
            {
                int r = await stream.ReadAsync(buffer, read, buffer.Length - read, cancellationToken);
                if (r <= 0)
                    throw new EndOfStreamException("Pipe closed before the full message was received.");
                read += r;
            }
        }

        private void OnCommandLineBatchTimerTick(object sender, EventArgs e)
        {
            // we can turn this off now and process any collected files. timer will be started again if we recieve additional cli args
            _notifyTimer.Enabled = false;

            if (_batch.Count > 0)
            {
                var args = _batch.ToArray();
                _batch.Clear();
                ArgsReceived?.Invoke(this, new CommandLineEventArgs(args));
            }
        }

        private void ProcessArgs(int pid, string[] args)
        {
            if (args == null || args.Length < 1)
                return;

            _notifyTimer.Enabled = false;

            // each launch's command word is stripped here, per message — chunked launches
            // (the shell extension splits huge selections) merge into one flat path batch
            foreach (var f in CliArgs.ExtractUploadPaths(args))
                _batch.Add(f);

            if (_ready)
                _notifyTimer.Enabled = true;
        }

        public void Dispose()
        {
            _notifyTimer.Enabled = false;
            _ready = false;
            _cts.Cancel();
            _cts = new CancellationTokenSource();

            if (_mutex != null)
            {
                // I don't know why this fails, but the process is exiting so I also don't care
                try { _mutex.ReleaseMutex(); }
                catch { }

                try { _mutex.Dispose(); }
                catch { }

                _mutex = null;
            }
        }
    }
}

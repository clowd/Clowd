using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using PipeMethodCalls;
using PipeMethodCalls.NetJson;

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

    interface IArgForwardingServer
    {
        void ReceiveArgs(SendArgsRequestModel request);
    }

    internal sealed class MutexArgsForwarder : IArgForwardingServer, IDisposable
    {
        public event EventHandler<CommandLineEventArgs> ArgsReceived;

        private bool _ready;
        private List<string> _batch;
        private System.Timers.Timer _notifyTimer;
        private Mutex _mutex;
        private Thread _hostThread;
        private CancellationTokenSource _cts;

        private static readonly ILogger _log = LogManager.GetCurrentClassLogger();

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
            _mutex = new Mutex(false, Constants.ClowdMutex, out created);
            if (!created)
            {
                if (args != null || args.Length > 0)
                    await SendArgsToRemote(args);

                // Can't call dispose here, we don't own the mutex and Dispose will try to release the mutex
                _mutex.Dispose();
                _mutex = null;
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
            var pipeClient = new PipeClient<IArgForwardingServer>(new NetJsonPipeSerializer(), Constants.ClowdNamedPipe);
            try
            {
                await pipeClient.ConnectAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
                await pipeClient.InvokeAsync(adder => adder.ReceiveArgs(req), new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
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
                var server = new PipeServer<IArgForwardingServer>(new NetJsonPipeSerializer(), Constants.ClowdNamedPipe, () => this, maxNumberOfServerInstances: -1);
                server.SetLogger((m) => _log.Info(m));
                await server.WaitForConnectionAsync(token.Token);
                server.WaitForRemotePipeCloseAsync().ContinueWith(v => server.Dispose());
            }

            int err = 0;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await AcceptConnection();
                    err = 0;
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "Unable to receive named pipe connection request");
                    if (err++ > 3) return; // exit if 3 errors in a row.
                }
            }
        }

        private void OnCommandLineBatchTimerTick(object sender, EventArgs e)
        {
            // we can turn this off now and process any collected files. timer will be started again if we recieve additional cli args
            _notifyTimer.Enabled = false;

            if (_batch.Count > 0)
            {
                _log.Info($"Processing batch of {_batch.Count} cli arguments");
                var args = _batch.ToArray();
                _batch.Clear();
                ArgsReceived?.Invoke(this, new CommandLineEventArgs(args));
            }
        }

        void IArgForwardingServer.ReceiveArgs(SendArgsRequestModel request)
        {
            ProcessArgs(request.pid, request.args);
        }

        private void ProcessArgs(int pid, string[] args)
        {
            if (args == null || args.Length < 1)
                return;

            _log.Info($"Enqueuing {args.Length} cli args received from pid.{pid}");

            _notifyTimer.Enabled = false;

            foreach (var f in args)
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

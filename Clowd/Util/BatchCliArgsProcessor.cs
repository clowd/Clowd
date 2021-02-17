using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.ServiceModel;
using System.Text;
using System.Threading;
using System.Threading.Tasks;


namespace Clowd.Util
{
    [ServiceContract]
    public interface ICommandLineProxy
    {
        [OperationContract]
        void ProcessArgs(int pid, string[] args);

        [OperationContract]
        bool Heartbeat();
    }

    public class CommandLineEventArgs : EventArgs
    {
        public string[] Args { get; }
        public CommandLineEventArgs(string[] args)
        {
            Args = args;
        }
    }

    internal class BatchCliArgsProcessor : ICommandLineProxy, IDisposable
    {
        public event EventHandler<CommandLineEventArgs> ArgsReceived;

        private bool _ready;
        private List<string> _batch;
        private System.Timers.Timer _notifyTimer;
        private ServiceHost _host;
        private Mutex _mutex;
        private string _mutexName;
        private string _pipeName;

        public BatchCliArgsProcessor(string mutexName)
        {
            _ready = false;
            _batch = new List<string>();
            _notifyTimer = new System.Timers.Timer();
            _notifyTimer.Interval = 1000;
            _notifyTimer.Elapsed += OnCommandLineBatchTimerTick;
            _mutexName = mutexName;
            _pipeName = mutexName + "npipe";
        }

        /// <summary>
        /// If this method returns true, app should continue startup. If false, the args have been forwarded to another already running instance, and you should exit.
        /// </summary>
        public void Startup(string[] args)
        {
            Dispose();

            bool created;
            _mutex = new Mutex(false, _mutexName, out created);
            if (!created)
            {
                if (args != null || args.Length > 0)
                    SendArgsToRemote(args);
                Environment.Exit(0);
            }
            else
            {
                StartServiceHost();
                (this as ICommandLineProxy).ProcessArgs(Process.GetCurrentProcess().Id, args);
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

        private void SendArgsToRemote(string[] args)
        {
            ChannelFactory<ICommandLineProxy> pipeFactory = new ChannelFactory<ICommandLineProxy>(
                        new NetNamedPipeBinding(),
                        new EndpointAddress("net.pipe://localhost/" + _pipeName));

            ICommandLineProxy pipeProxy = pipeFactory.CreateChannel();
            if (!pipeProxy.Heartbeat())
                throw new Exception($"Already running application instance is unresponsive.");

            if (args.Length > 0)
                pipeProxy.ProcessArgs(Process.GetCurrentProcess().Id, args);

            pipeFactory.Close();
        }

        private void StartServiceHost()
        {
            _host = new ServiceHost(this, new[] { new Uri("net.pipe://localhost") });
            var behaviour = _host.Description.Behaviors.Find<ServiceBehaviorAttribute>();
            behaviour.InstanceContextMode = InstanceContextMode.Single;
            _host.AddServiceEndpoint(typeof(ICommandLineProxy), new NetNamedPipeBinding(), _pipeName);
            _host.Open();
        }

        private void OnCommandLineBatchTimerTick(object sender, EventArgs e)
        {
            // we can turn this off now and process any collected files. timer will be started again if we recieve additional cli args
            _notifyTimer.Enabled = false;

            if (_batch.Count > 0)
            {
                Console.WriteLine($"Processing batch of {_batch.Count} cli arguments");
                var args = _batch.ToArray();
                _batch.Clear();
                ArgsReceived?.Invoke(this, new CommandLineEventArgs(args));
            }
        }

        void ICommandLineProxy.ProcessArgs(int pid, string[] args)
        {
            if (args == null || args.Length < 1)
                return;

            var p = Process.GetProcessById(pid);

            Console.WriteLine($"{args.Length} cli args received from external process '{p.ProcessName}' (PID {p.Id}).");

            _notifyTimer.Enabled = false;

            foreach (var f in args)
                _batch.Add(f);

            if (_ready)
                _notifyTimer.Enabled = true;
        }

        bool ICommandLineProxy.Heartbeat()
        {
            return true;
        }

        public void Dispose()
        {
            _notifyTimer.Enabled = false;
            _ready = false;

            if (_mutex != null)
            {
                _mutex.ReleaseMutex();
                _mutex.Dispose();
                _mutex = null;
            }

            if (_host != null)
            {
                _host.Close();
                _host = null;
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RT.Util.ExtensionMethods;

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

    internal sealed class MutexArgsForwarder : IDisposable
    {
        public event EventHandler<CommandLineEventArgs> ArgsReceived;

        private bool _ready;
        private List<string> _batch;
        private System.Timers.Timer _notifyTimer;
        private Mutex _mutex;
        private string _mutexName;

        private HttpListener _host;
        private Thread _hostThread;

        private const string _httpRoot = "http://127.0.0.1:45954/";

        public MutexArgsForwarder(string mutexName)
        {
            _ready = false;
            _batch = new List<string>();
            _notifyTimer = new System.Timers.Timer();
            _notifyTimer.Interval = 1000;
            _notifyTimer.Elapsed += OnCommandLineBatchTimerTick;
            _mutexName = mutexName;
        }

        /// <summary>
        /// If this method returns true, app should continue startup. If false, the args have been forwarded to another already running instance, and you should exit.
        /// </summary>
        public async Task<bool> Startup(string[] args)
        {
            Dispose();

            bool created;
            _mutex = new Mutex(false, _mutexName, out created);
            if (!created)
            {
                if (args != null && args.Length > 0)
                    await SendArgsToRemote(args);

                // Can't call dispose here, we don't own the mutex and Dispose will try to release the mutex
                _mutex.Dispose();
                _mutex = null;

                return false;
            }
            else
            {
                StartServiceHost();
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
            using var http = new ClowdHttpClient();
            http.Timeout = TimeSpan.FromSeconds(3);
            await http.PostJsonAsync<SendArgsRequestModel, object>(new Uri(_httpRoot + "args"), req, true);
        }

        private void StartServiceHost()
        {
            _host = new HttpListener();
            _host.Prefixes.Add(_httpRoot);
            _host.Start();

            _hostThread = new Thread(ListenForHttpRequests);
            _hostThread.IsBackground = true;
            _hostThread.Priority = ThreadPriority.BelowNormal;
            _hostThread.Start();
        }

        private void ListenForHttpRequests()
        {
            while (_host != null && _host.IsListening)
            {
                try
                {
                    var request = _host.GetContext();
                    ThreadPool.QueueUserWorkItem((cb) =>
                    {
                        var context = cb as HttpListenerContext;
                        ProcessHttpRequest(context.Request, context.Response);
                    }, request);
                }
                catch { }
            }
        }

        private void ProcessHttpRequest(HttpListenerRequest request, HttpListenerResponse response)
        {
            try
            {
                response.StatusCode = 204;

                if (request.HttpMethod != "POST")
                {
                    response.StatusCode = 406;
                    return;
                }

                if (!request.Url.AbsolutePath.EqualsIgnoreCase("/args"))
                {
                    response.StatusCode = 404;
                    return;
                }

                if (!(request.ContentType ?? "").Split(';').First().EqualsIgnoreCase("application/json"))
                {
                    response.StatusCode = 415;
                    return;
                }

                SendArgsRequestModel reqBody;

                try
                {
                    var json = request.InputStream.ReadAllText(request.ContentEncoding);
                    reqBody = JsonConvert.DeserializeObject<SendArgsRequestModel>(json);
                    if (reqBody.pid < 1) throw new ArgumentException();
                    if (reqBody.args == null) throw new ArgumentException();
                }
                catch
                {
                    response.StatusCode = 400;
                    return;
                }

                ProcessArgs(reqBody.pid, reqBody.args);
            }
            finally
            {
                response.OutputStream.Close();
            }
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

        private void ProcessArgs(int pid, string[] args)
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

        public void Dispose()
        {
            _notifyTimer.Enabled = false;
            _ready = false;

            if (_mutex != null)
            {
                // I don't know why this fails, but the process is exiting so I also don't care
                try { _mutex.ReleaseMutex(); }
                catch { }

                try { _mutex.Dispose(); }
                catch { }

                _mutex = null;
            }

            if (_host != null)
            {
                _host.Abort();
                _host = null;
            }
        }

        public record SendArgsRequestModel(int pid, string[] args);
    }
}

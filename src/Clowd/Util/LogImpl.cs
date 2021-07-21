using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SharpRaven;
using SharpRaven.Data;
using SharpRaven.Logging;

namespace Clowd.Util
{
    public abstract class ScopedLog<T> : IScopedLog where T : ScopedLog<T>, new()
    {
        public string Name { get; protected set; }
        public DateTime CreatedUtc { get; }
        public DateTime? LastMessageUtc { get; protected set; }
        public ScopedLog<T> ProfilerRoot { get; protected set; }

        private static readonly object _lock = new object();

        private List<ScopedLog<T>> _children = new List<ScopedLog<T>>();

        private List<Breadcrumb> Breadcrumbs = new List<Breadcrumb>();

        protected ScopedLog()
        {
            CreatedUtc = DateTime.UtcNow;
        }

        protected virtual void InitProfile()
        {
            if (ProfilerRoot != null && ProfilerRoot != this)
                ProfilerRoot.RegisterChild(this);
        }

        protected virtual void RegisterChild(ScopedLog<T> child)
        {
            _children.Add(child);
        }

        protected virtual void CacheMsg(LogSeverity severity, string message, Exception ex)
        {
        }

        public IScopedLog CreateScope(string name)
        {
            var child = new T();
            child.Name = name;
            child.ProfilerRoot = ProfilerRoot;
            child.InitProfile();
            return child;
        }

        public IScopedLog CreateProfiledScope(string name)
        {
            var child = new T();
            child.Name = name;
            child.ProfilerRoot = ProfilerRoot ?? child;
            child.InitProfile();
            return child;
        }

        public virtual void Debug(string message)
        {
            LogBase(message, LogSeverity.Debug);
        }

        public virtual void Info(string message)
        {
            LogBase(message, LogSeverity.Info);
        }

        public virtual void Error(string message)
        {
            LogBase(message, LogSeverity.Error);
        }

        public virtual void Error(Exception ex)
        {
            LogBase(ex.ToString(), LogSeverity.Error, ex);
        }

        public virtual void Error(string message, Exception ex)
        {
            LogBase(message + Environment.NewLine + ex.ToString(), LogSeverity.Error, ex);
        }

        protected virtual void LogBase(string message, LogSeverity level, Exception ex = null)
        {
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                LastMessageUtc = now;

                if (ProfilerRoot != null)
                {
                    ProfilerRoot.LastMessageUtc = now;
                    ProfilerRoot.CacheMsg(level, Name + " - " + message, ex);
                }

                foreach (var line in message.Split(new char[] { '\n', '\r' }))
                    if (!String.IsNullOrWhiteSpace(line))
                        WriteLine(FormatLine(line, level), level);
            }
        }

        protected virtual string FormatLine(string message, LogSeverity level)
        {
            var levelMsg = level switch
            {
                LogSeverity.Debug => "DEBG",
                LogSeverity.Info => "INFO",
                LogSeverity.Error => "ERRO",
                _ => throw new ArgumentOutOfRangeException(nameof(level)),
            };

            var ms = 0;
            if (ProfilerRoot != null)
                ms = (int)(LastMessageUtc.Value - ProfilerRoot.CreatedUtc).TotalMilliseconds;

            return $"[{LastMessageUtc.Value.ToLongTimeString()}][{levelMsg}][{Name}]{(ms > 0 ? $" +{ms}ms" : "")} - {message}";
        }

        protected abstract void WriteLine(string message, LogSeverity level);

        public void Dispose()
        {
            if (ProfilerRoot == this && LastMessageUtc.HasValue)
            {
                if (_children.Any())
                {
                    WriteLine("", LogSeverity.Info);
                    WriteLine($"{Name} Profiler, Total time: {((int)(LastMessageUtc.Value - CreatedUtc).TotalMilliseconds)}ms, Summary:", LogSeverity.Info);
                    foreach (var key in _children)
                        if (key.LastMessageUtc.HasValue)
                            WriteLine($"  {key.Name} - {((int)(key.LastMessageUtc.Value - key.CreatedUtc).TotalMilliseconds)}ms", LogSeverity.Info);
                    WriteLine("", LogSeverity.Info);
                }
                else
                {
                    WriteLine("", LogSeverity.Info);
                    WriteLine($"{Name} Profiler, Total time: {((int)(LastMessageUtc.Value - CreatedUtc).TotalMilliseconds)}ms", LogSeverity.Info);
                    WriteLine("", LogSeverity.Info);
                }
            }
        }

        public void RunProfiled(string name, Action<IScopedLog> func)
        {
            using (var scope = CreateProfiledScope(name))
            {
                try
                {
                    scope.Info("Starting: " + name);
                    func(scope);
                    scope.Info("Completed: " + name);
                }
                catch (Exception ex)
                {
                    scope.Error(ex);
                    throw;
                }
            }
        }

        public T RunProfiled<T>(string name, Func<IScopedLog, T> func)
        {
            using (var scope = CreateProfiledScope(name))
            {
                try
                {
                    scope.Info("Starting: " + name);
                    var ret = func(scope);
                    scope.Info("Completed: " + name);
                    return ret;
                }
                catch (Exception ex)
                {
                    scope.Error(ex);
                    throw;
                }
            }
        }

        public async Task RunProfiledAsync(string name, Func<IScopedLog, Task> func)
        {
            using (var scope = CreateProfiledScope(name))
            {
                try
                {
                    scope.Info("Starting: " + name);
                    await func(scope);
                    scope.Info("Completed: " + name);
                }
                catch (Exception ex)
                {
                    scope.Error(ex);
                    throw;
                }
            }
        }

        public async Task<T> RunProfiledAsync<T>(string name, Func<IScopedLog, Task<T>> func)
        {
            using (var scope = CreateProfiledScope(name))
            {
                try
                {
                    scope.Info("Starting: " + name);
                    var ret = await func(scope);
                    scope.Info("Completed: " + name);
                    return ret;
                }
                catch (Exception ex)
                {
                    scope.Error(ex);
                    throw;
                }
            }
        }
    }

    public class DefaultScopedLog : ScopedLog<DefaultScopedLog>
    {
        public LogSeverity MinLogLevel { get; set; } = LogSeverity.Info;

        public static IRavenClient Sentry { get; private set; }
        private List<Breadcrumb> _breadcrumbs = new List<Breadcrumb>();

        [Obsolete("This constructor is used for generic instantiation : new() and should not be used in code")]
        public DefaultScopedLog()
        {
        }

        public DefaultScopedLog(string name)
        {
            Name = name;
        }

        public static void EnableSentry(string key)
        {
            Sentry = new RavenClient(key);
        }

        protected override void CacheMsg(LogSeverity level, string message, Exception ex)
        {
            if (level == LogSeverity.Error && Sentry != null)
            {
                SentryEvent evt = ex == null ? new SentryEvent(new SentryMessage(message)) : new SentryEvent(ex);
                evt.Breadcrumbs = _breadcrumbs;
                Sentry.Capture(evt);
            }
            else
            {
                if (level >= MinLogLevel)
                    _breadcrumbs.Add(new Breadcrumb(level.ToString()) { Message = message });
            }
        }

        protected override void LogBase(string message, LogSeverity level, Exception ex = null)
        {
            if (level == LogSeverity.Error && ProfilerRoot == null && Sentry != null)
            {
                SentryEvent evt = ex == null ? new SentryEvent(new SentryMessage(message)) : new SentryEvent(ex);
                Sentry.Capture(evt);
            }
            base.LogBase(message, level, ex);
        }

        protected override void WriteLine(string message, LogSeverity level)
        {
            if (level >= MinLogLevel)
                Console.WriteLine(message);
        }
    }

    public interface IExtenedRavenClient : IRavenClient
    {
        bool Enabled { get; set; }

        string SubmitLog(string message, ErrorLevel level = ErrorLevel.Info);
    }

    public static class SentryImpl
    {
        public static IExtenedRavenClient Default { get; private set; }

        public static void Init(string key)
        {
            Default = new MockRaven(new RavenClient(key));
        }

        private class MockRaven : IExtenedRavenClient
        {
            private readonly RavenClient _raven;

            public MockRaven(RavenClient raven)
            {
                _raven = raven;
                Enabled = true;
            }

            public void AddTrail(Breadcrumb breadcrumb)
            {
                if (this.IsDisabled())
                    return;
                ((IRavenClient)_raven).AddTrail(breadcrumb);
            }

            public string Capture(SentryEvent @event)
            {
                if (this.IsDisabled())
                    return null;
                return ((IRavenClient)_raven).Capture(@event);
            }

            public string CaptureException(Exception exception, SentryMessage message = null, ErrorLevel level = ErrorLevel.Error,
                IDictionary<string, string> tags = null, string[] fingerprint = null, object extra = null)
            {
                throw new NotImplementedException();
            }

            public string CaptureMessage(SentryMessage message, ErrorLevel level = ErrorLevel.Info, IDictionary<string, string> tags = null,
                string[] fingerprint = null, object extra = null)
            {
                throw new NotImplementedException();
            }

            public void RestartTrails()
            {
                if (this.IsDisabled())
                    return;
                ((IRavenClient)_raven).RestartTrails();
            }

            public Task<string> CaptureAsync(SentryEvent @event)
            {
                if (this.IsDisabled())
                    return Task.FromResult<string>(null);
                return ((IRavenClient)_raven).CaptureAsync(@event);
            }

            public Task<string> CaptureExceptionAsync(Exception exception, SentryMessage message = null, ErrorLevel level = ErrorLevel.Error,
                IDictionary<string, string> tags = null, string[] fingerprint = null, object extra = null)
            {
                throw new NotImplementedException();
            }

            public Task<string> CaptureMessageAsync(SentryMessage message, ErrorLevel level = ErrorLevel.Info, IDictionary<string, string> tags = null,
                string[] fingerprint = null, object extra = null)
            {
                throw new NotImplementedException();
            }

            public string CaptureEvent(Exception e)
            {
                throw new NotImplementedException();
            }

            public string CaptureEvent(Exception e, Dictionary<string, string> tags)
            {
                throw new NotImplementedException();
            }

            private bool IsDisabled()
            {
                return !this.Enabled;
            }

            public Func<Requester, Requester> BeforeSend
            {
                get { return _raven.BeforeSend; }
                set { _raven.BeforeSend = value; }
            }

            public bool Compression
            {
                get { return _raven.Compression; }
                set { _raven.Compression = value; }
            }

            public Dsn CurrentDsn
            {
                get { return _raven.CurrentDsn; }
            }

            public string Environment
            {
                get { return _raven.Environment; }
                set { _raven.Environment = value; }
            }

            public bool IgnoreBreadcrumbs
            {
                get { return _raven.IgnoreBreadcrumbs; }
                set { _raven.IgnoreBreadcrumbs = value; }
            }

            public string Logger
            {
                get { return _raven.Logger; }
                set { _raven.Logger = value; }
            }

            public IScrubber LogScrubber
            {
                get { return _raven.LogScrubber; }
                set { _raven.LogScrubber = value; }
            }

            public string Release
            {
                get { return _raven.Release; }
                set { _raven.Release = value; }
            }

            public IDictionary<string, string> Tags
            {
                get { return _raven.Tags; }
            }

            public TimeSpan Timeout
            {
                get { return _raven.Timeout; }
                set { _raven.Timeout = value; }
            }

            public bool Enabled { get; set; }
            public string SubmitLog(string message, ErrorLevel level = ErrorLevel.Info)
            {
                if (IsDisabled())
                    return null;

                var evt = new SentryEvent(message);
                evt.Level = level;
                return Capture(evt);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Clowd
{
    public interface ILog
    {
        void Debug(string message);
        void Info(string message);
        void Error(string message);
        void Error(string message, Exception ex);
        void Error(Exception ex);
    }

    public interface IScopedLog : ILog, IDisposable
    {
        IScopedLog CreateScope(string name);

        IScopedLog CreateProfiledScope(string name);

        //void AddTrackedLogFile(string path);
        //void WriteToFile(string directory);
    }

    public abstract class ScopedLog<T> : IScopedLog where T : ScopedLog<T>, new()
    {
        public string Name { get; protected set; }
        public DateTime CreatedUtc { get; }
        public DateTime? LastMessageUtc { get; protected set; }
        public ScopedLog<T> ProfilerRoot { get; protected set; }

        private static readonly object _lock = new object();

        private List<ScopedLog<T>> children = new List<ScopedLog<T>>();

        protected ScopedLog()
        {
            CreatedUtc = DateTime.UtcNow;
        }

        //public ScopedLog(ScopedLog parent, bool profileOnDispose)
        //{
        //    this.parent = parent;
        //    ProfileOnDispose = profileOnDispose;
        //}

        protected void InitProfile()
        {
            if (ProfilerRoot != null)
                ProfilerRoot.RegisterChild(this);
        }

        protected void RegisterChild(ScopedLog<T> child)
        {
            children.Add(child);
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
            LogBase(ex.ToString(), LogSeverity.Error);
        }

        public virtual void Error(string message, Exception ex)
        {
            LogBase(message, LogSeverity.Error);
            LogBase(ex.ToString(), LogSeverity.Error);
        }

        protected virtual void LogBase(string message, LogSeverity level)
        {
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                LastMessageUtc = now;

                if (ProfilerRoot != null)
                    ProfilerRoot.LastMessageUtc = now;

                foreach (var line in message.Split('\n'))
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
                WriteLine("", LogSeverity.Info);
                WriteLine($"{Name} Profiler, Total time: {((int)(LastMessageUtc.Value - CreatedUtc).TotalMilliseconds)}ms, Summary:", LogSeverity.Info);
                foreach (var key in children)
                    if (key.LastMessageUtc.HasValue)
                        WriteLine($"  {key.Name} - {((int)(key.LastMessageUtc.Value - key.CreatedUtc).TotalMilliseconds)}ms", LogSeverity.Info);
                WriteLine("", LogSeverity.Info);
            }
        }
    }

    public enum LogSeverity
    {
        Debug = 1,
        Info = 2,
        Error = 3,
    }

    public class ConsoleScopedLog : ScopedLog<ConsoleScopedLog>
    {
        public LogSeverity MinLogLevel { get; set; } = LogSeverity.Info;

        public ConsoleScopedLog()
        {
        }

        public ConsoleScopedLog(string name)
        {
            Name = name;
        }

        protected override void WriteLine(string message, LogSeverity level)
        {
            if (level >= MinLogLevel)
                Console.WriteLine(message);
        }
    }
}

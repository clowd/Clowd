using System;
using NLog;

namespace Clowd
{
    public sealed class ProfiledLogger : IDisposable
    {
        public string Name { get; }
        public DateTime? CreatedUtc { get; private set; }

        private readonly ILogger _log;

        public ProfiledLogger(string name)
        {
            Name = name;
            _log = LogManager.GetLogger(name);
            Info("Profiler Created: " + Name);
            CreatedUtc = DateTime.UtcNow;
        }

        public void Info(string message)
        {
            Log(LogLevel.Info, message);
        }

        public void Debug(string message)
        {
            Log(LogLevel.Debug, message);
        }

        public void Warn(string message)
        {
            Log(LogLevel.Warn, message);
        }

        public void Error(string message)
        {
            Log(LogLevel.Error, message);
        }

        public void Error(Exception ex, string message)
        {
            Log(LogLevel.Error, message, ex);
        }

        public void Fatal(string message)
        {
            Log(LogLevel.Fatal, message);
        }

        private void Log(LogLevel level, string message, Exception ex = null)
        {
            if (CreatedUtc.HasValue)
            {
                var ms = (DateTime.UtcNow - CreatedUtc.Value).TotalMilliseconds;
                message = $" +{ms}ms - " + message;
            }

            if (ex != null)
            {
                _log.Log(level, ex, message);
            }
            else
            {
                _log.Log(level, message);
            }
        }

        public void Dispose()
        {
            if (CreatedUtc == null)
                return;

            var ms = (DateTime.UtcNow - CreatedUtc.Value).TotalMilliseconds;
            Info($"Profiler Complete: {Name}, Total Time: {ms}ms.");
            CreatedUtc = null;
        }
    }

    public static class ProfiledLoggerExtensions
    {
        public static ProfiledLogger CreateProfiledScope(this ILogger logger, string name)
        {
            return new ProfiledLogger(name);
        }
    }
}

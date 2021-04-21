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

        void RunProfiled(string name, Action<IScopedLog> func);
        T RunProfiled<T>(string name, Func<IScopedLog, T> func);
        Task RunProfiledAsync(string name, Func<IScopedLog, Task> func);
        Task<T> RunProfiledAsync<T>(string name, Func<IScopedLog, Task<T>> func);



        //void AddTrackedLogFile(string path);
        //void WriteToFile(string directory);
    }



    public enum LogSeverity
    {
        Debug = 1,
        Info = 2,
        Error = 3,
    }
}

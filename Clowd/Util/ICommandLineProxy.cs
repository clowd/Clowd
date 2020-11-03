using System;
using System.ServiceModel;

namespace Clowd.Util
{
    [ServiceContract]
    public interface ICommandLineProxy
    {
        [OperationContract]
        void PassArgs(string[] args);

        [OperationContract]
        bool Heartbeat();
    }
    public class CommandLineProxy : ICommandLineProxy
    {
        public event EventHandler<CommandLineEventArgs> CommandLineExecutedEvent;
        public void PassArgs(string[] args)
        {
            CommandLineExecutedEvent?.Invoke(this, new CommandLineEventArgs(args));
        }

        public bool Heartbeat()
        {
            return true;
        }
    }
    public class CommandLineEventArgs : EventArgs
    {
        public string[] Args { get; private set; }
        public CommandLineEventArgs(string[] args)
        {
            Args = args;
        }
    }
}

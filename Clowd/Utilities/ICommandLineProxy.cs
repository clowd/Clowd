using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using System.Text;
using System.Threading.Tasks;

namespace Clowd.Utilities
{
    [ServiceContract]
    public interface ICommandLineProxy
    {
        [OperationContract]
        void PassArgs(string[] args);
    }
    public class CommandLineProxy : ICommandLineProxy
    {
        public event EventHandler<CommandLineEventArgs> CommandLineExecutedEvent;
        public void PassArgs(string[] args)
        {
            CommandLineExecutedEvent?.Invoke(this, new CommandLineEventArgs(args));
        }
    }
    public class CommandLineEventArgs  :EventArgs
    {
        public string[] Args { get; private set; }
        public CommandLineEventArgs(string[] args)
        {
            Args = args;
        }
    }
}
